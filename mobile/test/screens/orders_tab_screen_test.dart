import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class MockOrderBloc extends Mock implements OrderBloc {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeOrderEvent extends Fake implements OrderEvent {}

const _tokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

/// ESC-09: paid-out orders move on from delivered with their money, so the
/// Orders tab filters Disbursing and Settled too.
void main() {
  late MockOrderBloc orderBloc;
  late MockAuthBloc authBloc;

  setUpAll(() => registerFallbackValue(FakeOrderEvent()));

  setUp(() {
    orderBloc = MockOrderBloc();
    authBloc = MockAuthBloc();
    when(() => orderBloc.state).thenReturn(const OrdersLoaded(orders: []));
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenReturn(null);
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: _tokens,
        role: UserRole.buyer,
        userName: 'Buyer',
      ),
    );
  });

  Widget buildScreen(Locale locale) => MultiBlocProvider(
        providers: [
          BlocProvider<OrderBloc>.value(value: orderBloc),
          BlocProvider<AuthBloc>.value(value: authBloc),
        ],
        child: MaterialApp(
          // The app's delegates: Afan Oromo has no built-in Material strings.
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          locale: locale,
          home: const Scaffold(body: OrdersTabScreen()),
        ),
      );

  // Wide enough for the wrapped (non-scrolling) chip row.
  void useDesktop(WidgetTester tester) {
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  testWidgets('shows Disbursing and Settled chips next to Delivered',
      (tester) async {
    useDesktop(tester);
    await tester.pumpWidget(buildScreen(const Locale('en')));
    await tester.pumpAndSettle();

    final en = lookupAppLocalizations(const Locale('en'));
    for (final label in [
      en.deliveryFilterDelivered,
      en.statusDisbursing,
      en.statusSettled,
    ]) {
      expect(find.widgetWithText(FilterChip, label), findsOneWidget);
    }
  });

  for (final (label, status) in [
    ('Disbursing', 'disbursing'),
    ('Settled', 'settled'),
  ]) {
    testWidgets('the $label chip filters on $status', (tester) async {
      useDesktop(tester);
      await tester.pumpWidget(buildScreen(const Locale('en')));
      await tester.pumpAndSettle();

      await tester.tap(find.widgetWithText(FilterChip, label));
      await tester.pump();

      verify(
        () => orderBloc.add(
          FilterOrders(
            userId: 'buyer-001',
            role: 'buyer',
            statusFilter: status,
          ),
        ),
      ).called(1);
    });
  }

  for (final locale in const [Locale('am'), Locale('om')]) {
    testWidgets('the chips are labelled in ${locale.languageCode}',
        (tester) async {
      useDesktop(tester);
      await tester.pumpWidget(buildScreen(locale));
      await tester.pumpAndSettle();

      final l10n = lookupAppLocalizations(locale);
      for (final label in [l10n.statusDisbursing, l10n.statusSettled]) {
        expect(find.widgetWithText(FilterChip, label), findsOneWidget);
      }
    });
  }
}
