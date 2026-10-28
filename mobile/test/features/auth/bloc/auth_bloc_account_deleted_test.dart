import 'dart:async';
import 'dart:convert';
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
import 'package:wereda_connect/core/database/schemas/buyer_intent.dart';
import 'package:wereda_connect/core/database/schemas/conversation_read.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/message.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/database/schemas/rating.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

import '../../../helpers/wait_for.dart';

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

final _at = DateTime.utc(2026, 10, 2);

CropListing _crop(String id, String farmerId) => CropListing()
  ..listingId = id
  ..farmerId = farmerId
  ..cropType = 'teff'
  ..quantity = 10
  ..pricePerKg = 45
  ..location = (LatLng()
    ..latitude = 9
    ..longitude = 38.7)
  ..locationName = 'Adama'
  ..description = ''
  ..status = ListingStatus.draft
  ..createdAt = _at
  ..updatedAt = _at
  ..version = 1;

Trip _trip(String id, String truckerId) => Trip()
  ..tripId = id
  ..truckerId = truckerId
  ..truckerName = 'Trucker'
  ..truckerPhone = ''
  ..fromWoreda = 'Adama'
  ..toWoreda = 'Addis Ababa'
  ..departureDate = _at
  ..capacity = 50
  ..pricePerQuintal = 100
  ..status = TripStatus.open
  ..matchedListings = <String>[]
  ..createdAt = _at
  ..updatedAt = _at
  ..version = 1;

Order _order(String id, {String b = _other, String f = _other, String? t}) =>
    Order()
      ..orderId = id
      ..listingId = 'l-$id'
      ..buyerId = b
      ..farmerId = f
      ..truckerId = t
      ..buyerName = 'Buyer'
      ..farmerName = 'Farmer'
      ..cropType = 'teff'
      ..quantity = 10
      ..pricePerUnit = 45
      ..totalPrice = 450
      ..status = OrderStatus.settled
      ..createdAt = _at
      ..version = 1;

Delivery _delivery(String id, {String f = _other, String t = _other}) =>
    Delivery()
      ..deliveryId = id
      ..tripId = 't-$id'
      ..listingId = 'l-$id'
      ..farmerId = f
      ..farmerName = 'Farmer'
      ..truckerId = t
      ..truckerName = 'Trucker'
      ..buyerId = _other
      ..buyerName = 'Buyer'
      ..qrToken = 'qr-$id'
      ..status = DeliveryStatus.delivered
      ..createdAt = _at
      ..updatedAt = _at
      ..version = 1;

Payment _payment(String id, String buyerId) => Payment()
  ..paymentId = id
  ..deliveryId = 'd-$id'
  ..farmerId = _other
  ..buyerId = buyerId
  ..amount = 100
  ..commission = 2
  ..commissionRate = 0.02
  ..farmerPayout = 98
  ..status = PaymentStatus.initiated
  ..createdAt = _at
  ..updatedAt = _at
  ..version = 1;

PaymentIntent _intent(String key, {String b = _other, String f = _other}) =>
    PaymentIntent()
      ..idempotencyKey = key
      ..deliveryId = 'd-$key'
      ..buyerId = b
      ..farmerId = f
      ..amount = 100
      ..status = IntentStatus.pendingSync
      ..createdAt = _at;

BuyerIntent _buyerIntent(String id, String buyerId) => BuyerIntent()
  ..intentId = id
  ..buyerId = buyerId
  ..cropType = 'teff'
  ..quantityNeeded = 10
  ..maxPricePerKg = 50
  ..deliveryWoreda = 'Adama'
  ..status = BuyerIntentStatus.active
  ..createdAt = _at
  ..updatedAt = _at
  ..version = 1;

Message _message(String id, String from, String to) => Message()
  ..messageId = id
  ..conversationId = 'order:o1:$from:$to'
  ..contextType = 'order'
  ..contextId = 'o1'
  ..senderId = from
  ..recipientId = to
  ..body = 'hello'
  ..createdAt = _at
  ..version = 1;

Rating _rating(String id, String rater, String ratee) => Rating()
  ..ratingId = id
  ..orderId = 'o-$id'
  ..raterId = rater
  ..rateeId = ratee
  ..rateeRole = 'farmer'
  ..stars = 5
  ..createdAt = _at
  ..updatedAt = _at
  ..version = 1;

