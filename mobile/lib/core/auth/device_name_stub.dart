import 'package:flutter/foundation.dart';

/// Short device description on Android / iOS. Uses the platform family only:
/// a model string would need an extra plugin and is not worth the APK size.
String describeDevice() => switch (defaultTargetPlatform) {
      TargetPlatform.android => 'Android phone',
      TargetPlatform.iOS => 'iPhone',
      TargetPlatform.macOS => 'macOS',
      TargetPlatform.windows => 'Windows',
      TargetPlatform.linux => 'Linux',
      TargetPlatform.fuchsia => 'Fuchsia',
    };
