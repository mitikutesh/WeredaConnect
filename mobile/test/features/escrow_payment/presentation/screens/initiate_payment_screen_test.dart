import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/initiate_payment_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockEscrowBloc extends Mock implements EscrowBloc {}

class FakeEscrowEvent extends Fake implements EscrowEvent {}

class FakeEscrowState extends Fake implements EscrowState {}

void main() {
  late MockEscrowBloc escrowBloc;

  setUpAll(() {
    registerFallbackValue(FakeEscrowEvent());
    registerFallbackValue(FakeEscrowState());
  });

  setUp(() {
    escrowBloc = MockEscrowBloc();
    when(() => escrowBloc.state).thenReturn(const EscrowInitial());
    when(() => escrowBloc.stream)
        .thenAnswer((_) => const Stream<EscrowState>.empty());
  });

  Widget buildScreen({
    double amount = 15000.0,
    String deliveryId = 'del-001-long-id',
    String buyerId = 'buyer-001',
    String farmerId = 'farmer-001',
    String farmerName = 'Abebe Kebede',
  }) {
    return BlocProvider<EscrowBloc>.value(
      value: escrowBloc,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: InitiatePaymentScreen(
          deliveryId: deliveryId,
          buyerId: buyerId,
          farmerId: farmerId,
          farmerName: farmerName,
          amount: amount,
        ),
      ),
    );
  }

  /// Find the confirm button by its check icon (visible in non-loading state).
  Finder findConfirmButton() {
    return find.byIcon(Icons.check);
  }

  group('InitiatePaymentScreen Fayda step-up (AUTH_041)', () {
    Future<List<EscrowEvent>> rejectThenFayda(
      WidgetTester tester, {
      required bool verifies,
    }) async {
      final states = StreamController<EscrowState>.broadcast();
      addTearDown(states.close);
      final sent = <EscrowEvent>[];
      when(() => escrowBloc.stream).thenAnswer((_) => states.stream);
      when(() => escrowBloc.add(any())).thenAnswer(
        (inv) => sent.add(inv.positionalArguments.first as EscrowEvent),
      );
      await tester.pumpWidget(
        BlocProvider<EscrowBloc>.value(
          value: escrowBloc,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: InitiatePaymentScreen(
              deliveryId: 'del-001-long-id',
              buyerId: 'buyer-001',
              farmerId: 'farmer-001',
              farmerName: 'Abebe Kebede',
              amount: 15000,
              faydaVerificationScreen: (context) => Scaffold(
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

      states.add(
        const EscrowError(code: 'AUTH_041', message: 'Fayda needed'),
      );
      await tester.pumpAndSettle();
      // The Fayda prompt, not the password one that could never satisfy it.
      expect(find.byKey(const Key('step_up_password_field')), findsNothing);
      await tester.tap(find.byKey(const Key('fayda_step_up_verify_button')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('fayda screen'));
      await tester.pumpAndSettle();
      return sent;
    }

    testWidgets('a Fayda login sends the same payment again', (tester) async {
      final sent = await rejectThenFayda(tester, verifies: true);

      final retry = sent.whereType<InitiatePayment>().single;
      expect(retry.deliveryId, 'del-001-long-id');
      expect(retry.amount, 15000);
    });

    testWidgets('a failed Fayda login sends nothing', (tester) async {
      final sent = await rejectThenFayda(tester, verifies: false);

      expect(sent.whereType<InitiatePayment>(), isEmpty);
    });
  });

  group('InitiatePaymentScreen', () {
    testWidgets('renders commission breakdown correctly',
        (WidgetTester tester) async {
      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Total amount, as the boards write money: in the amount card and
      // in the breakdown.
      expect(find.text('15,000 ETB'), findsNWidgets(2));
      // Commission: max(15000 * 0.025, 5) = 375
      expect(find.text('375 ETB'), findsOneWidget);
      // Farmer payout: 15000 - 375 = 14,625
      expect(find.text('14,625 ETB'), findsOneWidget);
      // 2.5% label
      expect(find.textContaining('2.5'), findsOneWidget);
    });

    testWidgets('renders minimum commission of 5 ETB for small amounts',
        (WidgetTester tester) async {
      // 100 * 0.025 = 2.5, but min is 5
      await tester.pumpWidget(buildScreen(amount: 100.0));
      await tester.pumpAndSettle();

      // Commission should be 5 (minimum)
      expect(find.text('5 ETB'), findsOneWidget);
      // Farmer receives: 100 - 5 = 95
      expect(find.text('95 ETB'), findsOneWidget);
    });

    testWidgets('shows Fayda warning when amount > 10,000',
        (WidgetTester tester) async {
      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.warning_amber), findsOneWidget);
    });

    testWidgets('hides Fayda warning when amount <= 10,000',
        (WidgetTester tester) async {
      await tester.pumpWidget(buildScreen(amount: 5000.0));
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.warning_amber), findsNothing);
    });

    testWidgets('shows truncated delivery ID', (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('del-001-'), findsOneWidget);
    });

    testWidgets('shows farmer name', (WidgetTester tester) async {
      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.textContaining('Abebe Kebede'), findsOneWidget);
    });

    testWidgets('shows escrow protection info with shield icon',
        (WidgetTester tester) async {
      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.shield), findsOneWidget);
    });

    testWidgets('shows confirm button with check icon',
        (WidgetTester tester) async {
      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      expect(findConfirmButton(), findsOneWidget);
    });

    testWidgets('tapping confirm shows confirmation dialog',
        (WidgetTester tester) async {
      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Tap the confirm button (find by icon)
      await tester.tap(findConfirmButton());
      await tester.pumpAndSettle();

      // Dialog should be visible
      expect(find.byType(AlertDialog), findsOneWidget);
      // Should show Pay button in dialog
      expect(find.text('Pay'), findsOneWidget);
    });

    testWidgets('cancel in dialog dismisses it', (WidgetTester tester) async {
      await tester.pumpWidget(buildScreen());
      await tester.pumpAndSettle();

      // Open dialog
      await tester.tap(findConfirmButton());
      await tester.pumpAndSettle();

      // Tap Cancel in dialog (TextButton)
      await tester.tap(find.byType(TextButton));
      await tester.pumpAndSettle();

      // Dialog should be gone
      expect(find.byType(AlertDialog), findsNothing);
    });

    testWidgets('Pay in dialog dispatches InitiatePayment event',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          deliveryId: 'del-001',
        ),
      );
      await tester.pumpAndSettle();

      // Open dialog
      await tester.tap(findConfirmButton());
      await tester.pumpAndSettle();

      // Tap Pay
      await tester.tap(find.text('Pay'));
      await tester.pumpAndSettle();

      verify(
        () => escrowBloc.add(
          const InitiatePayment(
            deliveryId: 'del-001',
            buyerId: 'buyer-001',
            farmerId: 'farmer-001',
            amount: 15000.0,
          ),
        ),
      ).called(1);
    });

    testWidgets('confirm button shows loading indicator during loading state',
        (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(const EscrowLoading());
      when(() => escrowBloc.stream)
          .thenAnswer((_) => Stream.value(const EscrowLoading()));

      await tester.pumpWidget(buildScreen());
      // Use pump() instead of pumpAndSettle() because
      // CircularProgressIndicator animates indefinitely.
      await tester.pump();

      // Should show a progress indicator instead of check icon
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(find.byIcon(Icons.check), findsNothing);
    });
  });
}
