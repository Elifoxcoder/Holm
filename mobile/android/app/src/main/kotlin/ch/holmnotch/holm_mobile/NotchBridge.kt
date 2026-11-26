package ch.holmnotch.holm_mobile

import android.content.ComponentName
import android.Manifest
import android.app.KeyguardManager
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.display.DisplayManager
import android.view.Display
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import io.flutter.FlutterInjector
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.embedding.engine.FlutterEngineCache
import io.flutter.embedding.engine.FlutterEngineGroup
import io.flutter.embedding.engine.dart.DartExecutor
import io.flutter.plugin.common.MethodChannel

/**
 * Connects the three parts of the Dynamic Notch: the app's Flutter engine (Holm: chat, Beam, settings), the notch's own
 * engine drawn in the accessibility overlay ([NotchService]) and the system sources ([NotchListener], the service's
 * receivers). Everything runs on the main thread.
 */
object NotchBridge {
    const val MAIN_ENGINE = "holm_main"
    private const val PREFS = "holm_notch"

    private var group: FlutterEngineGroup? = null
    private var appContext: Context? = null
    val handler = Handler(Looper.getMainLooper())

    // While the device is locked the overlay is hidden as well as these events being withheld from its engine.
    private val safeLockEvents = setOf("battery", "volume", "orientation", "torch", "dnd", "wifi", "vpn", "outside", "pcQuery")
    internal val lockedNotchMethods = setOf(
        "media", "open", "openApp", "notificationAction", "shade", "reply", "clear", "call", "muteCall", "keyboard",
        "gesture", "phoneStatus", "beamScreenshot", "overview", "offerAnswer", "mute", "setVolume", "volumes",
    )
    private val lockedMainMethods = setOf(
        "notchSettings", "holmEvent", "dismissNotification", "dismissAllNotifications", "takeShared", "saveToDownloads", "copyUri", "apps",
        "notifyUpdate", "installUpdate", "shizukuUnlock", "hidePopups", "open", "beamFile", "offerAnswer",
        "phoneStatus", "mute", "media",
    )

    /** The notch's Dart side, once it said it's ready. */
    var notch: MethodChannel? = null
        set(value) {
            field = value
            if (value != null) {
                val locked = appContext?.let { isDeviceLocked(it) } == true
                while (pending.isNotEmpty()) {
                    val event = pending.removeFirst()
                    val type = event["type"] as? String
                    if (locked && type !in safeLockEvents) continue
                    value.invokeMethod("event", event)
                }
            }
        }

    /** The app's Dart side (Holm). */
    var main: MethodChannel? = null
    var service: NotchService? = null
    var listener: NotchListener? = null
    /** Shared from Android's share menu, until the app takes it ("takeShared"). */
    var shared: Map<String, Any?>? = null
    private val pending = ArrayDeque<Map<String, Any?>>()

    fun group(context: Context): FlutterEngineGroup {
        appContext = context.applicationContext
        return group ?: FlutterEngineGroup(context.applicationContext).also { group = it }
    }

    /**
     * The app's engine, made once and kept: the activity shows it while open, and Holm keeps following its inbox and
     * the nearby devices while the accessibility service keeps the process alive.
     */
    fun mainEngine(context: Context): FlutterEngine {
        FlutterEngineCache.getInstance().get(MAIN_ENGINE)?.let { return it }
        val engine = group(context).createAndRunDefaultEngine(context.applicationContext)
        FlutterEngineCache.getInstance().put(MAIN_ENGINE, engine)
        val channel = MethodChannel(engine.dartExecutor.binaryMessenger, "holm/bridge")
        channel.setMethodCallHandler { call, result -> onMainCall(context.applicationContext, call.method, call.arguments, result) }
        main = channel
        return engine
    }

    fun notchEngine(context: Context): FlutterEngine {
        val bundle = FlutterInjector.instance().flutterLoader().findAppBundlePath()
        return group(context).createAndRunEngine(context, DartExecutor.DartEntrypoint(bundle, "notchMain"))
    }

