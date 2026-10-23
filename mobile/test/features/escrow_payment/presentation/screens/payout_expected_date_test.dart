import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/escrow_status_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockEscrowBloc extends Mock implements EscrowBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeEscrowEvent extends Fake implements EscrowEvent {}

/// 22:30 UTC on 26 Oct is 01:30 on 27 Oct in Ethiopia (UTC+3): the screens
/// show the day in Ethiopia (SIM-50), whatever the device timezone.
final _releaseNotBefore = DateTime.utc(2026, 10, 26, 22, 30);
const _expectedText = 'Payout expected on Oct 27, 2026';

Payment _payment(
  PaymentStatus status, {
  String id = 'escrow-order-ord-1',
  DateTime? releaseNotBefore,
}) {
  final now = DateTime.now().toUtc();
  return Payment()
    ..paymentId = id
    ..deliveryId = 'del-001-long-delivery-id'
    ..orderId = 'ord-1'
    ..farmerId = 'farmer-001'
    ..buyerId = 'buyer-001'
    ..truckerId = 'trucker-001'
    ..amount = 1500
    ..commission = 37.5
    ..commissionRate = 0.025
    ..farmerPayout = 1262.5
    ..truckerPayout = 200
    ..status = status
    ..createdAt = now
    ..updatedAt = now
    ..expiresAt = now.add(const Duration(days: 7))
    ..disputeWindowEndsAt = now.add(const Duration(hours: 24))
    ..releaseNotBefore = releaseNotBefore
    ..version = 1;
}

/// FRD-13: the farmer and the trucker see "Payout expected on (date)" on the
/// escrow detail and on their payment list while the money is held; the
/// buyer's list never shows it, and no screen says why the date is later.
void main() {
  late MockEscrowBloc escrowBloc;
  late MockAuthBloc authBloc;

  setUpAll(() => registerFallbackValue(FakeEscrowEvent()));

  setUp(() {
    escrowBloc = MockEscrowBloc();
    authBloc = MockAuthBloc();
    when(() => escrowBloc.add(any())).thenReturn(null);
    when(() => escrowBloc.stream)
        .thenAnswer((_) => const Stream<EscrowState>.empty());
    when(() => authBloc.stream)
        .thenAnswer((_) => const Stream<AuthState>.empty());
  });

  void signIn(UserRole role) => when(() => authBloc.state).thenReturn(
        AuthAuthenticated(
          userId: '${role.name}-001',
          tokens: const AuthTokens(
            accessToken: 'a',
            refreshToken: 'r',
            idToken: 'i',
          ),
          role: role,
        ),
      );

  Widget host(Widget home) => MultiBlocProvider(
        providers: [
          BlocProvider<EscrowBloc>.value(value: escrowBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: home,
        ),
      );

  void tallView(WidgetTester tester) {
    tester.view.physicalSize = const Size(1000, 4000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  group('EscrowStatusScreen', () {
    for (final role in const [UserRole.farmer, UserRole.trucker]) {
      testWidgets('${role.name} sees the expected payout day', (tester) async {
        tallView(tester);
        signIn(role);
        when(() => escrowBloc.state).thenReturn(
          EscrowDetailLoaded(
            payment: _payment(
              PaymentStatus.escrowed,
              releaseNotBefore: _releaseNotBefore,
            ),
          ),
        );

        await tester.pumpWidget(
          host(const EscrowStatusScreen(escrowId: 'escrow-order-ord-1')),
        );
        await tester.pumpAndSettle();

        expect(find.text(_expectedText), findsOneWidget);
      });
    }

    testWidgets('paid out: no expected day', (tester) async {
      tallView(tester);
      signIn(UserRole.farmer);
      when(() => escrowBloc.state).thenReturn(
        EscrowDetailLoaded(
          payment: _payment(
            PaymentStatus.completed,
            releaseNotBefore: _releaseNotBefore,
          ),
        ),
      );

      await tester.pumpWidget(
        host(const EscrowStatusScreen(escrowId: 'escrow-order-ord-1')),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('Payout expected'), findsNothing);
    });

    testWidgets('no date from the server (the buyer): nothing shown',
        (tester) async {
      tallView(tester);
      signIn(UserRole.buyer);
      when(() => escrowBloc.state).thenReturn(
        EscrowDetailLoaded(payment: _payment(PaymentStatus.escrowed)),
      );

      await tester.pumpWidget(
        host(const EscrowStatusScreen(escrowId: 'escrow-order-ord-1')),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('Payout expected'), findsNothing);
    });
  });

  group('PaymentHistoryScreen', () {
    void listOf(List<Payment> payments) =>
        when(() => escrowBloc.state).thenReturn(
          PaymentHistoryLoaded(payments: payments),
        );

    for (final role in const [UserRole.farmer, UserRole.trucker]) {
      testWidgets('${role.name} sees the expected payout day on a held row',
          (tester) async {
        signIn(role);
        listOf([
          _payment(PaymentStatus.escrowed, releaseNotBefore: _releaseNotBefore),
          _payment(
            PaymentStatus.completed,
            id: 'escrow-order-ord-2',
            releaseNotBefore: _releaseNotBefore,
          ),
        ]);

        await tester.pumpWidget(host(const PaymentHistoryScreen()));
        await tester.pumpAndSettle();

        // Only the escrowed row: the completed one is already paid.
        expect(find.text(_expectedText), findsOneWidget);
      });
    }

    testWidgets('the buyer never sees it, even on a cached dated row',
        (tester) async {
      signIn(UserRole.buyer);
      listOf([
        _payment(PaymentStatus.escrowed, releaseNotBefore: _releaseNotBefore),
      ]);

      await tester.pumpWidget(host(const PaymentHistoryScreen()));
      await tester.pumpAndSettle();

      expect(find.textContaining('Payout expected'), findsNothing);
    });
  });

  group('escrowPayoutExpectedOn in the three languages', () {
    for (final locale in const ['en', 'am', 'om']) {
      test('$locale carries the date', () {
        final text = lookupAppLocalizations(Locale(locale))
            .escrowPayoutExpectedOn('Oct 27, 2026');

        expect(text, contains('Oct 27, 2026'));
        expect(text, isNot(contains('{date}')));
      });
    }
  });
}