ConversationRead _read(String conversationId) => ConversationRead()
  ..conversationId = conversationId
  ..lastReadAt = _at;

User _user(String id, String phone) => User()
  ..userId = id
  ..phoneNumber = phone
  ..name = 'Name'
  ..role = UserRole.farmer
  ..woreda = 'Adama'
  ..createdAt = _at
  ..updatedAt = _at
  ..version = 1;

const _sessionKeys = [
  'current_user_phone',
  AuthBloc.currentUserIdKey,
  'access_token',
  'id_token',
  'refresh_token',
];

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late Isar isar;
  late Directory dir;
  late MockAuthService service;
  late MockOfflineAuthCache cache;
  late int tokenDeletes;
  late List<String> forgottenMarkers;
  late List<String> forgottenUsage;
  late List<String> forgottenTracks;
  late StreamController<String> accountGone;

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
    tokenDeletes = 0;
    forgottenMarkers = [];
    forgottenUsage = [];
    forgottenTracks = [];
    accountGone = StreamController<String>.broadcast();
    dir = Directory.systemTemp.createTempSync('account_deleted_test_');
    isar = await Isar.open(
      [
        UserSchema,
        CropListingSchema,
        TripSchema,
        DeliverySchema,
        OrderSchema,
        PaymentSchema,
        PaymentIntentSchema,
        SyncOperationSchema,
        BuyerIntentSchema,
        RatingSchema,
        MessageSchema,
        ConversationReadSchema,
        AppNotificationSchema,
      ],
      directory: dir.path,
      name: 'account_deleted_${DateTime.now().microsecondsSinceEpoch}',
    );
    service = MockAuthService();
    cache = MockOfflineAuthCache();
    when(() => cache.forgetAccount(any())).thenAnswer((_) async {});
    when(() => service.login(phoneNumber: _phone, password: 'Secret123'))
        .thenAnswer(
      (_) async => AuthResult.success(
        _tokens,
        userId: _me,
        role: UserRole.farmer,
        userName: 'Abebe',
        woreda: 'Adama',
      ),
    );
  });

  tearDown(() async {
    await accountGone.close();
    await isar.close(deleteFromDisk: true);
    if (dir.existsSync()) dir.deleteSync(recursive: true);
  });

  Future<void> waitForState(
    AuthBloc bloc,
    bool Function(AuthState) done,
  ) async {
    if (done(bloc.state)) return;
    await bloc.stream.firstWhere(done).timeout(const Duration(seconds: 5));
  }

  AuthBloc build({
    Isar? database,
    Future<void> Function()? pushTokenDeleter,
  }) {
    final bloc = AuthBloc(
      authService: service,
      offlineCache: cache,
      isar: database,
      pushTokenDeleter: pushTokenDeleter ?? () async => tokenDeletes++,
      forgetReadMarkers: (userId) async => forgottenMarkers.add(userId),
      forgetDataUsage: (userId) async => forgottenUsage.add(userId),
      forgetTrackSnapshots: (userId) async => forgottenTracks.add(userId),
      sessionExpired: const Stream.empty(),
      accountGone: accountGone.stream,
    );
    addTearDown(bloc.close);
    return bloc;
  }

  Future<void> signIn(AuthBloc bloc) async {
    bloc.add(
      const AuthLoginRequested(phoneNumber: _phone, password: 'Secret123'),
    );
    await waitForState(bloc, (s) => s is AuthAuthenticated);
  }

  Future<void> seed() async {
    await isar.writeTxn(() async {
      await isar.cropListings
          .putAll([_crop('c-me', _me), _crop('c-x', _other)]);
      await isar.trips.putAll([_trip('t-me', _me), _trip('t-x', _other)]);
      await isar.orders.putAll([
        _order('o-buyer', b: _me),
        _order('o-farmer', f: _me),
        _order('o-trucker', t: _me),
        _order('o-x', t: 'user-3'),
      ]);
      await isar.deliverys.putAll([
        _delivery('d-farmer', f: _me),
        _delivery('d-trucker', t: _me),
        _delivery('d-x'),
      ]);
      await isar.payments
          .putAll([_payment('p-me', _me), _payment('p-x', _other)]);
      await isar.paymentIntents.putAll([
        _intent('pi-buyer', b: _me),
        _intent('pi-farmer', f: _me),
        _intent('pi-x'),
      ]);
      await isar.buyerIntents
          .putAll([_buyerIntent('bi-me', _me), _buyerIntent('bi-x', _other)]);
      await isar.messages.putAll([
        _message('m-sent', _me, _other),
        _message('m-got', _other, _me),
        _message('m-x', _other, 'user-3'),
      ]);
      await isar.ratings.putAll([
        _rating('r-given', _me, _other),
        _rating('r-got', _other, _me),
        _rating('r-x', _other, 'user-3'),
      ]);
      await isar.conversationReads.putAll([
        _read('order:o1:$_me:$_other'),
        _read('crop:c-x:$_other:$_me'),
        _read('order:o2:$_other:user-3'),
      ]);
      await isar.users
          .putAll([_user(_me, _phone), _user(_other, '+251911000002')]);
    });
    final queue = SyncQueue(isar);
    await queue.enqueue(
      userId: _me,
      entityType: 'cropListing',
      entityId: 'c-me',
      action: 'create',
      data: '{}',
      operationId: 'op-me',
    );
    await queue.enqueue(
      userId: _other,
      entityType: 'order',
      entityId: 'o-x',
      action: 'update',
      data: '{}',
      operationId: 'op-x',
    );
  }

  test('wipes every row of the deleted account and keeps other accounts',
      () async {
    await seed();
    final bloc = build(database: isar);
    await signIn(bloc);
    _storage['device_id'] = 'device-1';

    bloc.add(const AuthAccountDeleted(userId: _me));
    await waitForState(bloc, (s) => s is AuthAccountWiped);

    expect(bloc.state, isA<AuthUnauthenticated>());
    expect(
      (await isar.cropListings.where().findAll()).map((c) => c.listingId),
      ['c-x'],
    );
    expect((await isar.trips.where().findAll()).map((t) => t.tripId), ['t-x']);
    expect(
      (await isar.orders.where().findAll()).map((o) => o.orderId),
      ['o-x'],
    );
    expect(
      (await isar.deliverys.where().findAll()).map((d) => d.deliveryId),
      ['d-x'],
    );
    expect(
      (await isar.payments.where().findAll()).map((p) => p.paymentId),
      ['p-x'],
    );
    expect(
      (await isar.paymentIntents.where().findAll())
          .map((p) => p.idempotencyKey),
      ['pi-x'],
    );
    expect(
      (await isar.buyerIntents.where().findAll()).map((i) => i.intentId),
      ['bi-x'],
    );
    expect(
      (await isar.messages.where().findAll()).map((m) => m.messageId),
      ['m-x'],
    );
    expect(
      (await isar.ratings.where().findAll()).map((r) => r.ratingId),
      ['r-x'],
    );
    expect(
      (await isar.syncOperations.where().findAll()).map((o) => o.operationId),
      ['op-x'],
    );
    expect(
      (await isar.conversationReads.where().findAll())
          .map((r) => r.conversationId),
      ['order:o2:$_other:user-3'],
    );
    expect(
      (await isar.users.where().findAll()).map((u) => u.userId),
      [_other],
    );

    // Secure storage (auth slots), offline cache, push token, read markers.
    for (final key in _sessionKeys) {
      expect(_storage.containsKey(key), isFalse, reason: key);
    }
    expect(_storage['device_id'], 'device-1');
    verify(() => cache.forgetAccount(_phone)).called(1);
    verifyNever(() => cache.clearCredentials(any()));
    expect(tokenDeletes, 1);
    expect(forgottenMarkers, [_me]);
    // LT-10: the stored live trip snapshots go with the account.
    expect(forgottenTracks, [_me]);

    // The server already ended the sessions and removed the device tokens.
    verifyNever(
      () => service.logout(
        phoneNumber: any(named: 'phoneNumber'),
        accessToken: any(named: 'accessToken'),
        refreshToken: any(named: 'refreshToken'),
        clearCache: any(named: 'clearCache'),
      ),
    );
    verifyNever(() => service.unregisterDeviceToken(any()));
  });

  test('web wiring (no Isar, no push): clears the session and the cache',
      () async {
    final bloc = AuthBloc(
      authService: service,
      offlineCache: cache,
      forgetReadMarkers: (userId) async => forgottenMarkers.add(userId),
      forgetDataUsage: (userId) async => forgottenUsage.add(userId),
      sessionExpired: const Stream.empty(),
      accountGone: const Stream.empty(),
    );
    addTearDown(bloc.close);
    await signIn(bloc);

    bloc.add(const AuthAccountDeleted(userId: _me));
    await waitForState(bloc, (s) => s is AuthAccountWiped);

    for (final key in _sessionKeys) {
      expect(_storage.containsKey(key), isFalse, reason: key);
    }
    verify(() => cache.forgetAccount(_phone)).called(1);
    expect(forgottenMarkers, [_me]);
  });

  test('a failing step never keeps the user signed in', () async {
    await seed();
    when(() => cache.forgetAccount(any()))
        .thenThrow(Exception('storage broken'));
    final bloc = build(
      database: isar,
      pushTokenDeleter: () async => throw Exception('firebase down'),
    );
    await signIn(bloc);

    bloc.add(const AuthAccountDeleted(userId: _me));
    await waitForState(bloc, (s) => s is AuthAccountWiped);

    for (final key in _sessionKeys) {
      expect(_storage.containsKey(key), isFalse, reason: key);
    }
    expect(
      (await isar.cropListings.where().findAll()).map((c) => c.listingId),
      ['c-x'],
    );
  });

  // Review item 6: the answer came after that account signed out, or after
  // another one signed in. Only the deleted account's local data goes.
  test(
      'a deleted account that is not signed in: its rows go, the session '
      'and push token of the signed-in one stay', () async {
    await seed();
    final bloc = build(database: isar);
    await signIn(bloc);

    bloc.add(
      const AuthAccountDeleted(userId: _other, phoneNumber: '+251911000002'),
    );
    await waitFor(() => forgottenUsage.isNotEmpty);

    expect(bloc.state, isA<AuthAuthenticated>());
    expect(
      (await isar.cropListings.where().findAll()).map((c) => c.listingId),
      ['c-me'],
    );
    expect(
      (await isar.users.where().findAll()).map((u) => u.userId),
      [_me],
    );
    expect(_storage['current_user_phone'], _phone);
    verify(() => cache.forgetAccount('+251911000002')).called(1);
    verifyNever(() => cache.forgetAccount(_phone));
    expect(tokenDeletes, 0);
    expect(forgottenMarkers, [_other]);
    expect(forgottenUsage, [_other]);
  });

  test('the same user signed out before the answer: its rows still go',
      () async {
    when(() => cache.clearCredentials(any())).thenAnswer((_) async {});
    when(
      () => service.logout(
        phoneNumber: any(named: 'phoneNumber'),
        accessToken: any(named: 'accessToken'),
        refreshToken: any(named: 'refreshToken'),
        clearCache: any(named: 'clearCache'),
      ),
    ).thenAnswer((_) async {});
    final bloc = build(database: isar);
    await signIn(bloc);
    bloc.add(const AuthLogoutRequested());
    await waitForState(bloc, (s) => s is AuthUnauthenticated);
    // Logout keeps listings, trips and buying intents; deletion does not.
    await seed();

    bloc.add(const AuthAccountDeleted(userId: _me, phoneNumber: _phone));
    await waitFor(() => forgottenUsage.isNotEmpty);

    expect(bloc.state, const AuthUnauthenticated());
    expect(
      (await isar.cropListings.where().findAll()).map((c) => c.listingId),
      ['c-x'],
    );
    expect(
      (await isar.buyerIntents.where().findAll()).map((i) => i.intentId),
      ['bi-x'],
    );
    verify(() => cache.forgetAccount(_phone)).called(1);
    expect(forgottenUsage, [_me]);
    expect(tokenDeletes, 0);
  });

  test('nobody signed in: no session change, no push token change', () async {
    final bloc = build(database: isar);

    bloc.add(const AuthAccountDeleted(userId: _me));
    await waitFor(() => forgottenUsage.isNotEmpty);

    expect(bloc.state, const AuthInitial());
    verifyNever(() => cache.forgetAccount(any()));
    expect(tokenDeletes, 0);
    expect(forgottenMarkers, [_me]);
  });

  // Review item 2: a 410 ACCOUNT_004 from /sync or any other write.
  group('410 ACCOUNT_004', () {
    for (final state in ['completed', 'cognito_pending', '']) {
      test('"$state" wipes the signed-in account', () async {
        await seed();
        final bloc = build(database: isar);
        await signIn(bloc);

        accountGone.add(state);
        await waitForState(bloc, (s) => s is AuthAccountWiped);

        expect(
          (await isar.cropListings.where().findAll()).map((c) => c.listingId),
          ['c-x'],
        );
        expect(_storage.containsKey('current_user_phone'), isFalse);
        verify(() => cache.forgetAccount(_phone)).called(1);
        expect(tokenDeletes, 1);
      });
    }

    test('in_progress keeps everything (the deletion can still abort)',
        () async {
      await seed();
      final bloc = build(database: isar);
      await signIn(bloc);

      accountGone.add('in_progress');
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(bloc.state, isA<AuthAuthenticated>());
      expect(await isar.cropListings.count(), 2);
      verifyNever(() => cache.forgetAccount(any()));
    });

    test('ignored when the stored session is another account (web tab)',
        () async {
      await seed();
      final bloc = build(database: isar);
      await signIn(bloc);
      // Another tab signed in a different account in the shared storage.
      _storage[AuthBloc.currentUserIdKey] = _other;

      accountGone.add('completed');
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(bloc.state, isA<AuthAuthenticated>());
      expect(await isar.cropListings.count(), 2);
      verifyNever(() => cache.forgetAccount(any()));
    });
  });

  // Review: the deletion request carries this token, never the interceptor's.
  group('bearerFor', () {
    String jwt(String sub) {
      String part(Map<String, dynamic> m) =>
          base64Url.encode(utf8.encode(jsonEncode(m))).replaceAll('=', '');
      return '${part({'alg': 'none'})}.${part({'sub': sub})}.sig';
    }

    test('the stored ID token only when it is the signed-in user\'s own',
        () async {
      final bloc = build(database: isar);
      expect(await bloc.bearerFor(_me), isNull, reason: 'signed out');

      await signIn(bloc);
      _storage['id_token'] = jwt(_me);
      expect(await bloc.bearerFor(_me), jwt(_me));
      expect(await bloc.bearerFor(_other), isNull);

      // Another tab signed in a different account: its token is stored now.
      _storage['id_token'] = jwt(_other);
      expect(await bloc.bearerFor(_me), isNull, reason: 'token of another');

      _storage['id_token'] = jwt(_me);
      _storage[AuthBloc.currentUserIdKey] = _other;
      expect(await bloc.bearerFor(_me), isNull, reason: 'stored user differs');

      _storage[AuthBloc.currentUserIdKey] = _me;
      _storage['id_token'] = 'not-a-jwt';
      expect(await bloc.bearerFor(_me), isNull, reason: 'unreadable token');
    });
  });

  group('isSignedInAs', () {
    test('true only for the signed-in user with a matching stored session',
        () async {
      final bloc = build(database: isar);
      expect(await bloc.isSignedInAs(_me), isFalse, reason: 'signed out');

      await signIn(bloc);
      expect(await bloc.isSignedInAs(_me), isTrue);
      expect(await bloc.isSignedInAs(_other), isFalse);

      _storage[AuthBloc.currentUserIdKey] = _other;
      expect(await bloc.isSignedInAs(_me), isFalse, reason: 'other tab');
    });
  });

  test('a plain logout still emits AuthUnauthenticated, not AuthAccountWiped',
      () async {
    when(() => cache.clearCredentials(any())).thenAnswer((_) async {});
    when(
      () => service.logout(
        phoneNumber: any(named: 'phoneNumber'),
        accessToken: any(named: 'accessToken'),
        refreshToken: any(named: 'refreshToken'),
        clearCache: any(named: 'clearCache'),
      ),
    ).thenAnswer((_) async {});
    final bloc = build(database: isar);
    await signIn(bloc);

    bloc.add(const AuthLogoutRequested());
    await waitForState(bloc, (s) => s is AuthUnauthenticated);

    expect(bloc.state, const AuthUnauthenticated());
    expect(bloc.state, isNot(isA<AuthAccountWiped>()));
    expect(tokenDeletes, 0);
    expect(forgottenMarkers, isEmpty);
  });
}
