import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class _MockApiClient extends Mock implements ApiClient {}

class _MockAuthBloc extends Mock implements AuthBloc {}

class _MockAuthService extends Mock implements AuthService {}

class _MockDeliveryBloc extends Mock implements DeliveryBloc {}

class _MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class _MockRatingBloc extends Mock implements RatingBloc {}

class _MockRatingRepository extends Mock implements BaseRatingRepository {}

class _FakeAuthEvent extends Fake implements AuthEvent {}

const _old = AuthTokens(accessToken: 'a1', refreshToken: 'r1', idToken: 'i1');
const _fresh = AuthTokens(accessToken: 'a2', refreshToken: 'r2', idToken: 'i2');

const _stepUpMessage =
    'AUTH_040: Please sign in again to cancel an order you accepted.';

/// An accepted trip order both the farmer and the trucker committed to, not
/// picked up yet: the farmer may cancel it and the trucker drop out, and the
/// server asks either for a fresh password (AUTH_040).
Map<String, dynamic> _orderDto() {
  final now = DateTime.now().toUtc();
  return {
    'orderId': 'order-001',
    'listingId': 'listing-001',
    'buyerId': 'buyer-001',
    'farmerId': 'farmer-001',
    'buyerName': 'Buyer Bee',
    'farmerName': 'Farmer Fikru',
    'cropType': 'teff',
    'quantity': 100.0,
    'pricePerUnit': 85.0,
    'totalPrice': 8500.0,
    'status': 'accepted',
    'createdAt': now.subtract(const Duration(days: 2)).toIso8601String(),
    'version': 4,
    'tripId': 'trip-001',
    'truckerId': 'trucker-001',
    'truckerName': 'Trucker Tesfaye',
    'fulfillment': 'trucker',
    'farmerResponse': 'accepted',
    'truckerResponse': 'accepted',
    'pickupDate': now.add(const Duration(days: 2)).toIso8601String(),
    'pickupDeadlineAt': now.add(const Duration(days: 3)).toIso8601String(),
    'contactOpen': true,
  };
}

