import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/screens/payments_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/account_menu.dart';

class _MockEscrowBloc extends Mock implements EscrowBloc {}

class _MockAuthBloc extends Mock implements AuthBloc {}

class _FakeEscrowEvent extends Fake implements EscrowEvent {}

/// Payments left the bottom bar (design canvas page M): the page is pushed
/// from Home and the account sheet, so it must stand on its own route.
void main() {
  late _MockEscrowBloc escrow;
  late _MockAuthBloc auth;
  final l10n = lookupAppLocalizations(const Locale('en'));

  setUpAll(() => registerFallbackValue(_FakeEscrowEvent()));

  setUp(() {
    escrow = _MockEscrowBloc();
    auth = _MockAuthBloc();
    when(() => escrow.state).thenReturn(const EscrowLoading());
    when(() => escrow.stream).thenAnswer((_) => const Stream.empty());
    when(() => auth.stream).thenAnswer((_) => const Stream.empty());
  });

  void signIn(UserRole role) => when(() => auth.state).thenReturn(
        AuthAuthenticated(
          userId: 'user-001',
          tokens: const AuthTokens(
            accessToken: 'a',
            refreshToken: 'r',
            idToken: 'i',
          ),
          role: role,
          userName: 'Dawit Tesfaye',
        ),
      );

  Future<void> pumpPushed(WidgetTester tester, UserRole role) async {
    signIn(role);
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<EscrowBloc>.value(value: escrow),
          BlocProvider<AuthBloc>.value(value: auth),
        ],
        child: MaterialApp(
          theme: AppTheme.light(role: role),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Builder(
            builder: (context) => Scaffold(
              body: TextButton(
                onPressed: () => Navigator.of(context).push(
                  MaterialPageRoute<void>(
                    builder: (_) => const PaymentsTabScreen(),
                  ),
                ),
                child: const Text('open payments'),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open payments'));
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
  }

  for (final role in UserRole.values) {
    testWidgets('pushed for a ${role.name}: its own page with Back',
        (tester) async {
      await pumpPushed(tester, role);

      expect(tester.takeException(), isNull);
      expect(find.text(l10n.myPayments), findsOneWidget);
      // No bell and account buttons: the sheet it came from has them.
      expect(find.byType(HeaderActions), findsNothing);
      final back = find.byKey(const Key('payments_back'));
      expect(back, findsOneWidget);
      expect(tester.getSize(back).height, greaterThanOrEqualTo(48));

      await tester.tap(back);
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      expect(find.text(l10n.myPayments), findsNothing);
      expect(find.text('open payments'), findsOneWidget);
    });
  }
}
