plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.dormcast.receiver"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.dormcast.receiver"
        minSdk = 26
        targetSdk = 35
        versionCode = 71
        versionName = "0.7.1"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // Signed with debug config by default when no custom release keystore is supplied,
            // so the produced APK is directly installable on any Android device.
            signingConfig = signingConfigs.getByName("debug")
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.media:media:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
}
