import 'package:integration_test/integration_test_driver.dart';

/// Test driver for integration tests.
///
/// This driver enables running integration tests on real devices
/// or emulators using `flutter drive`.
///
/// Usage:
/// ```bash
/// flutter drive \
///   --driver=test_driver/integration_test.dart \
///   --target=integration_test/sync_crop_listing_test.dart
/// ```
///
/// For performance testing on low-end devices:
/// ```bash
/// flutter drive \
///   --driver=test_driver/integration_test.dart \
///   --target=integration_test/sync_crop_listing_test.dart \
///   --profile
/// ```
Future<void> main() => integrationDriver();
