plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// The version comes from the desktop app's (Dingus.csproj), so all three apps carry the same number.
val desktopVersion: String = Regex("<Version>([0-9.]+)</Version>")
    .find(rootProject.file("../Dingus.csproj").readText())?.groupValues?.get(1) ?: "0.0.0"
val versionParts = desktopVersion.split(".").map { it.toIntOrNull() ?: 0 } + listOf(0, 0, 0)

android {
    namespace = "app.dingus.tv"
    compileSdk = 35

    defaultConfig {
        applicationId = "app.dingus.tv"
        minSdk = 22          // Fire OS 5 and later (every Fire TV Stick still in use)
        targetSdk = 34
        versionCode = versionParts[0] * 1_000_000 + versionParts[1] * 1_000 + versionParts[2]
        versionName = desktopVersion
    }

    signingConfigs {
        create("release") {
            // A fixed key, so each new version installs over the last one.
            storeFile = rootProject.file("dingus-release.jks")
            storePassword = "dingus-android"
            keyAlias = "dingus"
            keyPassword = "dingus-android"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.getByName("release")
        }
        debug {
            signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { buildConfig = true }
    lint { abortOnError = false; checkReleaseBuilds = false }
}

dependencies {
    val media3 = "1.4.1"
    implementation("androidx.media3:media3-exoplayer:$media3")
    implementation("androidx.media3:media3-exoplayer-hls:$media3")
    implementation("androidx.media3:media3-ui:$media3")
    implementation("androidx.recyclerview:recyclerview:1.3.2")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
}
