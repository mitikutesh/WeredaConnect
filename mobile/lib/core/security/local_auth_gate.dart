import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:local_auth/error_codes.dart' as auth_error;
import 'package:local_auth/local_auth.dart';

/// Result of asking the device to confirm the user (biometric or PIN).
enum LocalAuthOutcome {
  /// The user passed the device prompt.
  authenticated,

  /// The device has no lock enrolled or local auth is unsupported (or this
  /// is web). Callers proceed and log a warning: the server stays the
  /// authority, so this is never a hard block.
  unavailable,

  /// The user cancelled or failed the prompt. Callers must not proceed.
  failed,
}

/// Whether an outcome lets the gated action go ahead: everything except an
/// explicit failure. "Unavailable" proceeds (logged) because the server stays
/// the authority and a device without a lock must not be locked out.
extension LocalAuthOutcomeDecision on LocalAuthOutcome {
  bool get allowsProceed => this != LocalAuthOutcome.failed;
}

/// Small injectable wrapper around `local_auth` so screens and the app lock
/// can be tested with a fake.
abstract class LocalAuthGate {
  /// Show the system prompt with [reason]. Device PIN / pattern / password is
  /// accepted as a fallback to biometrics.
  Future<LocalAuthOutcome> authenticate(String reason);

  /// Whether the device can authenticate the user at all (biometrics
  /// enrolled or a device credential set). Always false on web.
  Future<bool> isAvailable();
}

/// Default [LocalAuthGate] backed by `package:local_auth`.
class DeviceLocalAuthGate implements LocalAuthGate {
  DeviceLocalAuthGate({LocalAuthentication? auth})
      : _auth = auth ?? LocalAuthentication();

  final LocalAuthentication _auth;

  /// Error codes meaning "this device cannot authenticate the user": no lock
  /// screen, nothing enrolled, or no hardware. Treated as unavailable.
  static const _unavailableCodes = {
    auth_error.notAvailable,
    auth_error.notEnrolled,
    auth_error.passcodeNotSet,
    auth_error.otherOperatingSystem,
  };

  @override
  Future<bool> isAvailable() async {
    if (kIsWeb) return false;
    try {
      return await _auth.isDeviceSupported() || await _auth.canCheckBiometrics;
    } on Object catch (e) {
      debugPrint('[LocalAuthGate] availability check failed: $e');
      return false;
    }
  }

  @override
  Future<LocalAuthOutcome> authenticate(String reason) async {
    if (kIsWeb) return LocalAuthOutcome.unavailable;
    try {
      if (!await _auth.isDeviceSupported()) {
        debugPrint(
          '[LocalAuthGate] WARNING: device has no lock screen or local auth '
          'support; proceeding without a device check.',
        );
        return LocalAuthOutcome.unavailable;
      }
      final ok = await _auth.authenticate(
        localizedReason: reason,
        // biometricOnly stays at its default (false): device PIN / pattern
        // / password is an accepted fallback to biometrics.
        options: const AuthenticationOptions(
          // Keep the prompt alive if the app is briefly backgrounded.
          stickyAuth: true,
        ),
      );
      return ok ? LocalAuthOutcome.authenticated : LocalAuthOutcome.failed;
    } on PlatformException catch (e) {
      if (_unavailableCodes.contains(e.code)) {
        debugPrint(
          '[LocalAuthGate] WARNING: local auth unavailable (${e.code}); '
          'proceeding without a device check.',
        );
        return LocalAuthOutcome.unavailable;
      }
      debugPrint('[LocalAuthGate] authentication error: ${e.code}');
      return LocalAuthOutcome.failed;
    } on Object catch (e) {
      // Plugin missing (e.g. tests without a platform channel): never block.
      debugPrint(
        '[LocalAuthGate] WARNING: local auth failed to run ($e); proceeding.',
      );
      return LocalAuthOutcome.unavailable;
    }
  }
}
