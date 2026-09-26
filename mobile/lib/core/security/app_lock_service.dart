import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'package:wereda_connect/core/security/local_auth_gate.dart';

/// App lock (P1): require device authentication on cold start after an
/// auto-login and when returning after more than [backgroundTimeout] in the
/// background.
///
/// The on/off preference lives in secure storage under [enabledKey]. When the
/// user never chose, the lock defaults ON if the device can authenticate the
/// user, and the setting is hidden when it cannot. Web is a no-op.
class AppLockService {
  AppLockService({LocalAuthGate? gate, FlutterSecureStorage? storage})
      : gate = gate ?? DeviceLocalAuthGate(),
        _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
            );

  /// Secure storage key for the user's choice ('true' / 'false').
  static const enabledKey = 'app_lock_enabled';

  /// Time in the background after which returning to the app locks it. A
  /// short trip to the browser (Fayda) or the camera stays under this.
  static const backgroundTimeout = Duration(minutes: 5);

  /// The device prompt used to unlock.
  final LocalAuthGate gate;
  final FlutterSecureStorage _storage;

  /// Whether this device can use the app lock at all.
  Future<bool> isSupported() async {
    if (kIsWeb) return false;
    return gate.isAvailable();
  }

  /// Whether the lock is on. Unsupported devices are always off.
  Future<bool> isEnabled() async {
    if (!await isSupported()) return false;
    try {
      final stored = await _storage.read(key: enabledKey);
      return stored == null || stored == 'true';
    } on Object {
      return true;
    }
  }

  /// Save the user's choice.
  Future<void> setEnabled({required bool enabled}) async {
    await _storage.write(key: enabledKey, value: enabled.toString());
  }

  /// Show the unlock prompt. True when the app may be shown: the user
  /// passed, or the device can no longer authenticate (never a hard block).
  Future<bool> unlock(String reason) async {
    final outcome = await gate.authenticate(reason);
    return outcome.allowsProceed;
  }

  /// Whether time spent in the background should lock the app.
  static bool shouldLockAfter(Duration inBackground) =>
      inBackground > backgroundTimeout;

  static int _externalFlows = 0;

  /// Mark the start of a trip out of the app that the user started on
  /// purpose (the Fayda browser flow). Returning from it never locks, even
  /// when it took longer than [backgroundTimeout]. Pair every call with
  /// [endExternalFlow].
  static void beginExternalFlow() => _externalFlows++;

  /// The external flow started with [beginExternalFlow] is over.
  static void endExternalFlow() {
    if (_externalFlows > 0) _externalFlows--;
  }

  /// True while an external flow is in progress.
  static bool get externalFlowActive => _externalFlows > 0;

  /// Reset the external-flow counter between tests.
  @visibleForTesting
  static void resetExternalFlows() => _externalFlows = 0;
}
