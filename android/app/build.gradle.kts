plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "io.github.ghermin.camtel"
    compileSdk = 35

    defaultConfig {
        applicationId = "io.github.ghermin.camtel"
        minSdk = 26
        targetSdk = 35
        versionCode = (System.getenv("CAMTEL_VERSION_CODE") ?: "1").toInt()
        versionName = System.getenv("CAMTEL_VERSION_NAME") ?: "1.0.0"
    }

    signingConfigs {
        create("release") {
            val ks = System.getenv("CAMTEL_KEYSTORE")
            if (ks != null) {
                storeFile = file(ks)
                storePassword = System.getenv("CAMTEL_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("CAMTEL_KEY_ALIAS") ?: "camtel"
                keyPassword = System.getenv("CAMTEL_KEY_PASSWORD") ?: System.getenv("CAMTEL_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (System.getenv("CAMTEL_KEYSTORE") != null)
                signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        viewBinding = true
    }
}

dependencies {
    val camerax = "1.4.1"
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.camera:camera-core:$camerax")
    implementation("androidx.camera:camera-camera2:$camerax")
    implementation("androidx.camera:camera-lifecycle:$camerax")
    implementation("androidx.camera:camera-view:$camerax")
}
