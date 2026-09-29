import 'dart:async';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/escrow_payment/data/services/escrow_sync_service.dart';

class MockApiClient extends Mock implements ApiClient {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

/// D: a queued escrow initiate that hits AUTH_040 stays queued (same
/// idempotency key, no retry burned) and is sent again after step-up.
void main() {
  late Isar isar;
  late Directory tempDir;
  late MockApiClient api;
  late MockConnectivityMonitor monitor;
  late SecurityEvents events;
  late List<SecurityEvent> emitted;
  late StreamSubscription<SecurityEvent> sub;
  late EscrowSyncService service;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('escrow_step_up_test_');
    isar = await Isar.open(
      [PaymentSchema, PaymentIntentSchema],
      directory: tempDir.path,
      name: 'escrow_step_up_${DateTime.now().microsecondsSinceEpoch}',
    );
    api = MockApiClient();
    monitor = MockConnectivityMonitor();
    when(() => monitor.isConnected()).thenAnswer((_) async => true);
    events = SecurityEvents.forTesting();
    emitted = [];
    sub = events.events.listen(emitted.add);
    service = EscrowSyncService(
      isar: isar,
      apiClient: api,
      connectivityMonitor: monitor,
      securityEvents: events,
    );
  });

  tearDown(() async {
    await sub.cancel();
    service.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<void> insertIntent(String key, String deliveryId) async {
    await isar.writeTxn(() async {
      await isar.paymentIntents.put(
        PaymentIntent()
          ..idempotencyKey = key
          ..deliveryId = deliveryId
          ..buyerId = 'buyer-1'
          ..farmerId = 'farmer-1'
          ..amount = 25000
          ..status = IntentStatus.pendingSync
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc(),
      );
    });
  }

  test('AUTH_040 keeps the intent pending and asks for step-up once', () async {
    await insertIntent('k-1', 'del-1');
    await insertIntent('k-2', 'del-2');
    when(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).thenThrow(const StepUpRequiredException('step-up required'));

    await service.processQueuedIntents();
    await Future<void>.delayed(Duration.zero);

    final intents = await isar.paymentIntents.where().findAll();
    for (final intent in intents) {
      expect(intent.status, IntentStatus.pendingSync);
      expect(intent.retryCount, 0);
    }
    // Stops at the first gated intent instead of hammering the server.
    verify(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).called(1);
    expect(emitted.whereType<StepUpRequired>(), hasLength(1));
    expect(emitted.whereType<StepUpRequired>().single.withFayda, isFalse);
  });

  test('AUTH_041 keeps the intent pending and asks for a Fayda login',
      () async {
    await insertIntent('k-1', 'del-1');
    when(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).thenThrow(
      const StepUpRequiredException('Fayda needed', 403, 'AUTH_041'),
    );

    await service.processQueuedIntents();
    await Future<void>.delayed(Duration.zero);

    final intent = (await isar.paymentIntents.where().findAll()).single;
    expect(intent.status, IntentStatus.pendingSync);
    expect(intent.retryCount, 0);
    expect(emitted.whereType<StepUpRequired>().single.withFayda, isTrue);
  });

  test('a completed step-up retries the queued intent', () async {
    when(() => monitor.onConnectivityChanged)
        .thenAnswer((_) => const Stream.empty());
    await insertIntent('k-1', 'del-1');
    when(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).thenThrow(const StepUpRequiredException());
    service.startListening();
    await Future<void>.delayed(const Duration(milliseconds: 20));

    when(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer(
      (_) async => {
        'escrowId': 'esc-1',
        'deliveryId': 'del-1',
        'buyerId': 'buyer-1',
        'farmerId': 'farmer-1',
        'amount': 25000.0,
        'commission': 625.0,
        'farmerPayout': 24375.0,
        'currency': 'ETB',
        'status': 'escrowed',
        'createdAt': '2026-09-25T10:00:00Z',
        'expiresAt': '2026-10-02T10:00:00Z',
      },
    );
    events.stepUpCompleted();
    for (var i = 0; i < 20; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 10));
      final intent = await isar.paymentIntents.where().findFirst();
      if (intent!.status == IntentStatus.completed) break;
    }

    final intent = await isar.paymentIntents.where().findFirst();
    expect(intent!.status, IntentStatus.completed);
    // Same persisted idempotency key on the retry.
    final sent = verify(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: captureAny<dynamic>(named: 'data'),
      ),
    ).captured;
    for (final body in sent) {
      expect((body as Map)['idempotencyKey'], 'k-1');
    }
  });
}
