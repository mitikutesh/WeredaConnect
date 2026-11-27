import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

/// The login screen's "Your role" switch (WelcomeM board). It dresses the
/// screen and words the button; it never decides who signs in.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late MockAuthBloc bloc;
  late StreamController<AuthState> states;
  late SettingsProvider settings;

  setUpAll(() async {
    registerFallbackValue(FakeAuthEvent());
    // The app's own font: whether the header sketch has room beside the
    // title depends on how wide the words really are.
    final loader = FontLoader(AppTheme.fontFamily);
    for (final weight in ['Medium', 'Bold', 'ExtraBold']) {
      loader.addFont(
        Future.value(
          ByteData.view(
            File('assets/fonts/BricolageGrotesque-$weight.ttf')
                .readAsBytesSync()
                .buffer,
          ),
        ),
      );
    }
    await loader.load();
  });

  Future<void> start({String? rememberedRole}) async {
    SharedPreferences.setMockInitialValues({
      if (rememberedRole != null) 'last_sign_in_role': rememberedRole,
    });
    settings = SettingsProvider();
    await settings.initialize();
  }

  setUp(() {
    bloc = MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    when(() => bloc.state).thenReturn(const AuthUnauthenticated());
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    when(() => bloc.isClosed).thenReturn(false);
  });

  tearDown(() => unawaited(states.close()));

  Future<void> pumpLogin(
    WidgetTester tester, {
    Locale locale = const Locale('en'),
    Size size = const Size(390, 844),
    double textScale = 1,
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    tester.platformDispatcher.textScaleFactorTestValue = textScale;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          ChangeNotifierProvider<LanguageProvider>(
            create: (_) => LanguageProvider(),
          ),
          ChangeNotifierProvider<SettingsProvider>.value(value: settings),
        ],
        child: BlocProvider<AuthBloc>.value(
          value: bloc,
          child: MaterialApp(
            // Signed out: the app's own theme is the brand's black.
            theme: AppTheme.light(),
            locale: locale,
            localizationsDelegates: const [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: const LoginScreen(),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder pill(UserRole role) => find.byKey(ValueKey('login_role_${role.name}'));

  /// The role colours the login subtree wears.
  RoleColors wornColors(WidgetTester tester) =>
      RoleColors.of(tester.element(find.byKey(const Key('login_submit'))));

  Future<void> fillAndSubmit(WidgetTester tester) async {
    await tester.enterText(find.byType(TextFormField).first, '0911234567');
    await tester.enterText(find.byType(TextFormField).at(1), 'password1');
    await tester.ensureVisible(find.byKey(const Key('login_submit')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('login_submit')));
    await tester.pump();
  }

  const tokens = AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i');

  group('look', () {
    testWidgets('the first run opens in the Farmer look', (tester) async {
      await start();
      await pumpLogin(tester);

      expect(find.text('Login as Farmer'), findsOneWidget);
      expect(
        find.text('Sell your harvest and follow every order'),
        findsOneWidget,
      );
      expect(wornColors(tester).primary, RoleColors.farmer.primary);
      expect(find.byKey(const ValueKey('login_sketch_farmer')), findsOneWidget);
      // One sketch per screen.
      expect(find.byType(RoleSketch), findsOneWidget);
    });

    for (final (role, button, tagline) in const [
      (
        UserRole.farmer,
        'Login as Farmer',
        'Sell your harvest and follow every order',
      ),
      (
        UserRole.trucker,
        'Login as Trucker',
        'Fill your truck and get paid on delivery',
      ),
      (
        UserRole.buyer,
        'Login as Buyer',
        'Find crops and transport in one place',
      ),
    ]) {
      testWidgets('picking ${role.name} dresses the whole screen',
          (tester) async {
        // Start from another role so the tap is what changes the look.
        await start(
          rememberedRole: role == UserRole.buyer ? 'farmer' : 'buyer',
        );
        await pumpLogin(tester);

        await tester.tap(pill(role));
        await tester.pumpAndSettle();

        expect(find.text(button), findsOneWidget);
        expect(find.text(tagline), findsOneWidget);
        final colors = RoleColors.forRole(role);
        expect(wornColors(tester).primary, colors.primary);
        expect(wornColors(tester).soft, colors.soft);
        // The Forgot Password link is in the role's colour.
        expect(
          tester.widget<Text>(find.text('Forgot Password?')).style!.color,
          colors.primary,
        );
        expect(
          find.byKey(ValueKey('login_sketch_${role.name}')),
          findsOneWidget,
        );
        expect(find.byType(RoleSketch), findsOneWidget);
      });
    }

    testWidgets('the switch is a labelled group of three exclusive buttons',
        (tester) async {
      final semantics = tester.ensureSemantics();
      await start();
      await pumpLogin(tester);

      expect(find.bySemanticsLabel('Your role'), findsOneWidget);
      for (final (role, label) in const [
        (UserRole.farmer, 'Farmer'),
        (UserRole.trucker, 'Trucker'),
        (UserRole.buyer, 'Buyer'),
      ]) {
        final data = tester.getSemantics(pill(role)).getSemanticsData();
        expect(data.label, label);
        expect(data.flagsCollection.isButton, isTrue);
        expect(
          data.flagsCollection.isInMutuallyExclusiveGroup,
          isTrue,
        );
        expect(
          data.flagsCollection.isSelected.toBoolOrNull(),
          role == UserRole.farmer,
        );
        expect(data.hasAction(SemanticsAction.tap), isTrue);
        // A 48 tall hit area.
        expect(tester.getSize(pill(role)).height, greaterThanOrEqualTo(48));
      }
      semantics.dispose();
    });
  });

  group('the switch decides nothing', () {
    testWidgets('sign-in sends the phone and password only, whatever is picked',
        (tester) async {
      await start();
      await pumpLogin(tester);
      await tester.tap(pill(UserRole.trucker));
      await tester.pumpAndSettle();

      await fillAndSubmit(tester);

      final sent = verify(() => bloc.add(captureAny())).captured.single;
      // The event has no role: nothing of the switch leaves the screen.
      expect(
        sent,
        const AuthLoginRequested(
          phoneNumber: '+251911234567',
          password: 'password1',
        ),
      );
    });

    testWidgets('Farmer picked, a buyer account signs in: no error',
        (tester) async {
      await start();
      await pumpLogin(tester);
      expect(find.text('Login as Farmer'), findsOneWidget);
      await fillAndSubmit(tester);

      states.add(
        const AuthAuthenticated(
          userId: 'buyer-1',
          tokens: tokens,
          role: UserRole.buyer,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('login_error_banner')), findsNothing);
      // The account's real role is what the device remembers.
      expect(settings.lastSignInRole, UserRole.buyer);
    });
  });

  group('remembered role', () {
    testWidgets('the last role that signed in is preselected', (tester) async {
      await start(rememberedRole: 'trucker');
      await pumpLogin(tester);

      expect(find.text('Login as Trucker'), findsOneWidget);
      expect(wornColors(tester).primary, RoleColors.trucker.primary);
    });

    testWidgets('picking a role does not change what is remembered',
        (tester) async {
      await start(rememberedRole: 'trucker');
      await pumpLogin(tester);

      await tester.tap(pill(UserRole.buyer));
      await tester.pumpAndSettle();

      // Only a sign-in is remembered, never a tap on the switch.
      expect(settings.lastSignInRole, UserRole.trucker);
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('last_sign_in_role'), 'trucker');
    });

    testWidgets('a sign-in is saved for the next visit', (tester) async {
      await start();
      await pumpLogin(tester);

      states.add(
        const AuthAuthenticated(
          userId: 'trucker-1',
          tokens: tokens,
          role: UserRole.trucker,
        ),
      );
      await tester.pumpAndSettle();

      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('last_sign_in_role'), 'trucker');
    });

    testWidgets('an unknown stored value falls back to Farmer', (tester) async {
      await start(rememberedRole: 'admin');
      await pumpLogin(tester);

      expect(find.text('Login as Farmer'), findsOneWidget);
    });
  });

  group('long labels', () {
    for (final (lang, size, scale) in const [
      ('om', Size(320, 640), 1.0),
      ('am', Size(320, 640), 1.0),
      ('en', Size(320, 640), 1.0),
      ('om', Size(390, 844), 2.0),
      ('am', Size(390, 844), 2.0),
      ('en', Size(390, 844), 2.0),
    ]) {
      testWidgets(
          'nothing overflows ($lang, ${size.width.toInt()} px, ${scale}x)',
          (tester) async {
        await start(rememberedRole: 'trucker');
        await pumpLogin(
          tester,
          locale: Locale(lang),
          size: size,
          textScale: scale,
        );

        // An overflow is a Flutter error: none was reported.
        expect(tester.takeException(), isNull);
        // Every pill is inside the screen, with its whole label.
        for (final role in UserRole.values) {
          final rect = tester.getRect(pill(role));
          expect(rect.left, greaterThanOrEqualTo(0));
          expect(rect.right, lessThanOrEqualTo(size.width));
          final label = tester.renderObject<RenderParagraph>(
            find.descendant(of: pill(role), matching: find.byType(Text)),
          );
          expect(label.didExceedMaxLines, isFalse);
        }
      });
    }
  });
}
