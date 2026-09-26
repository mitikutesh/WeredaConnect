import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:local_auth/local_auth.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/security/local_auth_gate.dart';

class MockLocalAuthentication extends Mock implements LocalAuthentication {}

void main() {
  late MockLocalAuthentication auth;
  late DeviceLocalAuthGate gate;

  setUpAll(() {
    registerFallbackValue(const AuthenticationOptions());
  });

  setUp(() {
    auth = MockLocalAuthentication();
    gate = DeviceLocalAuthGate(auth: auth);
    when(() => auth.isDeviceSupported()).thenAnswer((_) async => true);
    when(() => auth.canCheckBiometrics).thenAnswer((_) async => true);
  });

  void stubAuthenticate(Future<bool> Function() answer) {
    when(
      () => auth.authenticate(
        localizedReason: any(named: 'localizedReason'),
        options: any(named: 'options'),
      ),
    ).thenAnswer((_) => answer());
  }

  test('passing the prompt is authenticated', () async {
    stubAuthenticate(() async => true);
    expect(await gate.authenticate('why'), LocalAuthOutcome.authenticated);
  });

  test('allows device PIN as a fallback (biometricOnly off)', () async {
    stubAuthenticate(() async => true);
    await gate.authenticate('why');
    final options = verify(
      () => auth.authenticate(
        localizedReason: 'why',
        options: captureAny(named: 'options'),
      ),
    ).captured.single as AuthenticationOptions;
    expect(options.biometricOnly, isFalse);
  });

  test('cancelling the prompt fails', () async {
    stubAuthenticate(() async => false);
    expect(await gate.authenticate('why'), LocalAuthOutcome.failed);
  });

  test('no device lock means unavailable (proceed, no hard block)', () async {
    when(() => auth.isDeviceSupported()).thenAnswer((_) async => false);
    expect(await gate.authenticate('why'), LocalAuthOutcome.unavailable);
    verifyNever(
      () => auth.authenticate(
        localizedReason: any(named: 'localizedReason'),
        options: any(named: 'options'),
      ),
    );
  });

  test('NotEnrolled / PasscodeNotSet are unavailable', () async {
    stubAuthenticate(() => throw PlatformException(code: 'NotEnrolled'));
    expect(await gate.authenticate('why'), LocalAuthOutcome.unavailable);
    stubAuthenticate(() => throw PlatformException(code: 'PasscodeNotSet'));
    expect(await gate.authenticate('why'), LocalAuthOutcome.unavailable);
  });

  test('LockedOut is a failure (user cannot pass right now)', () async {
    stubAuthenticate(() => throw PlatformException(code: 'LockedOut'));
    expect(await gate.authenticate('why'), LocalAuthOutcome.failed);
  });

  test('isAvailable reflects device support or biometrics', () async {
    expect(await gate.isAvailable(), isTrue);
    when(() => auth.isDeviceSupported()).thenAnswer((_) async => false);
    when(() => auth.canCheckBiometrics).thenAnswer((_) async => false);
    expect(await gate.isAvailable(), isFalse);
  });

  test('only an explicit failure blocks the gated action (QR confirm)', () {
    expect(LocalAuthOutcome.authenticated.allowsProceed, isTrue);
    expect(LocalAuthOutcome.unavailable.allowsProceed, isTrue);
    expect(LocalAuthOutcome.failed.allowsProceed, isFalse);
  });
}
