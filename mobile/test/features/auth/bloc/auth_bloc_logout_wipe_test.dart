import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/database/schemas/app_notification.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/message.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/database/schemas/rating.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

class MockAuthService extends Mock implements AuthService {}

class MockOfflineAuthCache extends Mock implements OfflineAuthCache {}

const _phone = '+251911234567';
const _me = 'user-1';
const _other = 'user-2';
const _tokens = AuthTokens(
  accessToken: 'access-1',
  refreshToken: 'refresh-1',
  idToken: 'id-1',
);

final Map<String, String> _storage = {};

Message _message(String id, String from, String to) => Message()
  ..messageId = id
  ..conversationId = 'order:o1:$from:$to'
  ..contextType = 'order'
  ..contextId = 'o1'
  ..senderId = from
  ..recipientId = to
  ..body = 'hello'
  ..createdAt = DateTime.utc(2026, 9, 26)
  ..version = 1;

Rating _rating(String id, String rater, String ratee) => Rating()
  ..ratingId = id
  ..orderId = 'o-$id'
  ..raterId = rater
  ..rateeId = ratee
  ..rateeRole = 'farmer'
  ..stars = 5
  ..createdAt = DateTime.utc(2026, 9, 26)
  ..updatedAt = DateTime.utc(2026, 9, 26)
  ..version = 1;

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late Isar isar;
  late Directory dir;
  late MockAuthService service;
  late MockOfflineAuthCache cache;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
    registerFallbackValue(UserRole.farmer);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (MethodCall call) async {
        final args = (call.arguments as Map?)?.cast<dynamic, dynamic>();
        switch (call.method) {
          case 'write':
            final value = args!['value'] as String?;
            if (value != null) _storage[args['key'] as String] = value;
            return null;
          case 'read':
            return _storage[args!['key'] as String];
          case 'delete':
            _storage.remove(args!['key'] as String);
            return null;
          default:
            return null;
        }
      },
    );
  });

  setUp(() async {
    _storage.clear();
    dir = Directory.systemTemp.createTempSync('logout_wipe_test_');
    isar = await Isar.open(
      [
        DeliverySchema,
        PaymentSchema,
        OrderSchema,
        PaymentIntentSchema,
        MessageSchema,
        RatingSchema,
        SyncOperationSchema,
        AppNotificationSchema,
      ],
      directory: dir.path,
      name: 'logout_wipe_${DateTime.now().microsecondsSinceEpoch}',
    );
    service = MockAuthService();
    cache = MockOfflineAuthCache();
    when(() => cache.clearCredentials(any())).thenAnswer((_) async {});
    when(() => service.login(phoneNumber: _phone, password: 'Secret123'))
        .thenAnswer(
      (_) async => AuthResult.success(
        _tokens,
        userId: _me,
        role: UserRole.buyer,
        userName: 'Abebe',
        woreda: 'Adama',
      ),
    );
    when(
      () => service.logout(
        phoneNumber: any(named: 'phoneNumber'),
        accessToken: any(named: 'accessToken'),
        refreshToken: any(named: 'refreshToken'),
        clearCache: any(named: 'clearCache'),
      ),
    ).thenAnswer((_) async {});
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (dir.existsSync()) dir.deleteSync(recursive: true);
  });

  Future<void> waitFor(AuthBloc bloc, bool Function(AuthState) done) async {
    if (done(bloc.state)) return;
    await bloc.stream.firstWhere(done).timeout(const Duration(seconds: 5));
  }

  test('logout wipes the user\'s messages, ratings and queued sync ops',
      () async {
    await isar.writeTxn(() async {
      await isar.messages.putAll([
        _message('m-sent', _me, _other),
        _message('m-received', _other, _me),
        _message('m-unrelated', _other, 'user-3'),
      ]);
      await isar.ratings.putAll([
        _rating('r-given', _me, _other),
        _rating('r-got', _other, _me),
        _rating('r-unrelated', _other, 'user-3'),
      ]);
      // ESC-21: the inbox belongs to the user it was pulled for.
      await isar.appNotifications.putAll([
        _notification('n-mine', _me),
        _notification('n-other', _other),
      ]);
    });
    final queue = SyncQueue(isar);
    await queue.enqueue(
      userId: _me,
      entityType: 'order',
      entityId: 'o1',
      action: 'update',
      data: '{"status":"payment_submitted"}',
      operationId: 'op-mine',
    );
    await queue.enqueue(
      userId: _other,
      entityType: 'order',
      entityId: 'o2',
      action: 'update',
      data: '{"status":"payment_submitted"}',
      operationId: 'op-other',
    );

    final bloc = AuthBloc(
      authService: service,
      offlineCache: cache,
      isar: isar,
      sessionExpired: const Stream.empty(),
    );
    addTearDown(bloc.close);

    bloc.add(
      const AuthLoginRequested(phoneNumber: _phone, password: 'Secret123'),
    );
    await waitFor(bloc, (s) => s is AuthAuthenticated);

    bloc.add(const AuthLogoutRequested());
    await waitFor(bloc, (s) => s is AuthUnauthenticated);

    final messages = await isar.messages.where().findAll();
    expect(messages.map((m) => m.messageId), ['m-unrelated']);

    final ratings = await isar.ratings.where().findAll();
    expect(ratings.map((r) => r.ratingId), ['r-unrelated']);

    final ops = await isar.syncOperations.where().findAll();
    expect(ops.map((o) => o.operationId), ['op-other']);

    final inbox = await isar.appNotifications.where().findAll();
    expect(inbox.map((n) => n.notificationId), ['n-other']);
  });

  // ESC-12: sync refusals kept for a listener belong to the leaving user.
  test('logout drops sync refusals kept for the next listener', () async {
    final events = SecurityEvents.forTesting();
    final bloc = AuthBloc(
      authService: service,
      offlineCache: cache,
      isar: isar,
      sessionExpired: const Stream.empty(),
      securityEvents: events,
    );
    addTearDown(bloc.close);

    bloc.add(
      const AuthLoginRequested(phoneNumber: _phone, password: 'Secret123'),
    );
    await waitFor(bloc, (s) => s is AuthAuthenticated);
    events.emit(
      const SyncOperationRejected(
        entityType: 'order',
        entityId: 'o1',
        action: 'update',
        message: 'ORDER_VALIDATION: no',
        userId: _me,
      ),
    );

    bloc.add(const AuthLogoutRequested());
    await waitFor(bloc, (s) => s is AuthUnauthenticated);

    final received = <SecurityEvent>[];
    final sub = events.events.listen(received.add);
    await Future<void>.delayed(Duration.zero);
    await sub.cancel();
    expect(received, isEmpty);
  });
}

AppNotification _notification(String id, String userId) => AppNotification()
  ..notificationId = id
  ..userId = userId
  ..type = 'order_confirmed'
  ..entity = 'order'
  ..entityId = 'o1'
  ..orderId = 'o1'
  ..params = '{}'
  ..createdAt = DateTime.utc(2026, 10, 27);
