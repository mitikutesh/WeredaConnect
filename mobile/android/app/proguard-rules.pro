# Flutter-specific ProGuard rules
# Keep Flutter engine
-keep class io.flutter.** { *; }
-keep class io.flutter.plugins.** { *; }
-keep class io.flutter.plugin.** { *; }
-keep class io.flutter.util.** { *; }
-keep class io.flutter.view.** { *; }

# Keep annotations
-keepattributes *Annotation*

# Isar database - required for offline-first storage
-keep class dev.isar.** { *; }
-keep class **.isar.** { *; }
-dontwarn dev.isar.**

# Connectivity plugin
-keep class dev.fluttercommunity.plus.connectivity.** { *; }

# Keep native methods for Isar
-keepclasseswithmembernames class * {
    native <methods>;
}

# Prevent R8 from stripping interface information
-keep,allowobfuscation,allowoptimization class * extends com.google.gson.TypeAdapter

# Suppress Play Core warnings (deferred components not used)
-dontwarn com.google.android.play.core.**
