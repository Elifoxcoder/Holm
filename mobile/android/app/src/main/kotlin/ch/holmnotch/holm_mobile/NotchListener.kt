package ch.holmnotch.holm_mobile

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.RemoteInput
import android.content.Intent
import android.os.Bundle
import android.os.CancellationSignal
import android.content.ComponentName
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.drawable.Drawable
import android.net.Uri
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.Locale

/**
 * What the notch hears from the system's notifications: new ones, ongoing ones (downloads, timers, Live Updates, Quick
 * Share) and the media session that plays (Spotify or any player), with its controls. Classifying is up to the Dart side
 * (lib/notch/activities.dart); this only describes.
 */
class NotchListener : NotificationListenerService() {
    private var sessions: MediaSessionManager? = null
    private var controller: MediaController? = null
    private val iconsSent = mutableSetOf<String>()
    private var lastArtKey: String? = null
    /** Covers loaded from a player's link (Spotify gives one instead of a picture), by song. */
    private val artFromUri = object : LinkedHashMap<String, ByteArray>(8, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, ByteArray>?) = size > 8
    }
    private var artLoad: ArtLoad? = null
    private var artLoadingKey: String? = null

    private class ArtLoad {
        @Volatile var cancelled = false
        @Volatile var finished = false
        @Volatile var connection: HttpURLConnection? = null
        @Volatile var input: InputStream? = null
        @Volatile var signal: CancellationSignal? = null
        var thread: Thread? = null
        var timeout: Runnable? = null

        fun cancel() {
            if (cancelled) return
            cancelled = true
            thread?.interrupt()
            runCatching { signal?.cancel() }
            runCatching { connection?.disconnect() }
            runCatching { input?.close() }
        }

        fun checkActive() {
            if (cancelled) throw java.io.IOException("Artwork load cancelled")
        }
    }

    /** The song whose cover is still missing, and how often it was looked for again. */
    private var artMissingKey: String? = null
    private var artRetries = 0
    private val me get() = ComponentName(this, NotchListener::class.java)

    private val onSessions = MediaSessionManager.OnActiveSessionsChangedListener { list -> pick(list ?: emptyList()) }

    private val onMedia = object : MediaController.Callback() {
        override fun onMetadataChanged(metadata: MediaMetadata?) = sendMedia()
        override fun onPlaybackStateChanged(state: PlaybackState?) {
            // Another session may have started playing meanwhile: follow the one that plays.
            sessions?.let { pick(it.getActiveSessions(me)) } ?: sendMedia()
        }
        override fun onSessionDestroyed() {
            sessions?.let { pick(it.getActiveSessions(me)) }
        }
    }

    override fun onListenerConnected() {
        super.onListenerConnected()
        NotchBridge.listener = this
        val msm = getSystemService(MediaSessionManager::class.java)
        sessions = msm
        runCatching {
            msm.addOnActiveSessionsChangedListener(onSessions, me)
            pick(msm.getActiveSessions(me))
        }
        resendAll()
    }

    override fun onInterruptionFilterChanged(interruptionFilter: Int) = sendDnd()

    private fun sendDnd() {
        val filter = runCatching { currentInterruptionFilter }.getOrDefault(INTERRUPTION_FILTER_ALL)
        NotchBridge.toNotch(mapOf("type" to "dnd", "on" to (filter != INTERRUPTION_FILTER_ALL && filter != INTERRUPTION_FILTER_UNKNOWN)))
    }

    override fun onListenerDisconnected() {
        cancelArt()
        artFromUri.clear()
        sessions?.removeOnActiveSessionsChangedListener(onSessions)
        controller?.unregisterCallback(onMedia)
        controller = null
        if (NotchBridge.listener === this) NotchBridge.listener = null
        super.onListenerDisconnected()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        send(sbn, initial = false)
        // A player's notification may bring the cover its session lacks.
        // (Also the first sign of a new player: look at the sessions again.)
        if (sbn.notification.extras.containsKey(Notification.EXTRA_MEDIA_SESSION)) {
            sessions?.let { pick(runCatching { it.getActiveSessions(me) }.getOrDefault(emptyList())) } ?: sendMedia()
        }
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        NotchBridge.toNotch(mapOf("type" to "notificationRemoved", "key" to sbn.key))
    }

    /** Everything as it is now, without popping anything out (the notch just started). */
    fun resendAll() {
        runCatching { activeNotifications }.getOrNull()?.forEach { send(it, initial = true) }
        lastArtKey = null
        sendMedia()
        sendDnd()
    }

    fun hasNotificationFrom(pkg: String) = runCatching { activeNotifications.any { it.packageName == pkg } }.getOrDefault(false)

    /** Sends a PendingIntent with background-activity-start permission on Android 14+. */
    private fun sendPendingIntent(intent: PendingIntent, fillIn: Intent? = null) {
        if (android.os.Build.VERSION.SDK_INT >= 34) {
            val options = android.app.ActivityOptions.makeBasic()
                .setPendingIntentBackgroundActivityStartMode(
                    android.app.ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED
                ).toBundle()
            if (fillIn != null) intent.send(this, 0, fillIn, null, null, null, options)
            else intent.send(this, 0, null, null, null, null, options)
        } else {
            if (fillIn != null) intent.send(this, 0, fillIn) else intent.send()
        }
    }

    /** Presses one of the notification's buttons. */
    fun fireAction(key: String, index: Int) {
        if (NotchBridge.isDeviceLocked(this)) return
        val action = runCatching { activeNotifications.firstOrNull { it.key == key }?.notification?.actions?.getOrNull(index) }.getOrNull()
        val pi = action?.actionIntent ?: return
        if (!NotchBridge.isDeviceLocked(this)) {
            runCatching { sendPendingIntent(pi) }
                .onFailure { android.util.Log.w("HolmNotch", "fireAction $index failed", it) }
        }
    }

    /** Answers a message from the notch: the text goes to the app as if typed in its notification. */
    fun reply(key: String, index: Int, text: String): Boolean = runCatching {
        if (NotchBridge.isDeviceLocked(this)) return false
        val action = activeNotifications.firstOrNull { it.key == key }?.notification?.actions?.getOrNull(index) ?: return false
        val inputs = action.remoteInputs ?: return false
        val intent = Intent()
        RemoteInput.addResultsToIntent(inputs, intent, Bundle().apply { putCharSequence(inputs.first().resultKey, text) })
        if (NotchBridge.isDeviceLocked(this)) false else {
            sendPendingIntent(action.actionIntent, intent)
            true
        }
    }.getOrDefault(false)

    /** Clears a notification from the shade too (a swipe to the right in the notch), if it can be cleared. */
    fun clear(key: String) {
        if (NotchBridge.isDeviceLocked(this)) return
        runCatching {
            if (!NotchBridge.isDeviceLocked(this) && activeNotifications.any { it.key == key && it.isClearable }) cancelNotification(key)
        }
    }

    /** Clears all clearable notifications from the shade (asked from a PC). */
    fun clearAll() {
        if (NotchBridge.isDeviceLocked(this)) return
        runCatching {
            if (!NotchBridge.isDeviceLocked(this)) {
                activeNotifications.filter { it.isClearable }.forEach { cancelNotification(it.key) }
            }
        }
    }

    /** Answer, decline or hang up a call, with the phone app's call notification or TelecomManager. */
    fun callAction(key: String, which: String) {
        if (NotchBridge.isDeviceLocked(this)) return
        val sbn = runCatching { activeNotifications.firstOrNull { it.key == key } }.getOrNull()
        val n = sbn?.notification
        if (n != null) {
            val x = n.extras
            val actions = n.actions ?: emptyArray()
            fun matchAction(vararg words: String): PendingIntent? =
                actions.firstOrNull { a ->
                    val t = a.title?.toString()?.lowercase(Locale.ROOT) ?: ""
                    words.any { t.contains(it) }
                }?.actionIntent
            val intent = when (which) {
                "answer" -> x.getParcelable(Notification.EXTRA_ANSWER_INTENT, PendingIntent::class.java)
                    ?: matchAction("answer", "accept", "annehmen", "abnehmen", "répondre", "rispondi")
                    ?: actions.lastOrNull()?.actionIntent
                "decline" -> x.getParcelable(Notification.EXTRA_DECLINE_INTENT, PendingIntent::class.java)
                    ?: matchAction("decline", "reject", "dismiss", "ablehnen", "abweisen", "refuser", "rifiuta")
                    ?: actions.firstOrNull()?.actionIntent
                else -> x.getParcelable(Notification.EXTRA_HANG_UP_INTENT, PendingIntent::class.java)
                    ?: x.getParcelable(Notification.EXTRA_DECLINE_INTENT, PendingIntent::class.java)
                    ?: matchAction("hang up", "end", "auflegen", "beenden", "raccrocher", "termina")
                    ?: actions.firstOrNull()?.actionIntent
            }
            if (intent != null && !NotchBridge.isDeviceLocked(this)) {
                val ok = runCatching { sendPendingIntent(intent); true }
                    .onFailure { android.util.Log.w("HolmNotch", "call $which PendingIntent failed", it) }
                    .getOrDefault(false)
                if (ok) return
            }
        }
        // Fallback for cellular calls on Android 15+ where CallStyle intents may be stripped from listeners.
        if (checkSelfPermission(android.Manifest.permission.ANSWER_PHONE_CALLS) == android.content.pm.PackageManager.PERMISSION_GRANTED) {
            val telecom = getSystemService(android.telecom.TelecomManager::class.java)
            runCatching {
                if (NotchBridge.isDeviceLocked(this)) return@runCatching
                @Suppress("DEPRECATION")
                if (which == "answer") telecom.acceptRingingCall() else telecom.endCall()
            }.onFailure { android.util.Log.w("HolmNotch", "call $which telecom failed", it) }
        }
    }

    fun contentIntent(key: String): PendingIntent? {
        if (NotchBridge.isDeviceLocked(this)) return null
        return runCatching { activeNotifications.firstOrNull { it.key == key }?.notification?.contentIntent }.getOrNull()
    }

    private fun send(sbn: StatusBarNotification, initial: Boolean) {
        if (sbn.packageName == packageName || NotchBridge.isDeviceLocked(this)) return
        val n = sbn.notification
        val x = n.extras
        val pkg = sbn.packageName
        val locked = NotchBridge.isDeviceLocked(this)
        // Google Play services' earbuds card ("L: 100%  C: 96%  R: 100%"): the earbuds activity shows it, not a pop-up.
        if (pkg == "com.google.android.gms" && sendBudsBattery(n)) return
        sendIcon(pkg)
        val ranking = Ranking()
        val importance = if (currentRanking?.getRanking(sbn.key, ranking) == true) ranking.importance else NotificationManager.IMPORTANCE_DEFAULT
        val isMedia = x.containsKey(Notification.EXTRA_MEDIA_SESSION) ||
            x.getString(Notification.EXTRA_TEMPLATE)?.contains("MediaStyle") == true
        val rawCallType = x.getInt(Notification.EXTRA_CALL_TYPE, 0)
        val actionTitles = (n.actions ?: emptyArray()).map { (it.title?.toString() ?: "").lowercase() }
        val hasAnswerAction = actionTitles.any { t ->
            t.contains("answer") || t.contains("accept") || t.contains("annehmen") || t.contains("abnehmen") ||
                t.contains("répondre") || t.contains("rispondi") || t.contains("contestar")
        }
        val hasDeclineAction = actionTitles.any { t ->
            t.contains("decline") || t.contains("reject") || t.contains("ablehnen") || t.contains("abweisen") ||
                t.contains("refuser") || t.contains("rifiuta") || t.contains("rechazar")
        }
        val isPhoneRinging = runCatching {
            getSystemService(android.media.AudioManager::class.java)?.mode == android.media.AudioManager.MODE_RINGTONE
        }.getOrDefault(false)
        val isCallNotif = rawCallType != 0 || n.category == Notification.CATEGORY_CALL || (hasAnswerAction && hasDeclineAction)
        val callType = when {
            rawCallType != 0 -> rawCallType
            isCallNotif && (hasAnswerAction || isPhoneRinging || !x.getBoolean(Notification.EXTRA_SHOW_CHRONOMETER, false)) -> 1
            isCallNotif -> 2
            else -> 0
        }
        // The turn arrow of a navigation, the caller's photo: the notification's own picture.
        val picture = if (!locked && (pkg in PICTURE_APPS || callType != 0 || n.category == Notification.CATEGORY_CALL || n.category == Notification.CATEGORY_NAVIGATION))
            runCatching { n.getLargeIcon()?.loadDrawable(this)?.let { png(it, 96) } }.getOrNull() else null
        NotchBridge.toNotch(
            mapOf(
                "type" to "notification",
                "initial" to initial,
                "key" to sbn.key,
                "package" to pkg,
                "app" to label(pkg),
                "title" to if (locked) null else x.getCharSequence(Notification.EXTRA_TITLE)?.toString(),
                "text" to if (locked) null else
                    (x.getCharSequence(Notification.EXTRA_TEXT) ?: x.getCharSequence(Notification.EXTRA_BIG_TEXT))?.toString(),
                "sub" to if (locked) null else x.getCharSequence(Notification.EXTRA_SUB_TEXT)?.toString(),
                "category" to n.category,
                "channel" to n.channelId,
                // Google's Clock keeps the timer's state and time left here ("RUNNING|…|⏳0:03:31"); its view is custom.
                "sortKey" to if (locked) null else n.sortKey,
                "ongoing" to sbn.isOngoing,
                "summary" to ((n.flags and Notification.FLAG_GROUP_SUMMARY) != 0),
                "media" to isMedia,
                "progress" to x.getInt(Notification.EXTRA_PROGRESS, 0),
                "progressMax" to x.getInt(Notification.EXTRA_PROGRESS_MAX, 0),
                "indeterminate" to x.getBoolean(Notification.EXTRA_PROGRESS_INDETERMINATE, false),
                "chronometer" to x.getBoolean(Notification.EXTRA_SHOW_CHRONOMETER, false),
                "countDown" to x.getBoolean(Notification.EXTRA_CHRONOMETER_COUNT_DOWN, false),
                "when" to n.`when`,
                // Android 16 Live Updates: promoted ongoing notifications, with a short text for the status chip.
                "promoted" to ((n.flags and FLAG_PROMOTED_ONGOING) != 0),
                "shortText" to if (locked) null else x.getCharSequence("android.shortCriticalText")?.toString(),
                "color" to n.color,
                "importance" to importance,
                "posted" to sbn.postTime,
                // The notification's buttons ("Stop", "Add 1 min", "Mark as read"); a reply's is marked: it asks for text.
                "actions" to if (locked) emptyList() else (n.actions ?: emptyArray()).mapIndexed { i, a ->
                    listOf(i, a.title?.toString() ?: "", a.remoteInputs?.any { it.allowFreeFormInput } == true)
                },
                // Calls: 1 incoming, 2 ongoing, 3 screening (Notification.CallStyle).
                "callType" to callType,
                "caller" to if (locked) null else (if (callType != 0) runCatching {
                    x.getParcelable(Notification.EXTRA_CALL_PERSON, android.app.Person::class.java)?.name?.toString()
                }.getOrNull() else null),
                "picture" to picture,
            )
        )
    }

    /**
     * Each earbud's and the case's battery, from Google Play services' notification for Fast Pair earbuds. Its view is
     * custom: its texts are read from the view, laid out off screen. True if it was that card.
     */
    private fun sendBudsBattery(n: Notification): Boolean {
        val x = n.extras
        val texts = mutableListOf<String>()
        listOf(Notification.EXTRA_TITLE, Notification.EXTRA_TEXT, Notification.EXTRA_BIG_TEXT, Notification.EXTRA_SUB_TEXT)
            .forEach { k -> x.getCharSequence(k)?.let { texts += it.toString() } }
        x.getCharSequenceArray(Notification.EXTRA_TEXT_LINES)?.forEach { texts += it.toString() }
        for (views in listOfNotNull(n.bigContentView, n.contentView, n.headsUpContentView)) {
            runCatching {
                val view = views.apply(this, android.widget.FrameLayout(this))
                fun walk(v: android.view.View) {
                    if (v is android.widget.TextView) v.text?.let { texts += it.toString() }
                    if (v is android.view.ViewGroup) for (i in 0 until v.childCount) walk(v.getChildAt(i))
                }
                walk(view)
            }
        }
        val levels = mutableMapOf<String, Int>()
        val pattern = Regex("""\b([LRC])\s*:\s*(\d{1,3})\s*%""")
        for (t in texts) for (m in pattern.findAll(t)) levels.putIfAbsent(m.groupValues[1], m.groupValues[2].toInt().coerceIn(0, 100))
        if (levels.isEmpty()) return false
        NotchBridge.toNotch(
            mapOf(
                "type" to "budsBattery",
                "name" to x.getCharSequence(Notification.EXTRA_TITLE)?.toString(),
                "left" to levels["L"],
                "right" to levels["R"],
                "case" to levels["C"],
            )
        )
        return true
    }

    private fun label(pkg: String) =
        runCatching { packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString() }.getOrDefault(pkg)

    /** The app's own icon, once per app, to show and style the notification by. */
    private fun sendIcon(pkg: String) {
        if (!iconsSent.add(pkg)) return
        val png = runCatching { png(packageManager.getApplicationIcon(pkg), 96) }.getOrNull() ?: return
        NotchBridge.toNotch(mapOf("type" to "icon", "package" to pkg, "png" to png))
    }

    // ───────────── media ─────────────

    private fun pick(list: List<MediaController>) {
        val playing = list.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING }
        val next = playing ?: list.firstOrNull { it.packageName == controller?.packageName } ?: list.firstOrNull()
        if (next?.sessionToken != controller?.sessionToken) {
            controller?.unregisterCallback(onMedia)
            controller = next
            next?.registerCallback(onMedia, NotchBridge.handler)
            lastArtKey = null
        }
        sendMedia()
    }

    private fun sendMedia() {
        if (NotchBridge.isDeviceLocked(this)) {
            cancelArt()
            return
        }
        val c = controller
        val meta = c?.metadata
        val state = c?.playbackState
        if (c == null || meta == null) {
            cancelArt()
            NotchBridge.toNotch(mapOf("type" to "media", "active" to false))
            return
        }
        val title = meta.getString(MediaMetadata.METADATA_KEY_TITLE) ?: meta.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE)
        val artist = meta.getString(MediaMetadata.METADATA_KEY_ARTIST) ?: meta.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST)
            ?: meta.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE)
        val artKey = "${c.packageName}|$title|$artist"
        var art: ByteArray? = null
        if (artKey != lastArtKey) {
            val bmp = meta.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART) ?: meta.getBitmap(MediaMetadata.METADATA_KEY_ART)
                ?: meta.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
            art = bmp?.let { runCatching { png(it, 256) }.getOrNull() } ?: artFromUri[artKey] ?: coverFromNotification(c.packageName, title)
            if (art != null) cancelArt() else loadArt(artKey, meta)
            // Players often send the title first and the cover a moment later: only a song with its cover counts as sent.
            if (art != null) lastArtKey = artKey else lookAgainForArt(artKey)
        }
        val (canLike, liked) = likeState(c, meta, state)
        NotchBridge.toNotch(
            mapOf(
                "type" to "media",
                "active" to true,
                "package" to c.packageName,
                "app" to label(c.packageName),
                "title" to title,
                "artist" to artist,
                "art" to art,
                "artKey" to artKey,
                "playing" to (state?.state == PlaybackState.STATE_PLAYING || state?.state == PlaybackState.STATE_BUFFERING),
                "position" to (state?.position ?: 0L),
                "updated" to (state?.lastPositionUpdateTime ?: 0L),
                "speed" to (state?.playbackSpeed?.toDouble() ?: 1.0),
                "duration" to meta.getLong(MediaMetadata.METADATA_KEY_DURATION),
                "canSeek" to (((state?.actions ?: 0L) and PlaybackState.ACTION_SEEK_TO) != 0L),
                "canLike" to canLike,
                "liked" to liked,
            )
        )
    }

    private fun likeCustomAction(state: PlaybackState?): PlaybackState.CustomAction? =
        state?.customActions?.firstOrNull { a ->
            val s = "${a.action} ${a.name}".lowercase(Locale.ROOT)
            s.contains("like") || s.contains("heart") || s.contains("love") || s.contains("fav") ||
                s.contains("thumb_up") || s.contains("thumbs_up") || s.contains("add_to_collection") ||
                s.contains("remove_from_collection") || s.contains("collection") || s.contains("save")
        }

    private fun likeNotificationAction(pkg: String): Notification.Action? = runCatching {
        activeNotifications.firstOrNull {
            it.packageName == pkg && it.notification.extras.containsKey(Notification.EXTRA_MEDIA_SESSION)
        }?.notification?.actions?.firstOrNull { a ->
            val t = a.title?.toString()?.lowercase(Locale.ROOT) ?: ""
            t.contains("like") || t.contains("heart") || t.contains("love") || t.contains("fav") ||
                t.contains("save") || t.contains("gefällt") || t.contains("speichern") || t.contains("hinzufügen")
        }
    }.getOrNull()

    private fun likeState(c: MediaController, meta: MediaMetadata, state: PlaybackState?): Pair<Boolean, Boolean> {
        val custom = likeCustomAction(state)
        if (custom != null) {
            val s = "${custom.action} ${custom.name}".lowercase(Locale.ROOT)
            val isLiked = s.contains("remove") || s.contains("unlike") || s.contains("unfav") || s.contains("saved") || s.contains("added")
            return true to isLiked
        }
        val rating = meta.getRating(MediaMetadata.METADATA_KEY_USER_RATING)
        if (rating != null && rating.isRated) {
            return true to (rating.hasHeart() || rating.isThumbUp)
        }
        if (c.ratingType == android.media.Rating.RATING_HEART || c.ratingType == android.media.Rating.RATING_THUMB_UP_DOWN) {
            return true to false
        }
        val notifAct = likeNotificationAction(c.packageName)
        if (notifAct != null) {
            val t = notifAct.title?.toString()?.lowercase(Locale.ROOT) ?: ""
            val isLiked = t.contains("remove") || t.contains("unlike") || t.contains("entfernen")
            return true to isLiked
        }
        return false to false
    }

    /**
     * Skipped with the headphones, the screen off: some players (Spotify) fetch the cover only later and say so on no
     * channel the notch hears. Looked for again a few times, as long as that song plays.
     */
    private fun lookAgainForArt(artKey: String) {
        if (artMissingKey != artKey) {
            artMissingKey = artKey
            artRetries = 0
        }
        if (artRetries >= 4) return
        val delay = longArrayOf(800, 2000, 5000, 10000)[artRetries++]
        NotchBridge.handler.postDelayed({
            if (artMissingKey == artKey && lastArtKey != artKey) sendMedia()
        }, delay)
    }

    /**
     * Spotify and others name the cover by a link (an https address or a content:// one) instead of handing it over:
     * fetched once in the background, then the song is sent again with it. Both the compressed input and decoded
     * bitmap are bounded; an obsolete load is cancelled when the song changes or the listener disconnects.
     */
    private fun loadArt(artKey: String, meta: MediaMetadata) {
        if (artLoadingKey != artKey) cancelArt()
        val uri = listOf(MediaMetadata.METADATA_KEY_ALBUM_ART_URI, MediaMetadata.METADATA_KEY_ART_URI, MediaMetadata.METADATA_KEY_DISPLAY_ICON_URI)
            .firstNotNullOfOrNull { meta.getString(it)?.takeIf { s -> s.isNotBlank() } }
            ?.let(Uri::parse) ?: return
        val scheme = uri.scheme?.lowercase(Locale.ROOT)
        if (scheme != "https" && scheme != "content") return
        if (scheme == "content" && !UriSecurity.isAllowedContentUri(this, uri)) return
        if (artLoadingKey == artKey && artLoad?.cancelled == false) return

        val request = ArtLoad()
        artLoad = request
        artLoadingKey = artKey
        val timeout = Runnable { if (!request.finished) request.cancel() }
        request.timeout = timeout
        NotchBridge.handler.postDelayed(timeout, ART_TIMEOUT_MS)
        request.thread = Thread({
            val bytes = runCatching { fetchArt(uri, request) }.getOrNull()
            request.finished = true
            NotchBridge.handler.removeCallbacks(timeout)
            NotchBridge.handler.post {
                if (artLoad !== request) return@post
                artLoad = null
                artLoadingKey = null
                if (!request.cancelled && bytes != null) {
                    artFromUri[artKey] = bytes
                    if (lastArtKey != artKey) sendMedia()
                }
            }
        }, "HolmArt")
        if (request.cancelled) request.thread?.interrupt() else request.thread?.start()
    }

    private fun cancelArt() {
        artLoad?.cancel()
        artLoad = null
        artLoadingKey = null
    }

    private fun fetchArt(uri: Uri, request: ArtLoad): ByteArray? {
        request.checkActive()
        val bytes = (if (uri.scheme.equals("https", ignoreCase = true)) fetchHttps(uri, request)
        else {
            val signal = CancellationSignal()
            request.signal = signal
            contentResolver.openAssetFileDescriptor(uri, "r", signal)?.use { descriptor ->
                val input = descriptor.createInputStream()
                request.input = input
                readBounded(input, request)
            }
        }) ?: return null
        request.checkActive()
        val bitmap = decodeArt(bytes) ?: return null
        return try {
            png(bitmap, ART_SIZE)
        } finally {
            bitmap.recycle()
        }
    }

    private fun fetchHttps(uri: Uri, request: ArtLoad): ByteArray? {
        var current = uri
        for (redirects in 0..MAX_ART_REDIRECTS) {
            request.checkActive()
            val connection = URL(current.toString()).openConnection() as HttpURLConnection
            request.connection = connection
            connection.instanceFollowRedirects = false // a redirect must never downgrade https to http
            connection.connectTimeout = ART_IO_TIMEOUT_MS
            connection.readTimeout = ART_IO_TIMEOUT_MS
            connection.setRequestProperty("Accept", "image/*")
            try {
                val code = connection.responseCode
                if (code in 300..399) {
                    val location = connection.getHeaderField("Location") ?: return null
                    val next = runCatching { URL(URL(current.toString()), location) }.getOrNull() ?: return null
                    if (!next.protocol.equals("https", ignoreCase = true)) return null
                    current = Uri.parse(next.toString())
                    continue
                }
                if (code !in 200..299 || connection.contentLengthLong > MAX_ART_BYTES) return null
                request.input = connection.inputStream
                return readBounded(request.input!!, request)
            } finally {
                connection.disconnect()
            }
        }
        return null
    }

    private fun readBounded(input: InputStream, request: ArtLoad): ByteArray? {
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(16 * 1024)
        while (true) {
            request.checkActive()
            val count = input.read(buffer)
            if (count < 0) break
            if (output.size().toLong() + count > MAX_ART_BYTES) return null
            output.write(buffer, 0, count)
        }
        request.checkActive()
        return output.toByteArray()
    }

    /** Bounds pass first, then a power-of-two sample decode and a final fit. No full-resolution bitmap is allocated. */
    private fun decodeArt(bytes: ByteArray): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeStream(ByteArrayInputStream(bytes), null, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0 || bounds.outWidth > MAX_ART_DIMENSION ||
            bounds.outHeight > MAX_ART_DIMENSION || bounds.outWidth.toLong() * bounds.outHeight > MAX_ART_BOUNDS_PIXELS) return null

        var sample = 1L
        while (sample <= Int.MAX_VALUE / 2L &&
            (bounds.outWidth.toLong() / (sample * 2) >= ART_SIZE || bounds.outHeight.toLong() / (sample * 2) >= ART_SIZE)) {
            sample *= 2
        }
        val options = BitmapFactory.Options().apply {
            inSampleSize = sample.toInt()
            inPreferredConfig = Bitmap.Config.ARGB_8888
            inScaled = false
        }
        val bitmap = BitmapFactory.decodeStream(ByteArrayInputStream(bytes), null, options) ?: return null
        if (bitmap.width.toLong() * bitmap.height > MAX_DECODED_ART_PIXELS) {
            bitmap.recycle()
            return null
        }
        val scale = minOf(ART_SIZE.toFloat() / bitmap.width, ART_SIZE.toFloat() / bitmap.height).coerceAtMost(1f)
        val width = Math.round(bitmap.width * scale).coerceIn(1, ART_SIZE)
        val height = Math.round(bitmap.height * scale).coerceIn(1, ART_SIZE)
        val scaled = if (width == bitmap.width && height == bitmap.height) bitmap
        else Bitmap.createScaledBitmap(bitmap, width, height, true)
        if (scaled !== bitmap) bitmap.recycle()
        return scaled
    }

    /** Some players (Chrome, for one) put the cover only on their media notification, as its large icon. */
    private fun coverFromNotification(pkg: String, title: String?): ByteArray? = runCatching {
        val players = activeNotifications.filter {
            it.packageName == pkg && it.notification.extras.containsKey(Notification.EXTRA_MEDIA_SESSION)
        }
        // The notification of the session that plays (a browser has one per tab), else any with a picture.
        val token = controller?.sessionToken
        val n = (players.firstOrNull {
            it.notification.extras.getParcelable(Notification.EXTRA_MEDIA_SESSION, android.media.session.MediaSession.Token::class.java) == token
        } ?: players.firstOrNull { it.notification.getLargeIcon() != null })?.notification ?: return null
        // Right after a skip the notification may still show the song before: compare leniently if multiple players.
        val shows = n.extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.trim()
        if (players.size > 1 && title != null && shows != null && !shows.equals(title.trim(), ignoreCase = true)) return null
        val drawable = n.getLargeIcon()?.loadDrawable(this) ?: return null
        png(drawable, 256)
    }.getOrNull()

    fun mediaAction(action: String, position: Long?) {
        if (NotchBridge.isDeviceLocked(this)) return
        val c = controller ?: return
        val t = c.transportControls ?: return
        when (action) {
            "play" -> t.play()
            "pause" -> t.pause()
            "toggle" -> if (c.playbackState?.state == PlaybackState.STATE_PLAYING) t.pause() else t.play()
            "next" -> t.skipToNext()
            "previous" -> t.skipToPrevious()
            "seek" -> if (position != null) t.seekTo(position)
            "like" -> {
                val custom = likeCustomAction(c.playbackState)
                if (custom != null) {
                    t.sendCustomAction(custom, custom.extras)
                    NotchBridge.handler.postDelayed({ sendMedia() }, 350)
                    return
                }
                val rating = c.metadata?.getRating(MediaMetadata.METADATA_KEY_USER_RATING)
                if (c.ratingType == android.media.Rating.RATING_HEART) {
                    t.setRating(android.media.Rating.newHeartRating(!(rating?.hasHeart() == true)))
                    NotchBridge.handler.postDelayed({ sendMedia() }, 350)
                    return
                }
                if (c.ratingType == android.media.Rating.RATING_THUMB_UP_DOWN) {
                    t.setRating(android.media.Rating.newThumbRating(!(rating?.isThumbUp == true)))
                    NotchBridge.handler.postDelayed({ sendMedia() }, 350)
                    return
                }
                likeNotificationAction(c.packageName)?.actionIntent?.let { pi ->
                    runCatching { sendPendingIntent(pi) }
                    NotchBridge.handler.postDelayed({ sendMedia() }, 350)
                }
            }
        }
    }

    companion object {
        /** Apps whose notification picture means something (the next turn). */
        val PICTURE_APPS = setOf("com.google.android.apps.maps", "com.waze")

        /** Notification.FLAG_PROMOTED_ONGOING (Android 16). */
        const val FLAG_PROMOTED_ONGOING = 0x00040000

        private const val ART_SIZE = 256
        private const val MAX_ART_BYTES = 8 * 1024 * 1024L
        private const val ART_IO_TIMEOUT_MS = 8_000
        private const val ART_TIMEOUT_MS = 12_000L
        private const val MAX_ART_REDIRECTS = 3
        private const val MAX_ART_DIMENSION = 32_768
        private const val MAX_ART_BOUNDS_PIXELS = 1_000_000_000L
        private const val MAX_DECODED_ART_PIXELS = 4L * ART_SIZE * ART_SIZE

        fun png(drawable: Drawable, size: Int): ByteArray {
            val bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
            val canvas = Canvas(bmp)
            drawable.setBounds(0, 0, size, size)
            drawable.draw(canvas)
            return png(bmp, size)
        }

        fun png(bitmap: Bitmap, max: Int): ByteArray {
            val scale = minOf(1f, max.toFloat() / maxOf(bitmap.width, bitmap.height))
            val b = if (scale < 1f) Bitmap.createScaledBitmap(bitmap, (bitmap.width * scale).toInt().coerceAtLeast(1),
                (bitmap.height * scale).toInt().coerceAtLeast(1), true) else bitmap
            return ByteArrayOutputStream().use { b.compress(Bitmap.CompressFormat.PNG, 100, it); it.toByteArray() }
        }
    }
}