    /** An event for the notch; kept (the last 60) until the notch is ready. */
    fun toNotch(event: Map<String, Any?>) {
        handler.post {
            val locked = appContext?.let { isDeviceLocked(it) } == true
            val type = event["type"] as? String
            if (locked) {
                pending.clear() // nothing queued before the keyguard returned may flash after it
                if (type !in safeLockEvents) return@post
            }
            val channel = notch
            if (channel != null) channel.invokeMethod("event", event)
            else {
                if (pending.size >= 60) pending.removeFirst()
                pending.addLast(event)
            }
        }
    }

    /** Drop events queued before the keyguard returned, so they cannot flash after an unlock. */
    fun clearPending() = pending.clear()

    /** Battery/charging status is non-private and safe to forward while the phone is locked (stripping song/detail). */
    fun safeLockedPhoneStatus(args: Any?): Map<String, Any?>? {
        val m = args as? Map<*, *> ?: return null
        val kind = m["kind"] as? String ?: return null
        if (kind != "status" && kind != "battery") return null
        val out = mutableMapOf<String, Any?>(
            "kind" to kind,
            "title" to m["title"],
        )
        if (kind == "battery") out["detail"] = m["detail"]
        if (m.containsKey("on")) out["on"] = m["on"]
        if (m.containsKey("since")) out["since"] = m["since"]
        return out
    }

    fun toMain(method: String, args: Any?) = handler.post {
        val locked = appContext?.let { isDeviceLocked(it) } == true
        if (locked && method in lockedMainMethods) {
            if (method == "phoneStatus") {
                val safe = safeLockedPhoneStatus(args) ?: return@post
                main?.invokeMethod(method, safe)
            }
            return@post
        }
        main?.invokeMethod(method, args)
    }

    fun settings(context: Context): String? =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString("settings", null)

    /** Locked or with the screen off: neither state may expose private notch data or accept private actions. */
    fun isDeviceLocked(context: Context): Boolean {
        val keyguard = context.getSystemService(KeyguardManager::class.java)?.isDeviceLocked == true
        val screenOff = context.getSystemService(android.os.PowerManager::class.java)?.isInteractive == false
        return keyguard || screenOff
    }

