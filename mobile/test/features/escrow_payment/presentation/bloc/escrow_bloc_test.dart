import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_repository.dart'
    hide EscrowError;
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';

class MockApiClient extends Mock implements ApiClient {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

class MockBaseEscrowRepository extends Mock implements BaseEscrowRepository {}

/// Insert a test payment directly into Isar.
Future<Payment> _insertTestPayment(
  Isar isar, {
  String paymentId = 'esc-001',
  String deliveryId = 'del-001',
  PaymentStatus status = PaymentStatus.escrowed,
  double amount = 15000.0,
}) async {
  final now = DateTime.now().toUtc();
  final commission = amount * 0.025 < 5 ? 5.0 : amount * 0.025;
  final payment = Payment()
    ..paymentId = paymentId
    ..deliveryId = deliveryId
    ..farmerId = 'farmer-001'
    ..buyerId = 'buyer-001'
    ..amount = amount
    ..commission = commission
    ..commissionRate = 0.025
    ..farmerPayout = amount - commission
    ..status = status
    ..createdAt = now
    ..updatedAt = now
    ..expiresAt = now.add(const Duration(days: 7))
    ..version = 1;

  await isar.writeTxn(() async {
    await isar.payments.put(payment);
  });

  return payment;
}

/// Insert a test payment intent directly into Isar.
Future<PaymentIntent> _insertTestIntent(
  Isar isar, {
  String idempotencyKey = 'buyer-001:del-001:initiate:1708300000000',
  IntentStatus status = IntentStatus.pendingSync,
}) async {
  final intent = PaymentIntent()
    ..idempotencyKey = idempotencyKey
    ..deliveryId = 'del-001'
    ..buyerId = 'buyer-001'
    ..farmerId = 'farmer-001'
    ..amount = 15000.0
    ..status = status
    ..createdAt = DateTime.now().toUtc();

  await isar.writeTxn(() async {
    await isar.paymentIntents.put(intent);
  });

  return intent;
}

void main() {
  group('EscrowBloc', () {
    late Isar isar;
    late Directory tempDir;
    late MockApiClient apiClient;
    late MockConnectivityMonitor connectivityMonitor;
    late EscrowRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('escrow_bloc_test_');
      isar = await Isar.open(
        [
          UserSchema,
          CropListingSchema,
          TripSchema,
          DeliverySchema,
          PaymentSchema,
          PaymentIntentSchema,
          SyncOperationSchema,
        ],
        directory: tempDir.path,
        name: 'test_escrow_bloc_${DateTime.now().millisecondsSinceEpoch}',
      );
      apiClient = MockApiClient();
      connectivityMonitor = MockConnectivityMonitor();
      repository = EscrowRepository(
        isar: isar,
        apiClient: apiClient,
        connectivityMonitor: connectivityMonitor,
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('initial state is EscrowInitial', () {
      final bloc = EscrowBloc(repository: repository);
      expect(bloc.state, const EscrowInitial());
      bloc.close();
    });

    // ── InitiatePayment tests ──

    group('InitiatePayment', () {
      test('emits [Loading, PaymentInitiated] on online success', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'deliveryId': 'del-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'amount': 15000.0,
            'commission': 375.0,
            'farmerPayout': 14625.0,
            'currency': 'ETB',
            'status': 'escrowed',
            'createdAt': '2026-02-19T10:00:00Z',
            'expiresAt': '2026-02-26T10:00:00Z',
            'telebirrRef': 'TBR-001',
          },
        );

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(
          const InitiatePayment(
            deliveryId: 'del-001',
            buyerId: 'buyer-001',
            farmerId: 'farmer-001',
            amount: 15000.0,
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<PaymentInitiated>());
        expect(
          (states[1] as PaymentInitiated).payment.paymentId,
          'esc-001',
        );

        await subscription.cancel();
        await bloc.close();
      });

      test('emits [Loading, PaymentQueued] when offline', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => false);

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(
          const InitiatePayment(
            deliveryId: 'del-001',
            buyerId: 'buyer-001',
            farmerId: 'farmer-001',
            amount: 15000.0,
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<PaymentQueued>());

        await subscription.cancel();
        await bloc.close();
      });

      test('emits [Loading, EscrowError] on validation failure', () async {
        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(
          const InitiatePayment(
            deliveryId: 'del-001',
            buyerId: 'buyer-001',
            farmerId: 'farmer-001',
            amount: 50.0, // Below minimum
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<EscrowError>());
        expect((states[1] as EscrowError).code, 'ESCROW_001');

        await subscription.cancel();
        await bloc.close();
      });

      test('emits [Loading, EscrowError] on self-trading', () async {
        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(
          const InitiatePayment(
            deliveryId: 'del-001',
            buyerId: 'user-001',
            farmerId: 'user-001',
            amount: 15000.0,
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<EscrowError>());

        await subscription.cancel();
        await bloc.close();
      });
    });

    // ── LoadEscrowDetail tests ──

    group('LoadEscrowDetail', () {
      test('emits [Loading, EscrowDetailLoaded] when found', () async {
        await _insertTestPayment(isar);

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(const LoadEscrowDetail(escrowId: 'esc-001'));
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<EscrowDetailLoaded>());
        expect(
          (states[1] as EscrowDetailLoaded).payment.paymentId,
          'esc-001',
        );

        await subscription.cancel();
        await bloc.close();
      });

      test('emits [Loading, EscrowError] when not found', () async {
        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(const LoadEscrowDetail(escrowId: 'nonexistent'));
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<EscrowError>());
        expect((states[1] as EscrowError).code, 'ESCROW_016');

        await subscription.cancel();
        await bloc.close();
      });
    });

    // ── LoadPaymentHistory tests ──

    group('LoadPaymentHistory', () {
      test('emits [Loading, PaymentHistoryLoaded] with payments and intents',
          () async {
        await _insertTestPayment(isar);
        await _insertTestIntent(isar);

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(const LoadPaymentHistory(userId: 'farmer-001'));
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<PaymentHistoryLoaded>());
        final loaded = states[1] as PaymentHistoryLoaded;
        expect(loaded.payments.length, 1);
        expect(loaded.pendingIntents.length, 1);

        await subscription.cancel();
        await bloc.close();
      });

      test('emits history with status filter', () async {
        await _insertTestPayment(
          isar,
        );
        await _insertTestPayment(
          isar,
          paymentId: 'esc-002',
          deliveryId: 'del-002',
          status: PaymentStatus.completed,
        );

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(
          const LoadPaymentHistory(
            userId: 'farmer-001',
            statusFilter: 'escrowed',
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        final loaded = states[1] as PaymentHistoryLoaded;
        expect(loaded.payments.length, 1);
        expect(loaded.statusFilter, 'escrowed');

        await subscription.cancel();
        await bloc.close();
      });
    });

    // ── RaisePaymentDispute tests ──

    group('RaisePaymentDispute', () {
      test('emits [Loading, DisputeRaisedSuccess] on success', () async {
        await _insertTestPayment(isar);

        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'status': 'disputed',
            'disputeId': 'disp-001',
            'disputedBy': 'buyer-001',
            'reason': 'damaged_goods',
            'disputeWindowEndsAt': '2026-02-21T10:00:00Z',
            'createdAt': '2026-02-19T10:00:00Z',
          },
        );

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(
          const RaisePaymentDispute(
            escrowId: 'esc-001',
            disputedBy: 'buyer-001',
            reason: 'damaged_goods',
            description: 'Bags were wet',
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<DisputeRaisedSuccess>());
        expect(
          (states[1] as DisputeRaisedSuccess).payment.status,
          PaymentStatus.disputed,
        );

        await subscription.cancel();
        await bloc.close();
      });

      test('emits [Loading, EscrowError] when offline', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => false);

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(
          const RaisePaymentDispute(
            escrowId: 'esc-001',
            disputedBy: 'buyer-001',
            reason: 'damaged_goods',
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<EscrowError>());
        expect((states[1] as EscrowError).code, 'ESCROW_OFFLINE');

        await subscription.cancel();
        await bloc.close();
      });
    });

