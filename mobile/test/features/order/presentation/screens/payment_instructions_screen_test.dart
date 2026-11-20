import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_checkout_screen.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class FakeOrderEvent extends Fake implements OrderEvent {}

/// Payment above 10,000 ETB blocked by the KYC gate or the Fayda step-up
/// (web path: the error comes straight back from /sync). Verifying as the
/// account's owner must send the same payment again; anything else must
/// leave it unsent. Also: the transfer reference is the full order id
/// (SIM-54).
void main() {
  const defaultOrderId = '24d367a5-0000-0000-0000-000000000000';
  const realAccount = '1000123456789';
  const cbeLabel = 'Commercial Bank of Ethiopia (CBE)';

  late MockOrderBloc bloc;
  late StreamController<OrderState> states;
  late List<SubmitPayment> submitted;

  setUpAll(() => registerFallbackValue(FakeOrderEvent()));

  setUp(() {
    bloc = MockOrderBloc();
    states = StreamController<OrderState>.broadcast();
    submitted = [];
    when(() => bloc.state).thenReturn(const OrderInitial());
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    when(() => bloc.add(any())).thenAnswer((inv) {
      final event = inv.positionalArguments.first;
      if (event is SubmitPayment) submitted.add(event);
    });
  });

  tearDown(() => unawaited(states.close()));

  Order order({String id = defaultOrderId}) => Order()
    ..orderId = id
    ..listingId = 'listing-1'
    ..buyerId = 'buyer-1'
    ..farmerId = 'farmer-1'
    ..cropType = 'teff'
    ..quantity = 1000
    ..pricePerUnit = 94.5
    ..totalPrice = 94500
    ..status = OrderStatus.created
    ..createdAt = DateTime(2026, 9, 28)
    ..version = 1;

  Future<void> open(
    WidgetTester tester, {
    bool verifies = true,
    String orderId = defaultOrderId,
    String? cbeAccount,
  }) async {
    await tester.pumpWidget(
      BlocProvider<OrderBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: PaymentInstructionsScreen(
            order: order(id: orderId),
            cbeAccount: cbeAccount,
            kycVerificationScreen: (context) => Scaffold(
              body: TextButton(
                onPressed: () => Navigator.of(context).pop(verifies),
                child: const Text('fayda screen'),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> payAndGetRejected(
    WidgetTester tester,
    String message, {
    AppErrorCode code = AppErrorCode.kycRequired,
  }) async {
    await tester.ensureVisible(find.text('Submit Payment'));
    await tester.tap(find.text('Submit Payment'));
    await tester.pump();
    expect(submitted, hasLength(1));
    states.add(OrderError(message: message, code: code));
    await tester.pumpAndSettle();
  }

  // PaymentOfflineM (BUG-19): a payment that could not be sent for lack of
  // a network takes the page and offers the same payment again.
  group('no network while paying', () {
    for (final code in [AppErrorCode.network, AppErrorCode.timeout]) {
      testWidgets('${code.name}: the offline state, Retry sends it again',
          (tester) async {
        await open(tester);
        await payAndGetRejected(tester, 'x', code: code);

        expect(find.byKey(const Key('payment_offline_state')), findsOneWidget);
        expect(find.text('No internet connection'), findsOneWidget);
        expect(
          find.text(
            'Payment needs a connection. Your order is saved on this phone.',
          ),
          findsOneWidget,
        );
        // The form and its pinned actions give way to the state.
        expect(find.text('Submit Payment'), findsNothing);
        expect(find.byType(SnackBar), findsNothing);

        await tester.tap(find.widgetWithText(FilledButton, 'Retry'));
        await tester.pump();

        // The same payment, once more.
        expect(submitted, hasLength(2));
        expect(submitted.last.orderId, submitted.first.orderId);
        expect(submitted.last.paymentMethod, submitted.first.paymentMethod);
        expect(
          submitted.last.cbeTransactionId,
          submitted.first.cbeTransactionId,
        );
      });
    }

    testWidgets('another refusal is still a message, not the offline state',
        (tester) async {
      await open(tester);
      await payAndGetRejected(tester, 'x', code: AppErrorCode.server);

      expect(find.byKey(const Key('payment_offline_state')), findsNothing);
      expect(find.byType(SnackBar), findsOneWidget);
      expect(find.text('Submit Payment'), findsOneWidget);
    });

    testWidgets('Back to order leaves the payment', (tester) async {
      final navigator = GlobalKey<NavigatorState>();
      await tester.pumpWidget(
        BlocProvider<OrderBloc>.value(
          value: bloc,
          child: MaterialApp(
            navigatorKey: navigator,
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: const Scaffold(body: Text('the order')),
          ),
        ),
      );
      // The order detail is the page underneath.
      unawaited(
        navigator.currentState!.push(
          MaterialPageRoute<void>(
            builder: (_) => PaymentInstructionsScreen(
              order: order(),
              fromOrderDetail: true,
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await payAndGetRejected(tester, 'x', code: AppErrorCode.network);

      await tester.tap(find.widgetWithText(OutlinedButton, 'Back to order'));
      await tester.pumpAndSettle();

      expect(find.byType(PaymentInstructionsScreen), findsNothing);
      expect(find.text('the order'), findsOneWidget);
      expect(submitted, hasLength(1));
    });
  });

  testWidgets('above 10,000 ETB: a Fayda login resubmits the same payment',
      (tester) async {
    await open(tester);
    await payAndGetRejected(
      tester,
      "SyncRejectedException: AUTH_041: Confirm it's you with Fayda",
      code: AppErrorCode.faydaStepUpRequired,
    );

    // The Fayda prompt, not the password one.
    expect(find.byKey(const Key('step_up_password_field')), findsNothing);
    await tester.tap(find.byKey(const Key('fayda_step_up_verify_button')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('fayda screen'));
    await tester.pumpAndSettle();

    expect(submitted, hasLength(2));
    expect(submitted[1].orderId, submitted[0].orderId);
    expect(submitted[1].cbeTransactionId, submitted[0].cbeTransactionId);
  });

  testWidgets('above 10,000 ETB: a failed Fayda login sends nothing',
      (tester) async {
    await open(tester, verifies: false);
    await payAndGetRejected(
      tester,
      "AUTH_041: Confirm it's you with Fayda",
      code: AppErrorCode.faydaStepUpRequired,
    );

    await tester.tap(find.byKey(const Key('fayda_step_up_verify_button')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('fayda screen'));
    await tester.pumpAndSettle();

    expect(submitted, hasLength(1));
  });

  testWidgets('verified as the owner: the same payment is submitted again',
      (tester) async {
    await open(tester);
    await payAndGetRejected(
      tester,
      'SyncRejectedException: ORDER_KYC_REQUIRED: buyer_fayda: Verify',
    );

    await tester.tap(find.byKey(const Key('kyc_verify_button')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('fayda screen'));
    await tester.pumpAndSettle();

    expect(submitted, hasLength(2));
    expect(submitted[1].orderId, submitted[0].orderId);
    expect(submitted[1].paymentMethod, submitted[0].paymentMethod);
    expect(submitted[1].cbeTransactionId, submitted[0].cbeTransactionId);
  });

  testWidgets('verification failed: nothing is resubmitted, says why',
      (tester) async {
    await open(tester, verifies: false);
    await payAndGetRejected(
      tester,
      'ORDER_KYC_REQUIRED: buyer_fayda: Verify',
    );

    await tester.tap(find.byKey(const Key('kyc_verify_button')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('fayda screen'));
    await tester.pumpAndSettle();

    expect(submitted, hasLength(1));
    expect(
      find.text('Payment not sent. Your identity was not verified.'),
      findsOneWidget,
    );
  });

  testWidgets('cancelled: nothing is resubmitted', (tester) async {
    await open(tester);
    await payAndGetRejected(
      tester,
      'ORDER_KYC_REQUIRED: buyer_phone: Verify your phone',
    );

    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();

    expect(submitted, hasLength(1));
  });

  testWidgets('farmer not verified: no Fayda for the buyer, nothing resent',
      (tester) async {
    await open(tester);
    await payAndGetRejected(
      tester,
      'ORDER_KYC_REQUIRED: farmer_fayda: The farmer must verify',
    );

    expect(find.byKey(const Key('kyc_verify_button')), findsNothing);
    expect(
      find.textContaining('need the farmer to have a verified Fayda ID'),
      findsOneWidget,
    );
    await tester.tap(find.byKey(const Key('kyc_ok_button')));
    await tester.pumpAndSettle();

    expect(submitted, hasLength(1));
  });

  // BUG-29: the demo screen opened on the bank transfer and showed the
  // placeholder account 1000XXXXXXXXXX as if a buyer could pay into it.
  group('payment method (BUG-29)', () {
    Future<void> submit(WidgetTester tester) async {
      await tester.ensureVisible(find.text('Submit Payment'));
      await tester.tap(find.text('Submit Payment'));
      await tester.pump();
    }

    testWidgets('Telebirr is the default, also with a real CBE account',
        (tester) async {
      await open(tester, cbeAccount: realAccount);

      expect(find.text('Telebirr Payment'), findsOneWidget);
      expect(find.text(realAccount), findsNothing);
      await submit(tester);
      expect(submitted.single.paymentMethod, 'telebirr');
    });

    testWidgets('the shipped placeholder account hides the bank transfer',
        (tester) async {
      // No account passed: the configured one, which is the placeholder.
      await open(tester);

      expect(find.text(cbeLabel), findsNothing);
      expect(find.textContaining('1000XXXX'), findsNothing);
      expect(find.text('CBE Account Number'), findsNothing);
      expect(find.text('Telebirr Payment'), findsOneWidget);
      // The reference stays the full order id (SIM-54).
      expect(find.text(defaultOrderId), findsOneWidget);
      await submit(tester);
      expect(submitted.single.paymentMethod, 'telebirr');
    });

    for (final missing in ['', '   ', '1000XXXXXXXXXX', 'TBD', '12345']) {
      testWidgets('account "$missing" is not offered', (tester) async {
        await open(tester, cbeAccount: missing);
        expect(find.text(cbeLabel), findsNothing);
      });
    }

    testWidgets('a real account offers the bank transfer and shows it',
        (tester) async {
      await open(tester, cbeAccount: realAccount);

      expect(find.text(cbeLabel), findsOneWidget);
      await tester.ensureVisible(find.text(cbeLabel));
      await tester.tap(find.text(cbeLabel));
      await tester.pumpAndSettle();

      expect(find.text(realAccount), findsOneWidget);
      expect(find.textContaining('XXXX'), findsNothing);
      await submit(tester);
      expect(submitted.single.paymentMethod, 'cbe_transfer');
    });

    test('realCbeAccount keeps digits, spaces and hyphens only', () {
      expect(realCbeAccount('1000123456789'), '1000123456789');
      expect(realCbeAccount(' 1000 1234-56789 '), '1000 1234-56789');
      expect(realCbeAccount('1000XXXXXXXXXX'), isNull);
      expect(realCbeAccount(null), isNull);
    });
  });

  group('payment reference (SIM-54)', () {
    Future<void> submit(WidgetTester tester) async {
      await tester.ensureVisible(find.text('Submit Payment'));
      await tester.tap(find.text('Submit Payment'));
      await tester.pump();
    }

    /// The bank transfer, offered because a real account is configured.
    Future<void> chooseCbe(WidgetTester tester) async {
      await tester.ensureVisible(find.text(cbeLabel));
      await tester.tap(find.text(cbeLabel));
      await tester.pumpAndSettle();
      expect(find.text(realAccount), findsOneWidget);
    }

    /// The copy button on the detail row that shows [value].
    Finder copyButtonFor(String value) => find.descendant(
          of: find
              .ancestor(of: find.text(value), matching: find.byType(Row))
              .first,
          matching: find.byIcon(Icons.copy),
        );

    testWidgets('CBE card shows the full order id, not its first 8 chars',
        (tester) async {
      await open(tester, cbeAccount: realAccount);
      await chooseCbe(tester);

      expect(find.text(defaultOrderId), findsOneWidget);
      expect(find.text('24D367A5'), findsNothing);
    });

    testWidgets('Telebirr card shows the same full reference', (tester) async {
      await open(tester);
      expect(find.text('Telebirr Payment'), findsOneWidget);

      expect(find.text(defaultOrderId), findsOneWidget);
      expect(find.text('24D367A5'), findsNothing);
    });

    testWidgets('orders that share the first 8 chars get different references',
        (tester) async {
      const first = '24d367a5-0000-0000-0000-000000000001';
      const second = '24d367a5-0000-0000-0000-000000000002';

      await open(tester, orderId: first);
      expect(find.text(first), findsOneWidget);
      await submit(tester);

      await open(tester, orderId: second);
      expect(find.text(second), findsOneWidget);
      expect(find.text(first), findsNothing);
      await submit(tester);

      expect(submitted, hasLength(2));
      expect(submitted[0].cbeTransactionId, 'MOCK-TBR-$first');
      expect(submitted[1].cbeTransactionId, 'MOCK-TBR-$second');
    });

    testWidgets('a legacy web order id is shown whole and unchanged',
        (tester) async {
      const legacy = 'ord_1790123456789_ab12cd';
      await open(tester, orderId: legacy);

      expect(find.text(legacy), findsOneWidget);
      expect(find.text('ORD_1790'), findsNothing);
    });

    testWidgets('a blank transaction id sends MOCK-CBE-{orderId}',
        (tester) async {
      await open(tester, cbeAccount: realAccount);
      await chooseCbe(tester);
      await submit(tester);

      final payment = submitted.single;
      expect(payment.orderId, defaultOrderId);
      expect(payment.paymentMethod, 'cbe_transfer');
      expect(payment.cbeTransactionId, 'MOCK-CBE-$defaultOrderId');
      // The server refuses a cbeTransactionId above 100 characters.
      expect(payment.cbeTransactionId!.length, lessThanOrEqualTo(100));
    });

    testWidgets('Telebirr with no reference sends MOCK-TBR-{orderId}',
        (tester) async {
      await open(tester);
      await submit(tester);

      final payment = submitted.single;
      expect(payment.paymentMethod, 'telebirr');
      expect(payment.cbeTransactionId, 'MOCK-TBR-$defaultOrderId');
      expect(payment.cbeTransactionId!.length, lessThanOrEqualTo(100));
    });

    testWidgets('an entered transaction id is sent trimmed, as typed',
        (tester) async {
      await open(tester, cbeAccount: realAccount);
      await chooseCbe(tester);
      await tester.enterText(find.byType(TextField), '  FT26274ABC9  ');
      await submit(tester);

      expect(submitted.single.cbeTransactionId, 'FT26274ABC9');
    });

    testWidgets('Copy copies the full reference', (tester) async {
      String? copied;
      tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
        SystemChannels.platform,
        (call) async {
          if (call.method == 'Clipboard.setData') {
            copied =
                (call.arguments as Map<Object?, Object?>)['text'] as String?;
          }
          return null;
        },
      );
      addTearDown(
        () => tester.binding.defaultBinaryMessenger
            .setMockMethodCallHandler(SystemChannels.platform, null),
      );

      await open(tester);
      await tester.ensureVisible(copyButtonFor(defaultOrderId));
      // BUG-L16: the icon-only button has a name.
      expect(
        find.ancestor(
          of: copyButtonFor(defaultOrderId),
          matching: find.byTooltip('Copy'),
        ),
        findsOneWidget,
      );
      await tester.tap(copyButtonFor(defaultOrderId));
      await tester.pump();

      expect(copied, defaultOrderId);
      expect(find.text('Copied to clipboard'), findsOneWidget);
    });
  });

  group('live Telebirr build (ESC-44)', () {
    late _MockEscrowBloc escrowBloc;
    late _MockEscrowRepository escrow;
    late _MockOrderRepository orders;

    setUp(() {
      escrowBloc = _MockEscrowBloc();
      escrow = _MockEscrowRepository();
      orders = _MockOrderRepository();
      when(() => escrowBloc.repository).thenReturn(escrow);
      when(() => escrowBloc.stream).thenAnswer((_) => const Stream.empty());
      when(() => bloc.repository).thenReturn(orders);
      when(() => orders.getOrderById(any())).thenAnswer(
        (_) async => order()..status = OrderStatus.paymentSubmitted,
      );
      when(() => escrow.openOrderCheckout(any())).thenAnswer(
        (_) async => const OrderCheckoutFailed('ESCROW_031', 409, 'mock'),
      );
    });

    Future<void> openLive(WidgetTester tester, {double? orderTotal}) async {
      await tester.pumpWidget(
        MultiBlocProvider(
          providers: [
            BlocProvider<OrderBloc>.value(value: bloc),
            BlocProvider<EscrowBloc>.value(value: escrowBloc),
          ],
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: PaymentInstructionsScreen(
              order: order()..orderTotal = orderTotal ?? 94500,
              telebirrLive: true,
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('Telebirr only: no demo notice, no CBE, no reference fields',
        (tester) async {
      await openLive(tester);

      expect(find.text('Pay with Telebirr'), findsOneWidget);
      expect(find.textContaining('Demo mode'), findsNothing);
      expect(find.text('Commercial Bank of Ethiopia (CBE)'), findsNothing);
      expect(find.text('CBE Transaction ID (optional)'), findsNothing);
      expect(find.text(defaultOrderId), findsNothing);
      expect(find.text('Submit Payment'), findsNothing);
      expect(find.textContaining('94,500 ETB'), findsWidgets);
    });

    testWidgets('sends a Telebirr payment with no reference', (tester) async {
      await openLive(tester);

      await tester.tap(find.text('Pay with Telebirr'));
      await tester.pump();

      expect(submitted, hasLength(1));
      expect(submitted.single.paymentMethod, 'telebirr');
      expect(submitted.single.cbeTransactionId, isNull);
    });

    for (final total in [99.99, 500000.01]) {
      testWidgets('a total of $total ETB cannot be sent', (tester) async {
        await openLive(tester, orderTotal: total);

        expect(
          find.textContaining('Telebirr takes 100 to 500,000 ETB'),
          findsOneWidget,
        );
        final button = tester.widget<FilledButton>(
          find.widgetWithText(FilledButton, 'Pay with Telebirr'),
        );
        expect(button.onPressed, isNull);
      });
    }

    testWidgets('once submitted, the checkout screen takes over',
        (tester) async {
      await openLive(tester);

      states.add(const PaymentSubmitted());
      await tester.pump();
      // The page transition.
      await tester.pump(const Duration(seconds: 1));
      await tester.pump(const Duration(seconds: 1));

      expect(find.byType(OrderCheckoutScreen), findsOneWidget);
      expect(find.byType(PaymentInstructionsScreen), findsNothing);
      verify(() => escrow.openOrderCheckout(defaultOrderId)).called(1);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  });
}

class _MockEscrowBloc extends Mock implements EscrowBloc {}

class _MockEscrowRepository extends Mock implements BaseEscrowRepository {}

class _MockOrderRepository extends Mock implements BaseOrderRepository {}
