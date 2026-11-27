import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/crop_glyph.dart';

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
      final chip = find.byKey(Key('create_intent_crop_${crop.toLowerCase()}'));
      expect(chip, findsOneWidget);
      expect(tester.getBottomRight(chip).dx, lessThanOrEqualTo(390));
    }
  });

  testWidgets('each crop chip draws its own glyph', (tester) async {
    await pump(tester);

    for (final crop in ['wheat', 'lentils', 'coffee']) {
      final glyph = find.descendant(
        of: find.byKey(Key('create_intent_crop_$crop')),
        matching: find.byType(CropGlyph),
      );
      expect(glyph, findsOneWidget, reason: crop);
      expect(tester.widget<CropGlyph>(glyph).cropKey, crop);
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

    await tester.tap(find.byKey(const Key('create_intent_crop_teff')));
    await tester.pump();
    final findMatches = find.text('Find Matches');
    await tester.ensureVisible(findMatches);
    await tester.tap(findMatches);
    await tester.pumpAndSettle();

    expect(find.text('Enter the quantity you need'), findsOneWidget);
  });

  // CreateIntentM board: the buyer types kilograms and reads them back kg
  // first; the intent still stores quintals (buyer-intent contract).
  group('quantity in kilograms', () {
    setUp(() {
      registerFallbackValue(const LoadActiveIntents(buyerId: 'x'));
      when(() => buyerIntentBloc.add(any())).thenReturn(null);
      when(() => authBloc.state).thenReturn(
        const AuthAuthenticated(
          userId: 'buyer-001',
          tokens: _testTokens,
          role: UserRole.buyer,
          woreda: 'Addis Ababa',
        ),
      );
    });

    testWidgets('the field asks for kg and reads the quantity back kg first',
        (tester) async {
      await pump(tester);
      expect(find.text('Quantity needed (kg)'), findsOneWidget);
      expect(find.textContaining('quintals'), findsNothing);

      await tester.enterText(
        find.byKey(const Key('create_intent_quantity')),
        '2000',
      );
      await tester.pump();

      expect(find.text('2,000 kg (20 quintals)'), findsOneWidget);
    });

    testWidgets('2,000 kg typed is saved as 20 quintals', (tester) async {
      await pump(tester);
      await tester.tap(find.byKey(const Key('create_intent_crop_wheat')));
      await tester.enterText(
        find.byKey(const Key('create_intent_quantity')),
        '2000',
      );
      await tester.pump();
      await tester.ensureVisible(find.text('Find Matches'));
      await tester.tap(find.text('Find Matches'));
      await tester.pump();

      final sent = verify(
        () => buyerIntentBloc.add(captureAny(that: isA<CreateBuyerIntent>())),
      ).captured.single as CreateBuyerIntent;
      expect(sent.quantityNeeded, 20);
      expect(sent.cropType, 'wheat');
      expect(sent.maxPricePerKg, 0);
      expect(sent.deliveryWoreda, 'Addis Ababa');
    });

    testWidgets('part of a quintal keeps its fraction', (tester) async {
      await pump(tester);
      await tester.tap(find.byKey(const Key('create_intent_crop_teff')));
      await tester.enterText(
        find.byKey(const Key('create_intent_quantity')),
        '250',
      );
      await tester.pump();
      expect(find.text('250 kg (2.5 quintals)'), findsOneWidget);

      await tester.ensureVisible(find.text('Find Matches'));
      await tester.tap(find.text('Find Matches'));
      await tester.pump();
      final sent = verify(
        () => buyerIntentBloc.add(captureAny(that: isA<CreateBuyerIntent>())),
      ).captured.single as CreateBuyerIntent;
      expect(sent.quantityNeeded, 2.5);
    });

    testWidgets('a comma is grouping: "4,000" is 4,000 kg, "12,5" is 125 kg',
        (tester) async {
      await pump(tester);
      await tester.tap(find.byKey(const Key('create_intent_crop_teff')));
      final field = find.byKey(const Key('create_intent_quantity'));

      await tester.enterText(field, '12,5');
      await tester.pump();
      expect(tester.widget<TextField>(field).controller!.text, '125');
      expect(find.text('125 kg (1.25 quintals)'), findsOneWidget);

      await tester.enterText(field, '4,000');
      await tester.pump();
      expect(tester.widget<TextField>(field).controller!.text, '4000');
      expect(find.text('4,000 kg (40 quintals)'), findsOneWidget);

      await tester.ensureVisible(find.text('Find Matches'));
      await tester.tap(find.text('Find Matches'));
      await tester.pump();
      final sent = verify(
        () => buyerIntentBloc.add(captureAny(that: isA<CreateBuyerIntent>())),
      ).captured.single as CreateBuyerIntent;
      expect(sent.quantityNeeded, 40);
    });

    testWidgets('more than 1,000,000 kg is refused and said under the field',
        (tester) async {
      await pump(tester);
      await tester.tap(find.byKey(const Key('create_intent_crop_teff')));
      final field = find.byKey(const Key('create_intent_quantity'));

      await tester.enterText(field, '1000000');
      await tester.pump();
      expect(find.text('Enter at most 1,000,000 kg.'), findsNothing);
      expect(find.text('1,000,000 kg (10,000 quintals)'), findsOneWidget);

      await tester.enterText(field, '2000000');
      await tester.pump();
      expect(find.text('Enter at most 1,000,000 kg.'), findsOneWidget);

      await tester.ensureVisible(find.text('Find Matches'));
      await tester.tap(find.text('Find Matches'));
      await tester.pump();
      verifyNever(
        () => buyerIntentBloc.add(any(that: isA<CreateBuyerIntent>())),
      );
    });

    testWidgets('the note about alerts sits in the form, above the button',
        (tester) async {
      await pump(tester);
      final note = find.text(
        'Saving this request lets us alert you when new matches arrive',
      );
      expect(note, findsOneWidget);
      expect(
        tester.getTopLeft(note).dy,
        lessThan(tester.getTopLeft(find.text('Find Matches')).dy),
      );
    });
  });
}
