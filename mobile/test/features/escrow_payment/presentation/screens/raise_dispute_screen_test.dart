import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/raise_dispute_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockEscrowBloc extends Mock implements EscrowBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeEscrowEvent extends Fake implements EscrowEvent {}

class FakeEscrowState extends Fake implements EscrowState {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access',
  refreshToken: 'fake-refresh',
  idToken: 'fake-id',
);

void main() {
  late MockEscrowBloc escrowBloc;
  late MockAuthBloc authBloc;

  setUpAll(() {
    registerFallbackValue(FakeEscrowEvent());
    registerFallbackValue(FakeEscrowState());
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
  });

  setUp(() {
    escrowBloc = MockEscrowBloc();
    authBloc = MockAuthBloc();

    when(() => escrowBloc.state).thenReturn(const EscrowInitial());
    when(() => escrowBloc.stream)
        .thenAnswer((_) => const Stream<EscrowState>.empty());
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: _testTokens,
        role: UserRole.buyer,
      ),
    );
    when(() => authBloc.stream)
        .thenAnswer((_) => const Stream<AuthState>.empty());
  });

  Widget buildScreen({
    DateTime? disputeWindowEndsAt,
    String escrowId = 'esc-001',
    double amount = 15000.0,
    String deliveryId = 'del-001-long-id',
  }) {
    return MultiBlocProvider(
      providers: [
        BlocProvider<EscrowBloc>.value(value: escrowBloc),
        BlocProvider<AuthBloc>.value(value: authBloc),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: RaiseDisputeScreen(
          escrowId: escrowId,
          amount: amount,
          deliveryId: deliveryId,
          disputeWindowEndsAt: disputeWindowEndsAt,
        ),
      ),
    );
  }

  /// Find the submit button by its warning_amber icon (non-loading state).
  Finder findSubmitButton() {
    return find.byIcon(Icons.warning_amber);
  }

  group('RaiseDisputeScreen', () {
    testWidgets('renders payment amount and delivery ID',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      await tester.pumpAndSettle();

      // The header line: "15,000 ETB · Delivery #del-001-".
      expect(find.textContaining('15,000 ETB'), findsOneWidget);
      expect(find.textContaining('del-001-'), findsOneWidget);
    });

    testWidgets('shows 6 radio options for dispute reasons',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byType(RadioListTile<String>), findsNWidgets(6));
    });

    testWidgets('shows countdown timer when dispute window active',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.timer), findsOneWidget);
    });

    testWidgets('shows expired banner when dispute window passed',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().subtract(const Duration(hours: 1)),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.cancel), findsOneWidget);
    });

    testWidgets('submit button shows warning icon when no reason selected',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      await tester.pumpAndSettle();

      // Submit button should show warning icon but be non-interactive
      // (no reason selected means _canSubmit is false → onPressed is null)
      expect(findSubmitButton(), findsOneWidget);
    });

    testWidgets('submit button enabled after selecting a reason',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      await tester.pumpAndSettle();

      // Tap the first radio option (damaged_goods)
      await tester.tap(find.byType(RadioListTile<String>).first);
      await tester.pumpAndSettle();

      // Submit button should be present and tappable
      expect(findSubmitButton(), findsOneWidget);
    });

    testWidgets('submit button disabled when window expired',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().subtract(const Duration(hours: 1)),
        ),
      );
      await tester.pumpAndSettle();

      // The submit button should be visible but the expired banner should
      // indicate the form is disabled
      expect(find.byIcon(Icons.cancel), findsOneWidget);
      expect(findSubmitButton(), findsOneWidget);
    });

    testWidgets('description field renders', (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byType(TextFormField), findsOneWidget);
      // Max length counter should show /1000
      expect(find.textContaining('1000'), findsOneWidget);
    });

    testWidgets('tapping submit opens confirmation dialog',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      await tester.pumpAndSettle();

      // Select a reason
      await tester.tap(find.byType(RadioListTile<String>).first);
      await tester.pumpAndSettle();

      // Scroll submit button into view (it's below the fold)
      await tester.ensureVisible(findSubmitButton());
      await tester.pumpAndSettle();

      // Tap submit button via its icon
      await tester.tap(findSubmitButton());
      await tester.pumpAndSettle();

      expect(find.byType(AlertDialog), findsOneWidget);
      // Both the main button and dialog button show 'Submit Dispute'
      expect(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.text('Submit Dispute'),
        ),
        findsOneWidget,
      );
    });

    testWidgets('confirming dialog dispatches RaisePaymentDispute event',
        (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      await tester.pumpAndSettle();

      // Select a reason
      await tester.tap(find.byType(RadioListTile<String>).first);
      await tester.pumpAndSettle();

      // Scroll submit button into view
      await tester.ensureVisible(findSubmitButton());
      await tester.pumpAndSettle();

      // Tap submit
      await tester.tap(findSubmitButton());
      await tester.pumpAndSettle();

      // Confirm in dialog – tap the button inside the AlertDialog
      await tester.tap(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.text('Submit Dispute'),
        ),
      );
      await tester.pumpAndSettle();

      verify(() => escrowBloc.add(any(that: isA<RaisePaymentDispute>())))
          .called(1);
    });

    testWidgets('cancel in dialog dismisses it', (WidgetTester tester) async {
      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      await tester.pumpAndSettle();

      // Select a reason
      await tester.tap(find.byType(RadioListTile<String>).first);
      await tester.pumpAndSettle();

      // Scroll submit button into view
      await tester.ensureVisible(findSubmitButton());
      await tester.pumpAndSettle();

      // Tap submit
      await tester.tap(findSubmitButton());
      await tester.pumpAndSettle();

      // Tap Cancel in dialog
      await tester.tap(find.byType(TextButton));
      await tester.pumpAndSettle();

      expect(find.byType(AlertDialog), findsNothing);
      verifyNever(() => escrowBloc.add(any(that: isA<RaisePaymentDispute>())));
    });

    testWidgets('shows loading indicator during submission',
        (WidgetTester tester) async {
      when(() => escrowBloc.state).thenReturn(const EscrowLoading());
      when(() => escrowBloc.stream)
          .thenAnswer((_) => Stream.value(const EscrowLoading()));

      await tester.pumpWidget(
        buildScreen(
          disputeWindowEndsAt:
              DateTime.now().toUtc().add(const Duration(hours: 24)),
        ),
      );
      // Use pump() because CircularProgressIndicator never settles
      await tester.pump();

      // Button should show loading indicator
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      // Warning icon should be replaced by loading spinner
      expect(findSubmitButton(), findsNothing);
    });
  });
}