    private fun onMainCall(context: Context, method: String, args: Any?, result: MethodChannel.Result) {
        if (isDeviceLocked(context) && method in lockedMainMethods) {
            result.error("device_locked", "Unlock the device before using Holm", null)
            return
        }
        when (method) {
            "notchSettings" -> {
                val json = args as String
                context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString("settings", json).apply()
                toNotch(mapOf("type" to "settings", "json" to json))
                result.success(null)
            }
            "holmEvent" -> {
                @Suppress("UNCHECKED_CAST")
                toNotch(args as Map<String, Any?>)
                result.success(null)
            }
            "status" -> result.success(
                mapOf(
                    "overlay" to isAccessibilityOn(context),
                    "running" to (service != null),
                    "notifications" to isListenerOn(context),
                    "popupsAllowed" to canHidePopups(context),
                    "popupsHidden" to wantsPopupsHidden(context),
                    "camera" to camera(context),
                )
            )
            "batteryStatus" -> {
                val intent = context.registerReceiver(null, android.content.IntentFilter(android.content.Intent.ACTION_BATTERY_CHANGED))
                val level = intent?.getIntExtra(android.os.BatteryManager.EXTRA_LEVEL, -1) ?: -1
                val scale = intent?.getIntExtra(android.os.BatteryManager.EXTRA_SCALE, 100) ?: 100
                val status = intent?.getIntExtra(android.os.BatteryManager.EXTRA_STATUS, 0) ?: 0
                val plugged = (intent?.getIntExtra(android.os.BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0 ||
                    status == android.os.BatteryManager.BATTERY_STATUS_CHARGING ||
                    status == android.os.BatteryManager.BATTERY_STATUS_FULL
                if (service != null) toNotch(mapOf("type" to "pcQuery"))
                result.success(
                    mapOf(
                        "level" to if (level < 0 || scale <= 0) null else level * 100 / scale,
                        "plugged" to plugged,
                        "notchRunning" to (service != null),
                    )
                )
            }
            "media" -> {
                (args as? String)?.let { listener?.mediaAction(it, null) }
                result.success(null)
            }
            "dismissNotification" -> {
                (args as? String)?.let { listener?.clear(it) }
                result.success(null)
            }
            "dismissAllNotifications" -> {
                listener?.clearAll()
                result.success(null)
            }
            "takeShared" -> {
                result.success(shared)
                shared = null
            }
            "saveToDownloads" -> Thread {
                val ok = runCatching {
                    if (isDeviceLocked(context)) return@runCatching false
                    val file = java.io.File(args as String)
                    val values = android.content.ContentValues().apply {
                        put(android.provider.MediaStore.Downloads.DISPLAY_NAME, file.name)
                        put(android.provider.MediaStore.Downloads.RELATIVE_PATH, android.os.Environment.DIRECTORY_DOWNLOADS + "/HolyNotch")
                        put(android.provider.MediaStore.Downloads.IS_PENDING, 1)
                    }
                    val resolver = context.contentResolver
                    val uri = resolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)!!
                    resolver.openOutputStream(uri)!!.use { out -> file.inputStream().use { it.copyTo(out, 256 * 1024) } }
                    values.clear()
                    values.put(android.provider.MediaStore.Downloads.IS_PENDING, 0)
                    resolver.update(uri, values, null, null)
                    true
                }.getOrDefault(false)
                handler.post { result.success(if (isDeviceLocked(context)) false else ok) }
            }.start()
            "copyUri" -> {
                val uri = runCatching { android.net.Uri.parse(args as String) }.getOrNull()
                if (uri == null || !UriSecurity.isAllowedContentUri(context, uri)) {
                    result.success(null)
                } else Thread {
                    val path = runCatching {
                        // Revalidate at the point of use; no file:// or Holm-owned provider is ever opened.
                        if (isDeviceLocked(context) || !UriSecurity.isAllowedContentUri(context, uri)) return@runCatching null
                        val name = context.contentResolver.query(uri, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)
                            ?.use { c -> if (c.moveToFirst()) c.getString(0) else null } ?: "Screenshot.png"
                        val dir = java.io.File(context.cacheDir, "beam").apply { mkdirs() }
                        val out = java.io.File(dir, name.replace(Regex("[\\\\/:*?\"<>|]"), "_"))
                        context.contentResolver.openInputStream(uri)!!.use { input -> out.outputStream().use { input.copyTo(it) } }
                        out.path
                    }.getOrNull()
                    handler.post { result.success(if (isDeviceLocked(context)) null else path) }
                }.start()
            }
            "apps" -> Thread {
                val pm = context.packageManager
                val launcher = android.content.Intent(android.content.Intent.ACTION_MAIN).addCategory(android.content.Intent.CATEGORY_LAUNCHER)
                val list = pm.queryIntentActivities(launcher, 0).map { it.activityInfo.applicationInfo }.distinctBy { it.packageName }
                    .filter { it.packageName != context.packageName }
                    .map { info ->
                        mapOf(
                            "package" to info.packageName,
                            "name" to pm.getApplicationLabel(info).toString(),
                            "icon" to runCatching { NotchListener.png(pm.getApplicationIcon(info), 64) }.getOrNull(),
                        )
                    }.sortedBy { (it["name"] as String).lowercase() }
                handler.post { result.success(if (isDeviceLocked(context)) emptyList<Map<String, Any?>>() else list) }
            }.start()
            "appVersion" -> result.success(Updates.currentVersion(context).toInt())
            "appVersionName" -> result.success(context.packageManager.getPackageInfo(context.packageName, 0).versionName)
            "notifyUpdate" -> {
                val m = args as Map<*, *>
                Updates.notify(context, (m["code"] as Number).toLong(), m["name"] as String)
                result.success(null)
            }
            "installUpdate" -> result.success(runCatching { Updates.install(context, args as String) }.getOrDefault("failed"))
            "shizuku" -> result.success(ShizukuGrant.status(context))
            "shizukuUnlock" -> ShizukuGrant.unlock(context) { answer ->
                handler.post {
                    if (answer == "granted") applyPopups(context, service != null)
                    result.success(answer)
                }
            }
            "hidePopups" -> {
                context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("hidePopups", args == true).apply()
                applyPopups(context, service != null)
                result.success(canHidePopups(context))
            }
            else -> result.notImplemented()
        }
    }

    // ───────────── Android's own pop-ups ─────────────

    private const val HEADS_UP = "heads_up_notifications_enabled"

    fun canHidePopups(context: Context) =
        context.checkSelfPermission(Manifest.permission.WRITE_SECURE_SETTINGS) == PackageManager.PERMISSION_GRANTED

    fun wantsPopupsHidden(context: Context) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean("hidePopups", false)

    /**
     * Android's pop-ups off while the notch runs and the person asked for it; back on otherwise. Only what Holm itself
     * switched off is switched back on, so a choice made in Android's settings stays.
     */
    fun applyPopups(context: Context, notchRunning: Boolean) {
        if (!canHidePopups(context)) return
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val resolver = context.contentResolver
        val hide = notchRunning && wantsPopupsHidden(context)
        runCatching {
            if (hide && Settings.Global.getInt(resolver, HEADS_UP, 1) != 0) {
                Settings.Global.putInt(resolver, HEADS_UP, 0)
                prefs.edit().putBoolean("popupsOffByHolm", true).apply()
            } else if (!hide && prefs.getBoolean("popupsOffByHolm", false)) {
                Settings.Global.putInt(resolver, HEADS_UP, 1)
                prefs.edit().putBoolean("popupsOffByHolm", false).apply()
            }
        }
    }

    /**
     * Where the front camera's cut-out is, in dp: its centre from the screen's middle (x) and top (y), and its size,
     * so the closed notch can be fitted over it. Null on a phone without one.
     */
    fun camera(context: Context): Map<String, Double>? {
        val display = context.getSystemService(DisplayManager::class.java)?.getDisplay(Display.DEFAULT_DISPLAY) ?: return null
        val cutout = display.cutout ?: return null
        // The camera hole itself (its path), not the bounding band, which reaches down the whole status bar.
        val rect = cutout.cutoutPath?.let { path ->
            val bounds = android.graphics.RectF()
            path.computeBounds(bounds, true)
            bounds.takeIf { !it.isEmpty && it.top < it.bottom && it.centerY() < context.resources.displayMetrics.heightPixels / 3 }
                ?.let { android.graphics.Rect(it.left.toInt(), it.top.toInt(), it.right.toInt(), it.bottom.toInt()) }
        } ?: cutout.boundingRectTop?.takeIf { !it.isEmpty } ?: return null
        val d = context.resources.displayMetrics.density.toDouble()
        val screen = context.resources.displayMetrics.widthPixels
        return mapOf(
            "x" to (rect.exactCenterX() - screen / 2.0) / d,
            "y" to rect.exactCenterY() / d,
            "w" to rect.width() / d,
            "h" to rect.height() / d,
        )
    }

    fun isAccessibilityOn(context: Context): Boolean {
        val enabled = Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
        val me = ComponentName(context, NotchService::class.java)
        return enabled.split(':').any { ComponentName.unflattenFromString(it) == me }
    }

    fun isListenerOn(context: Context): Boolean {
        val enabled = Settings.Secure.getString(context.contentResolver, "enabled_notification_listeners") ?: return false
        val me = ComponentName(context, NotchListener::class.java)
        return enabled.split(':').any { ComponentName.unflattenFromString(it) == me }
    }
}
