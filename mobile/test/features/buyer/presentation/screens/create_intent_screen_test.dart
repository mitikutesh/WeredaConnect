import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockBuyerIntentBloc extends Mock implements BuyerIntentBloc {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

void main() {
  late MockAuthBloc authBloc;
  late MockBuyerIntentBloc buyerIntentBloc;

  setUp(() {
    authBloc = MockAuthBloc();
    buyerIntentBloc = MockBuyerIntentBloc();
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: _testTokens,
        role: UserRole.buyer,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => buyerIntentBloc.state).thenReturn(const BuyerIntentInitial());
    when(() => buyerIntentBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Future<void> pump(WidgetTester tester) async {
    tester.view.physicalSize = const Size(390, 1200);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<BuyerIntentBloc>.value(value: buyerIntentBloc),
        ],
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: CreateIntentScreen(),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  // Buyer · Create intent board.
  testWidgets('quantity and max price sit side by side', (tester) async {
    await pump(tester);

    final quantity = tester.getTopLeft(
      find.byKey(const Key('create_intent_quantity')),
    );
    final price = tester.getTopLeft(
      find.byKey(const Key('create_intent_max_price')),
    );
    expect(price.dy, quantity.dy);
    expect(price.dx, greaterThan(quantity.dx));
  });

  testWidgets('every crop chip shows without scrolling', (tester) async {
    await pump(tester);

    for (final crop in ['Teff', 'Wheat', 'Sesame']) {
      final chip = find.widgetWithText(FilterChip, crop);
      expect(chip, findsOneWidget);
      expect(tester.getBottomRight(chip).dx, lessThanOrEqualTo(390));
    }
  });

  testWidgets('the bar has no title: the question names the screen',
      (tester) async {
    await pump(tester);

    expect(find.text('What do you need?'), findsOneWidget);
    expect(
      find.descendant(of: find.byType(AppBar), matching: find.byType(Text)),
      findsNothing,
    );
  });

  testWidgets('an empty quantity shows its error under the field',
      (tester) async {
    await pump(tester);

    await tester.tap(find.widgetWithText(FilterChip, 'Teff'));
    await tester.pump();
    final findMatches = find.text('Find Matches');
    await tester.ensureVisible(findMatches);
    await tester.tap(findMatches);
    await tester.pumpAndSettle();

    expect(find.text('Enter the quantity you need'), findsOneWidget);
  });
}
