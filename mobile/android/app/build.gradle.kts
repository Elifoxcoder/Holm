import java.io.File

plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

// Release builds use Holm's existing release key and never fall back to the debug key. The documented external
// key.properties is loaded automatically; tool/sign-release.ps1 can then re-sign the APK with the same rotation lineage.
// Overrides: -Pholm.keyProperties=<path>, HOLM_KEY_PROPERTIES, mobile/android/key.properties, or the environment values
// HOLM_KEYSTORE, HOLM_KEYSTORE_PASSWORD, HOLM_KEY_ALIAS and HOLM_KEY_PASSWORD.
// Parse like tool/sign-release.ps1: Java Properties would eat single Windows path backslashes in an existing key file.
val releaseKeyProperties = mutableMapOf<String, String>()
val configuredPropertiesPath = providers.gradleProperty("holm.keyProperties").orNull
    ?: providers.environmentVariable("HOLM_KEY_PROPERTIES").orNull
val androidPropertiesFile = rootProject.file("key.properties")
val externalPropertiesFile = providers.environmentVariable("LOCALAPPDATA").orNull
    ?.let { File(it, "HolmDev/keys/key.properties") }
val releasePropertiesFile = when {
    configuredPropertiesPath != null -> rootProject.file(configuredPropertiesPath)
    androidPropertiesFile.isFile -> androidPropertiesFile
    externalPropertiesFile?.isFile == true -> externalPropertiesFile
    else -> null
}
if (releasePropertiesFile?.isFile == true) {
    releasePropertiesFile.forEachLine { line ->
        val match = Regex("""^\s*([^=]+)=(.*)$""").matchEntire(line)
        if (match != null) releaseKeyProperties[match.groupValues[1].trim()] = match.groupValues[2].trim()
    }
}

fun releaseCredential(property: String, environment: String, legacyProperty: String? = null): String? {
    val fromFile = listOfNotNull(property, legacyProperty).firstNotNullOfOrNull { name ->
        releaseKeyProperties[name]?.trim()?.takeIf { it.isNotEmpty() }
    }
    return fromFile ?: providers.environmentVariable(environment).orNull?.trim()?.takeIf { it.isNotEmpty() }
}

val releaseStoreFromFile = releaseKeyProperties["storeFile"]?.trim()?.takeIf { it.isNotEmpty() }
val releaseStorePath = releaseStoreFromFile
    ?: providers.environmentVariable("HOLM_KEYSTORE").orNull?.trim()?.takeIf { it.isNotEmpty() }
val releaseStorePassword = releaseCredential("storePassword", "HOLM_KEYSTORE_PASSWORD", "password")
val releaseKeyAlias = releaseCredential("keyAlias", "HOLM_KEY_ALIAS", "alias")
val releaseKeyPassword = releaseCredential("keyPassword", "HOLM_KEY_PASSWORD", "password")
val releaseStoreFile = releaseStorePath?.let { path ->
    val file = File(path)
    when {
        file.isAbsolute -> file
        releaseStoreFromFile != null -> File(releasePropertiesFile!!.parentFile, path)
        else -> rootProject.file(path)
    }
}
val hasReleaseSigning = releaseStoreFile != null && releaseStorePassword != null &&
    releaseKeyAlias != null && releaseKeyPassword != null

android {
    namespace = "ch.holmnotch.holm_mobile"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        applicationId = "ch.holmnotch.holm_mobile"
        // Android 15 (API 35) and newer.
        minSdk = 35
        targetSdk = flutter.targetSdkVersion
        // Uses the version code from pubspec.yaml. When using split APKs, 1000 * ABI_VERSION
        // is added automatically by Flutter. (https://developer.android.com/studio/build/configure-apk-splits#configure-APK-versions)
        // You can force using the value of versionCode by specifying the `-P force-version-code-ignoring-abi=true`
        // flag during build.
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    signingConfigs {
        if (hasReleaseSigning) {
            create("release") {
                storeFile = releaseStoreFile
                storePassword = releaseStorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword
            }
        }
    }

    buildTypes {
        release {
            // No debug fallback: a release task fails below unless the external release key is complete.
            signingConfig = if (hasReleaseSigning) signingConfigs.getByName("release") else null
        }
    }
}

val validateReleaseSigning by tasks.registering {
    outputs.upToDateWhen { false }
    doLast {
        val missing = buildList {
            if (releaseStorePath.isNullOrBlank()) add("storeFile")
            if (releaseStorePassword.isNullOrBlank()) add("storePassword")
            if (releaseKeyAlias.isNullOrBlank()) add("keyAlias")
            if (releaseKeyPassword.isNullOrBlank()) add("keyPassword")
        }
        if (missing.isNotEmpty()) {
            throw GradleException(
                "Release signing is required; missing ${missing.joinToString()}. " +
                    "Use -Pholm.keyProperties, HOLM_KEY_PROPERTIES, mobile/android/key.properties, " +
                    "%LOCALAPPDATA%\\HolmDev\\keys\\key.properties, or HOLM_KEYSTORE/HOLM_KEYSTORE_PASSWORD/" +
                    "HOLM_KEY_ALIAS/HOLM_KEY_PASSWORD. Debug signing is never used for releases."
            )
        }
        if (!releaseStoreFile!!.isFile) {
            throw GradleException("Release signing is required; the configured keystore does not exist: $releaseStorePath")
        }
    }
}

tasks.matching { it.name.matches(Regex("^(assemble|bundle|package).*Release.*$")) }.configureEach {
    dependsOn(validateReleaseSigning)
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}

dependencies {
    // Shizuku: grants Holm the permission to hide Android's pop-ups without a computer (adb).
    implementation("dev.rikka.shizuku:api:13.1.5")
    implementation("dev.rikka.shizuku:provider:13.1.5")
}
