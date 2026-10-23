import 'dart:async';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/escrow_payment/data/services/escrow_sync_service.dart';

class MockApiClient extends Mock implements ApiClient {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

/// FRD-06: a queued escrow initiate refused with FRAUD_ACCOUNT_HELD is not
/// retried (the offline queue treats it as permanent) and the buyer is told.
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
    tempDir = Directory.systemTemp.createTempSync('escrow_fraud_hold_test_');
    isar = await Isar.open(
      [PaymentSchema, PaymentIntentSchema],
      directory: tempDir.path,
      name: 'escrow_fraud_hold_${DateTime.now().microsecondsSinceEpoch}',
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
    await isar.writeTxn(() async {
      await isar.paymentIntents.put(
        PaymentIntent()
          ..idempotencyKey = 'k-1'
          ..deliveryId = 'del-1'
          ..buyerId = 'buyer-1'
          ..farmerId = 'farmer-1'
          ..amount = 2500
          ..status = IntentStatus.pendingSync
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc(),
      );
    });
  });

  tearDown(() async {
    await sub.cancel();
    service.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  void answerPost(Object error) {
    when(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).thenThrow(error);
  }

  test('FRAUD_ACCOUNT_HELD fails the intent at once and says why', () async {
    answerPost(
      const AuthException(
        'Your account is under review.',
        403,
        fraudAccountHeldCode,
      ),
    );

    await service.processQueuedIntents();
    await service.processQueuedIntents();
    await Future<void>.delayed(Duration.zero);

    final intent = (await isar.paymentIntents.where().findAll()).single;
    expect(intent.status, IntentStatus.failed);
    expect(intent.lastError, fraudAccountHeldCode);
    // Sent once: a second drain finds nothing to send.
    verify(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).called(1);
    final event = emitted.whereType<SyncOperationRejected>().single;
    expect(event.userId, 'buyer-1');
    expect(event.message, startsWith(fraudAccountHeldCode));
    expect(
      syncRejectionErrorCode(
        entityType: event.entityType,
        action: event.action,
        message: event.message,
      ),
      AppErrorCode.accountHeld,
    );
    expect(emitted.whereType<StepUpRequired>(), isEmpty);
  });

  test('any other 403 keeps the old retry budget', () async {
    answerPost(const AuthException('not the buyer', 403, 'ESCROW_026'));

    await service.processQueuedIntents();

    final intent = (await isar.paymentIntents.where().findAll()).single;
    expect(intent.status, IntentStatus.pendingSync);
    expect(intent.retryCount, 1);
    expect(emitted.whereType<SyncOperationRejected>(), isEmpty);
  });
}
