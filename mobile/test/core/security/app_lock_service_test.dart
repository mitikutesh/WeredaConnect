import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

class FakeGate implements LocalAuthGate {
  FakeGate({
    this.available = true,
    this.outcome = LocalAuthOutcome.authenticated,
  });

  bool available;
  LocalAuthOutcome outcome;
  int prompts = 0;

  @override
  Future<LocalAuthOutcome> authenticate(String reason) async {
    prompts++;
    return outcome;
  }

  @override
  Future<bool> isAvailable() async => available;
}

void main() {
  late MockSecureStorage storage;
  late FakeGate gate;
  late AppLockService service;

  setUp(() {
    storage = MockSecureStorage();
    when(
      () => storage.write(
        key: any(named: 'key'),
        value: any(named: 'value'),
      ),
    ).thenAnswer((_) async {});
    gate = FakeGate();
    service = AppLockService(gate: gate, storage: storage);
  });

  test('defaults ON when the device can authenticate and no choice is saved',
      () async {
    when(() => storage.read(key: AppLockService.enabledKey))
        .thenAnswer((_) async => null);
    expect(await service.isEnabled(), isTrue);
  });

  test('respects a saved OFF choice', () async {
    when(() => storage.read(key: AppLockService.enabledKey))
        .thenAnswer((_) async => 'false');
    expect(await service.isEnabled(), isFalse);
  });

  test('is off and unsupported on a device without a lock', () async {
    gate.available = false;
    when(() => storage.read(key: AppLockService.enabledKey))
        .thenAnswer((_) async => 'true');
    expect(await service.isSupported(), isFalse);
    expect(await service.isEnabled(), isFalse);
  });

  test('setEnabled stores the choice', () async {
    await service.setEnabled(enabled: false);
    verify(() => storage.write(key: AppLockService.enabledKey, value: 'false'))
        .called(1);
  });

  test('unlock passes unless the user failed the prompt', () async {
    expect(await service.unlock('r'), isTrue);
    gate.outcome = LocalAuthOutcome.unavailable;
    expect(await service.unlock('r'), isTrue);
    gate.outcome = LocalAuthOutcome.failed;
    expect(await service.unlock('r'), isFalse);
  });

  test('only more than 5 minutes in the background locks', () {
    expect(AppLockService.shouldLockAfter(const Duration(minutes: 4)), isFalse);
    expect(AppLockService.shouldLockAfter(const Duration(minutes: 5)), isFalse);
    expect(
      AppLockService.shouldLockAfter(const Duration(minutes: 5, seconds: 1)),
      isTrue,
    );
  });
}
