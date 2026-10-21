import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
}

/** Machine-specific settings, git-ignored; the release key is configured here. */
val localProperties = Properties().apply {
    rootProject.file("local.properties").takeIf { it.exists() }?.inputStream()?.use { load(it) }
}

android {
    namespace = "t7helpers.t7drop"
    compileSdk = 36

    defaultConfig {
        applicationId = "t7helpers.t7drop"
        // 29: received files go to Downloads through MediaStore, hidden until complete.
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        // libsodium comes as native code: the phone, and the x86_64 emulator.
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
    }

    signingConfigs {
        val storeFile = localProperties.getProperty("release.storeFile")
        if (storeFile != null) {
            create("release") {
                this.storeFile = file(storeFile)
                storePassword = file(localProperties.getProperty("release.storePasswordFile")).readText().trim()
                keyAlias = localProperties.getProperty("release.keyAlias")
                keyPassword = storePassword
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // Without the key in local.properties, release builds come out unsigned.
            signingConfig = signingConfigs.findByName("release")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures {
        compose = true
    }
    sourceSets {
        // The encryption test vectors, shared with the other clients.
        getByName("androidTest").assets.srcDir("../../testdata")
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.work)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons.core)
    // libsodium through JNA: JNA as the Android AAR, which brings its native part, instead
    // of the desktop JAR that lazysodium-android asks for.
    implementation(libs.lazysodium.android) { exclude(group = "net.java.dev.jna") }
    implementation(libs.jna) { artifact { type = "aar" } }
    implementation(libs.zxing.embedded)

    testImplementation(libs.junit)
    // Android's org.json is a stub in local unit tests; this is the real one.
    testImplementation(libs.json)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.test.junit)
}
