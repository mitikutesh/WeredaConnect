import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class FakeOrderEvent extends Fake implements OrderEvent {}

/// Payment above 10,000 ETB blocked by the KYC gate or the Fayda step-up
/// (web path: the error comes straight back from /sync). Verifying as the
/// account's owner must send the same payment again; anything else must
/// leave it unsent.
void main() {
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

  Order order() => Order()
    ..orderId = '24d367a5-0000-0000-0000-000000000000'
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

  Future<void> open(WidgetTester tester, {required bool verifies}) async {
    await tester.pumpWidget(
      BlocProvider<OrderBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: PaymentInstructionsScreen(
            order: order(),
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

  testWidgets('above 10,000 ETB: a Fayda login resubmits the same payment',
      (tester) async {
    await open(tester, verifies: true);
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
    await open(tester, verifies: true);
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
    await open(tester, verifies: true);
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
    await open(tester, verifies: true);
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
}