    // ── CancelPaymentIntent tests ──

    group('CancelPaymentIntent', () {
      test('emits IntentCancelled on success', () async {
        final intent = await _insertTestIntent(isar);

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(CancelPaymentIntent(idempotencyKey: intent.idempotencyKey));
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 1);
        expect(states[0], isA<IntentCancelled>());

        await subscription.cancel();
        await bloc.close();
      });
    });

    // ── FilterPaymentHistory tests ──

    group('FilterPaymentHistory', () {
      test('emits filtered results', () async {
        await _insertTestPayment(
          isar,
        );
        await _insertTestPayment(
          isar,
          paymentId: 'esc-002',
          deliveryId: 'del-002',
          status: PaymentStatus.completed,
        );

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(
          const FilterPaymentHistory(
            userId: 'farmer-001',
            statusFilter: 'completed',
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 200));

        expect(states.length, 2);
        expect(states[0], isA<EscrowLoading>());
        expect(states[1], isA<PaymentHistoryLoaded>());
        final loaded = states[1] as PaymentHistoryLoaded;
        expect(loaded.payments.length, 1);
        expect(loaded.payments.first.status, PaymentStatus.completed);
        expect(loaded.statusFilter, 'completed');

        await subscription.cancel();
        await bloc.close();
      });
    });

    // ── LoadPendingIntents tests ──

    group('LoadPendingIntents', () {
      test('updates pending intents in existing PaymentHistoryLoaded state',
          () async {
        await _insertTestPayment(isar);

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        // First load history
        bloc.add(const LoadPaymentHistory(userId: 'farmer-001'));
        await Future<void>.delayed(const Duration(milliseconds: 200));

        // Now add a pending intent
        await _insertTestIntent(isar);

        // Load pending intents
        bloc.add(const LoadPendingIntents());
        await Future<void>.delayed(const Duration(milliseconds: 200));

        // Should have: Loading, HistoryLoaded, HistoryLoaded(with pending)
        expect(states.length, 3);
        final lastState = states[2] as PaymentHistoryLoaded;
        expect(lastState.pendingIntents.length, 1);

        await subscription.cancel();
        await bloc.close();
      });
    });

    // ── RefreshEscrow tests ──

    group('RefreshEscrow', () {
      test('emits EscrowDetailLoaded with updated data (no loading state)',
          () async {
        await _insertTestPayment(isar);

        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(() => apiClient.get<Map<String, dynamic>>(any())).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'deliveryId': 'del-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'amount': 15000.0,
            'commission': 375.0,
            'commissionRate': 0.025,
            'farmerPayout': 14625.0,
            'currency': 'ETB',
            'status': 'released',
            'createdAt': '2026-02-17T10:00:00Z',
            'updatedAt': '2026-02-19T10:00:00Z',
            'expiresAt': '2026-02-24T10:00:00Z',
            'version': 2,
          },
        );

        final bloc = EscrowBloc(repository: repository);
        final states = <EscrowState>[];
        final subscription = bloc.stream.listen(states.add);

        bloc.add(const RefreshEscrow(escrowId: 'esc-001'));
        await Future<void>.delayed(const Duration(milliseconds: 200));

        // Silent refresh: no Loading state emitted
        expect(states.length, 1);
        expect(states[0], isA<EscrowDetailLoaded>());
        expect(
          (states[0] as EscrowDetailLoaded).payment.status,
          PaymentStatus.released,
        );

        await subscription.cancel();
        await bloc.close();
      });
    });
  });

  // LoadEscrowByDelivery distinguishes a genuine "no escrow" (null result)
  // from a real request failure (ApiException). The latter must surface as a
  // distinct, retryable error — never as "payment not found". Uses a mock
  // repository so we can drive each ApiException path (the native repository
  // reads local Isar and never throws these).
  group('EscrowBloc.LoadEscrowByDelivery error mapping', () {
    late MockBaseEscrowRepository repository;

    setUp(() {
      repository = MockBaseEscrowRepository();
    });

    Payment buildPayment() => Payment()
      ..paymentId = 'esc-001'
      ..deliveryId = 'del-001'
      ..farmerId = 'farmer-001'
      ..buyerId = 'buyer-001'
      ..amount = 15000.0
      ..commission = 375.0
      ..commissionRate = 0.025
      ..farmerPayout = 14625.0
      ..status = PaymentStatus.escrowed
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..version = 1;

    Future<List<EscrowState>> run(EscrowBloc bloc) async {
      final states = <EscrowState>[];
      final subscription = bloc.stream.listen(states.add);
      bloc.add(const LoadEscrowByDelivery(deliveryId: 'del-001'));
      await Future<void>.delayed(const Duration(milliseconds: 100));
      await subscription.cancel();
      await bloc.close();
      return states;
    }

    test('emits [Loading, EscrowDetailLoaded] when an escrow exists', () async {
      when(() => repository.getEscrowByDelivery(any()))
          .thenAnswer((_) async => buildPayment());

      final states = await run(EscrowBloc(repository: repository));

      expect(states.length, 2);
      expect(states[0], isA<EscrowLoading>());
      expect(states[1], isA<EscrowDetailLoaded>());
    });

    test('emits ESCROW_016_DELIVERY when there is genuinely no escrow (null)',
        () async {
      when(() => repository.getEscrowByDelivery(any()))
          .thenAnswer((_) async => null);

      final states = await run(EscrowBloc(repository: repository));

      expect(states.length, 2);
      expect(states[1], isA<EscrowError>());
      expect((states[1] as EscrowError).code, 'ESCROW_016_DELIVERY');
    });

    test('maps a 403 to ESCROW_022 (not "payment not found")', () async {
      when(() => repository.getEscrowByDelivery(any()))
          .thenThrow(const AuthException('Forbidden', 403));

      final states = await run(EscrowBloc(repository: repository));

      expect(states[1], isA<EscrowError>());
      final error = states[1] as EscrowError;
      expect(error.code, 'ESCROW_022');
      expect(error.code, isNot('ESCROW_016_DELIVERY'));
    });

    test('maps a 401 to ESCROW_005', () async {
      when(() => repository.getEscrowByDelivery(any()))
          .thenThrow(const AuthException('Unauthorized'));

      final states = await run(EscrowBloc(repository: repository));

      expect((states[1] as EscrowError).code, 'ESCROW_005');
    });

    test('maps a 5xx to ESCROW_012', () async {
      when(() => repository.getEscrowByDelivery(any()))
          .thenThrow(const ServerException('Server error', 500));

      final states = await run(EscrowBloc(repository: repository));

      expect((states[1] as EscrowError).code, 'ESCROW_012');
    });

    test('maps a network failure to ESCROW_OFFLINE', () async {
      when(() => repository.getEscrowByDelivery(any()))
          .thenThrow(const NetworkException());

      final states = await run(EscrowBloc(repository: repository));

      expect((states[1] as EscrowError).code, 'ESCROW_OFFLINE');
    });
  });

  // LoadEscrowDetail (by id) applies the same distinction as by-delivery, and
  // RefreshEscrow must stay silent on a transient failure rather than blowing
  // away the displayed escrow with an error screen.
  group('EscrowBloc.LoadEscrowDetail / RefreshEscrow error mapping', () {
    late MockBaseEscrowRepository repository;

    setUp(() {
      repository = MockBaseEscrowRepository();
    });

    Future<List<EscrowState>> run(EscrowBloc bloc, EscrowEvent event) async {
      final states = <EscrowState>[];
      final subscription = bloc.stream.listen(states.add);
      bloc.add(event);
      await Future<void>.delayed(const Duration(milliseconds: 100));
      await subscription.cancel();
      await bloc.close();
      return states;
    }

    test('LoadEscrowDetail emits ESCROW_016 when the escrow is absent (null)',
        () async {
      when(() => repository.getEscrow(any())).thenAnswer((_) async => null);

      final states = await run(
        EscrowBloc(repository: repository),
        const LoadEscrowDetail(escrowId: 'esc-001'),
      );

      expect((states[1] as EscrowError).code, 'ESCROW_016');
    });

    test('LoadEscrowDetail maps a 403 to ESCROW_022 (not "payment not found")',
        () async {
      when(() => repository.getEscrow(any()))
          .thenThrow(const AuthException('Forbidden', 403));

      final states = await run(
        EscrowBloc(repository: repository),
        const LoadEscrowDetail(escrowId: 'esc-403'),
      );

      final error = states[1] as EscrowError;
      expect(error.code, 'ESCROW_022');
      expect(error.code, isNot('ESCROW_016'));
    });

    test('LoadEscrowDetail maps a network failure to ESCROW_OFFLINE', () async {
      when(() => repository.getEscrow(any()))
          .thenThrow(const NetworkException());

      final states = await run(
        EscrowBloc(repository: repository),
        const LoadEscrowDetail(escrowId: 'esc-net'),
      );

      expect((states[1] as EscrowError).code, 'ESCROW_OFFLINE');
    });

    test('RefreshEscrow stays silent (no state) when refresh returns null',
        () async {
      when(() => repository.refreshEscrow(any()))
          .thenAnswer((_) async => null);

      final states = await run(
        EscrowBloc(repository: repository),
        const RefreshEscrow(escrowId: 'esc-001'),
      );

      // A null refresh (transient failure / no update) emits nothing, leaving
      // the currently displayed escrow intact.
      expect(states, isEmpty);
    });
  });
}