/// Pump frames for the dialog and the requests behind it without waiting
/// for the focused password field's cursor to stop blinking (it never does).
Future<void> _settle(WidgetTester tester) async {
  for (var i = 0; i < 10; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

/// FUP-552: on web a farmer's cancel of an order they accepted, or a
/// trucker's drop-out, is sent at once, and the server may answer AUTH_040.
/// The order detail asks for the password in neutral words (no "payment")
/// and sends the same change again. Runs the real web order repository,
/// bloc, screen and password prompt; only the network and the login are
/// faked.
void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  late _MockApiClient api;
  late _MockAuthBloc authBloc;
  late _MockAuthService authService;
  late StreamController<AuthState> authStates;
  late _MockDeliveryBloc deliveryBloc;
  late _MockRatingBloc ratingBloc;
  late List<Map<String, dynamic>> sent;
  late List<String> answers;
  late int logins;

  setUpAll(() => registerFallbackValue(_FakeAuthEvent()));

  setUp(() {
    api = _MockApiClient();
    authBloc = _MockAuthBloc();
    authService = _MockAuthService();
    authStates = StreamController<AuthState>.broadcast();
    deliveryBloc = _MockDeliveryBloc();
    ratingBloc = _MockRatingBloc();
    sent = [];
    answers = [];
    logins = 0;

    when(() => api.get<Map<String, dynamic>>('/orders/order-001'))
        .thenAnswer((_) async => {'order': _orderDto()});
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      final data = invocation.namedArguments[#data] as Map<String, dynamic>;
      final op = (data['operations'] as List).single as Map<String, dynamic>;
      sent.add(op);
      final refuse = answers.isNotEmpty && answers.removeAt(0) == 'stepUp';
      return {
        'syncBatchId': data['syncBatchId'],
        'results': [
          if (refuse)
            {
              'operationId': op['operationId'],
              'status': 'error',
              'message': _stepUpMessage,
            }
          else
            {
              'operationId': op['operationId'],
              'status': 'success',
              'serverVersion': 5,
            },
        ],
      };
    });

    when(() => authBloc.stream).thenAnswer((_) => authStates.stream);
    when(() => authBloc.authService).thenReturn(authService);
    when(
      () => authService.loginOnline(
        phoneNumber: any(named: 'phoneNumber'),
        password: any(named: 'password'),
      ),
    ).thenAnswer((_) async {
      logins++;
      return AuthResult.success(_fresh, userId: 'someone');
    });

    final ratingRepo = _MockRatingRepository();
    when(() => ratingBloc.repository).thenReturn(ratingRepo);
    when(() => ratingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(
      () => ratingRepo.hasRated(
        orderId: any(named: 'orderId'),
        rateeId: any(named: 'rateeId'),
      ),
    ).thenAnswer((_) async => false);
    when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
    final deliveryRepo = _MockDeliveryRepository();
    when(() => deliveryBloc.repository).thenReturn(deliveryRepo);
    when(
      () => deliveryRepo.findActiveDeliveryForOrder(
        any(),
        userId: any(named: 'userId'),
        role: any(named: 'role'),
        orderCreatedAt: any(named: 'orderCreatedAt'),
      ),
    ).thenAnswer((_) async => null);
  });

  // Not awaited: a broadcast close() waits for listeners the fake-async test
  // zone has already torn down.
  tearDown(() {
    unawaited(authStates.close());
  });

  Future<void> show(WidgetTester tester, UserRole role, String userId) async {
    tester.view.physicalSize = const Size(1000, 6000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final signedIn = AuthAuthenticated(
      userId: userId,
      tokens: _old,
      role: role,
      phoneNumber: '+251900100002',
    );
    when(() => authBloc.state).thenReturn(signedIn);
    // The real bloc stores the tokens, then emits the updated session.
    when(() => authBloc.add(any())).thenAnswer((invocation) {
      final event = invocation.positionalArguments.first;
      if (event is AuthStepUpSucceeded) {
        scheduleMicrotask(
          () => authStates.add(signedIn.withTokens(event.tokens)),
        );
      }
    });
    // Made inside the test's zone, so its events run as the test pumps.
    final orderBloc = OrderBloc(repository: WebOrderRepository(api: api));
    addTearDown(orderBloc.close);
    await tester.pumpWidget(
      // Web: no MarketplaceSync, no sync queue; every change goes at once.
      Provider<MarketplaceSync?>.value(
        value: null,
        child: MultiBlocProvider(
          providers: [
            BlocProvider<OrderBloc>.value(value: orderBloc),
            BlocProvider<AuthBloc>.value(value: authBloc),
            BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
            BlocProvider<RatingBloc>.value(value: ratingBloc),
          ],
          child: const MaterialApp(
            locale: Locale('en'),
            localizationsDelegates: [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              GlobalWidgetsLocalizations.delegate,
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: OrderDetailScreen(orderId: 'order-001'),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> tap(WidgetTester tester, Finder finder) async {
    await tester.ensureVisible(finder);
    await tester.tap(finder);
    await _settle(tester);
  }

  /// The trucker drops out, giving a reason.
  Future<void> dropOut(WidgetTester tester) async {
    await show(tester, UserRole.trucker, 'trucker-001');
    await tap(tester, find.text(en.truckerDropOut));
    await tap(tester, find.text(en.declineReasonCapacity));
  }

  /// The farmer cancels the order they accepted.
  Future<void> farmerCancel(WidgetTester tester) async {
    await show(tester, UserRole.farmer, 'farmer-001');
    await tap(tester, find.widgetWithText(OutlinedButton, en.cancelOrder));
    await tap(tester, find.widgetWithText(FilledButton, en.cancelOrder));
  }

  Future<void> confirmPassword(WidgetTester tester) async {
    await tester.enterText(
      find.byKey(const Key('step_up_password_field')),
      'TestPass1dev',
    );
    await tester.pump();
    await tester.tap(find.byKey(const Key('step_up_confirm_button')));
    await _settle(tester);
  }

  void expectNeutralPrompt() {
    expect(find.text(en.stepUpTitle), findsOneWidget);
    expect(find.text(en.stepUpOtherMessage), findsOneWidget);
    expect(find.text(en.stepUpRequiredMessage), findsNothing);
  }

  List<Map<String, dynamic>> sentData() =>
      [for (final op in sent) op['data'] as Map<String, dynamic>];

  testWidgets(
      'trucker: a drop-out asks for the password in neutral words, '
      'then goes through', (tester) async {
    answers.add('stepUp');

    await dropOut(tester);

    expectNeutralPrompt();
    expect(sent, hasLength(1));

    await confirmPassword(tester);
    await tester.pumpAndSettle();

    expect(logins, 1);
    expect(find.text(en.stepUpTitle), findsNothing);
    // The same drop-out, sent once more after the prompt.
    expect(sent, hasLength(2));
    for (final data in sentData()) {
      expect(data['truckerResponse'], 'decline');
      expect(data['declineReason'], 'capacity');
    }
    expect(find.text(en.stepUpRequiredMessage), findsNothing);
    expect(find.text(en.stepUpOtherMessage), findsNothing);
  });

  testWidgets(
      'farmer: a cancel of an accepted order asks the same way, '
      'then goes through', (tester) async {
    answers.add('stepUp');

    await farmerCancel(tester);

    expectNeutralPrompt();
    expect(sent, hasLength(1));

    await confirmPassword(tester);
    await tester.pumpAndSettle();

    expect(logins, 1);
    expect(sent, hasLength(2));
    for (final data in sentData()) {
      expect(data['status'], 'cancelled');
    }
    expect(find.text(en.orderCancelledSuccess), findsOneWidget);
    expect(find.text(en.stepUpRequiredMessage), findsNothing);
  });

  testWidgets('a cancelled prompt sends nothing more and says why, neutrally',
      (tester) async {
    answers.add('stepUp');

    await dropOut(tester);
    expectNeutralPrompt();
    await tester.tap(find.widgetWithText(TextButton, en.cancel));
    await tester.pumpAndSettle();

    expect(logins, 0);
    expect(sent, hasLength(1));
    expect(find.text(en.stepUpTitle), findsNothing);
    // The snackbar: the same neutral words, never "this payment".
    expect(find.text(en.stepUpOtherMessage), findsOneWidget);
    expect(find.text(en.stepUpRequiredMessage), findsNothing);
    // The drop-out is offered again.
    expect(find.text(en.truckerDropOut), findsOneWidget);
  });

  testWidgets('a second AUTH_040 after the resend does not prompt again',
      (tester) async {
    answers.addAll(['stepUp', 'stepUp']);

    await farmerCancel(tester);
    await confirmPassword(tester);
    await tester.pumpAndSettle();

    expect(logins, 1);
    expect(sent, hasLength(2));
    expect(find.text(en.stepUpTitle), findsNothing);
    expect(find.text(en.stepUpOtherMessage), findsOneWidget);
    expect(find.text(en.stepUpRequiredMessage), findsNothing);
    expect(find.text(en.orderCancelledSuccess), findsNothing);
  });

  testWidgets('a drop-out the server takes at once never prompts',
      (tester) async {
    await dropOut(tester);
    await tester.pumpAndSettle();

    expect(sent, hasLength(1));
    expect(logins, 0);
    expect(find.text(en.stepUpTitle), findsNothing);
  });
}
