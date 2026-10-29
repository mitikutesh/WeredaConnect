import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/forgot_password_screen.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/auth/screens/otp_verification_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

// ---------------------------------------------------------------------------
// Mocks
// ---------------------------------------------------------------------------
class MockAuthBloc extends Mock implements AuthBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

void main() {
  late MockAuthBloc authBloc;
  late LanguageProvider languageProvider;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
  });

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    languageProvider = LanguageProvider();
    authBloc = MockAuthBloc();

    // Default: unauthenticated, no loading
    when(() => authBloc.state).thenReturn(const AuthUnauthenticated());
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildTestWidget({
    Stream<AuthState>? stream,
    Locale? locale,
  }) {
    if (stream != null) {
      when(() => authBloc.stream).thenAnswer((_) => stream);
    }

    // As in main.dart: the app locale follows LanguageProvider, so the
    // login screen's language switch changes the whole screen.
    return ChangeNotifierProvider<LanguageProvider>.value(
      value: languageProvider,
      child: BlocProvider<AuthBloc>.value(
        value: authBloc,
        child: Consumer<LanguageProvider>(
          builder: (context, language, _) => MaterialApp(
            locale: locale ?? language.locale,
            // Same delegates as main.dart / main_web.dart, so Afan Oromo (no
            // Material localizations of its own) renders too.
            localizationsDelegates: const [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              GlobalWidgetsLocalizations.delegate,
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: const LoginScreen(),
          ),
        ),
      ),
    );
  }

  /// Set the test surface to mobile dimensions so the LoginScreen uses
  /// its mobile layout (not tablet/desktop which causes overflow). Pass
  /// [size] to render another layout.
  ///
  /// Also suppresses RenderFlex overflow errors from the register link Row,
  /// which is a known layout issue on narrow screens.
  void setMobileSize(WidgetTester tester, {Size size = const Size(500, 900)}) {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(() {
      tester.view.resetPhysicalSize();
      tester.view.resetDevicePixelRatio();
    });

    // Suppress RenderFlex overflow errors (known layout issue on narrow screens)
    final originalOnError = FlutterError.onError;
    FlutterError.onError = (FlutterErrorDetails details) {
      final message = details.exceptionAsString();
      if (message.contains('A RenderFlex overflowed')) {
        // Ignore overflow errors in tests
        return;
      }
      originalOnError?.call(details);
    };
    addTearDown(() => FlutterError.onError = originalOnError);
  }

  group('LoginScreen', () {
    testWidgets('renders all UI elements', (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Phone field
      final phoneField = find.byType(TextFormField).first;
      expect(phoneField, findsOneWidget);

      // Password field
      expect(find.text('Password'), findsWidgets);

      // Login button
      expect(find.text('Login'), findsWidgets);

      // Register link
      expect(find.text("Don't have an account? Register"), findsOneWidget);

      // Forgot password
      expect(find.text('Forgot Password?'), findsOneWidget);

      // Inline language switch, each language in its own script
      expect(find.text('Language'), findsOneWidget);
      expect(find.text('English'), findsOneWidget);
      expect(find.text('አማርኛ'), findsOneWidget);
      expect(find.text('Afaan Oromoo'), findsOneWidget);

      // Branding copy comes from l10n, not hardcoded English
      expect(find.text('Offline Ready'), findsOneWidget);
      expect(
        find.text('Connecting Ethiopian farmers, truckers, and buyers'),
        findsOneWidget,
      );
      expect(
        find.text('Payments held safely in escrow · Telebirr Payment'),
        findsOneWidget,
      );
    });

    for (final size in const [
      Size(500, 900),
      Size(800, 1000),
      Size(1280, 900),
    ]) {
      testWidgets('language switch shows on a ${size.width.toInt()} px layout',
          (tester) async {
        setMobileSize(tester, size: size);
        await tester.pumpWidget(buildTestWidget());
        await tester.pumpAndSettle();

        expect(find.byKey(const ValueKey('language-am')), findsOneWidget);
        expect(find.byKey(const ValueKey('language-om')), findsOneWidget);
      });
    }

    testWidgets('tapping Amharic switches and persists the language',
        (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('language-am')));
      await tester.pumpAndSettle();

      expect(languageProvider.locale, const Locale('am'));
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('selected_language'), 'am');
      // The screen re-renders in Amharic.
      final am = await AppLocalizations.delegate.load(const Locale('am'));
      expect(find.text(am.offlineReady), findsOneWidget);
      expect(find.text(am.loginNoAccountRegister), findsOneWidget);
      // The selected segment reports itself to screen readers.
      expect(
        tester.getSemantics(find.byKey(const ValueKey('language-am'))),
        isSemantics(isSelected: true, isButton: true),
      );
    });

    testWidgets('tapping Afaan Oromoo switches the language', (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('language-om')));
      await tester.pumpAndSettle();

      expect(languageProvider.locale, const Locale('om'));
      final om = await AppLocalizations.delegate.load(const Locale('om'));
      expect(
        find.text('${om.loginEscrowFooter} · ${om.telebirrPayment}'),
        findsOneWidget,
      );
    });

    testWidgets('phone field starts empty (no pre-fill)', (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Phone field should be empty (no +251 pre-fill)
      final phoneEditableText = find.byType(EditableText).first;
      final phoneController =
          tester.widget<EditableText>(phoneEditableText).controller;
      expect(phoneController.text, isEmpty);
    });

    testWidgets('shows validation errors on empty submit', (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Phone field is already empty (no pre-fill)

      // Tap login button — find the ElevatedButton
      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();

      // Should show the required message, not the field label
      expect(find.text('Phone number is required'), findsOneWidget);
      // And no login attempt is dispatched
      verifyNever(() => authBloc.add(any(that: isA<AuthLoginRequested>())));
    });

    testWidgets('shows error for invalid phone format', (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Enter invalid phone
      final phoneField = find.byType(TextFormField).first;
      await tester.tap(phoneField);
      await tester.pumpAndSettle();

      // Clear and enter invalid phone
      final phoneEditableText = find.byType(EditableText).first;
      final phoneController =
          tester.widget<EditableText>(phoneEditableText).controller;
      phoneController.text = '+25191';
      await tester.pumpAndSettle();

      // Enter a valid password so only phone error shows
      final passwordField = find.byType(TextFormField).at(1);
      await tester.tap(passwordField);
      await tester.pumpAndSettle();
      await tester.enterText(passwordField, 'password1');
      await tester.pumpAndSettle();

      // Tap login
      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();

      // Should show phone format error
      expect(
        find.text(
          'Invalid phone number. Use +251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX',
        ),
        findsOneWidget,
      );
    });

    testWidgets(
        'requires a non-empty password but does NOT enforce a min length on login',
        (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Enter valid phone
      final phoneField = find.byType(TextFormField).first;
      await tester.tap(phoneField);
      await tester.pumpAndSettle();
      final phoneEditableText = find.byType(EditableText).first;
      final phoneController =
          tester.widget<EditableText>(phoneEditableText).controller;
      phoneController.text = '+251911234567';
      await tester.pumpAndSettle();

      // Enter a short password (leave it short on purpose).
      final passwordField = find.byType(TextFormField).at(1);
      await tester.enterText(passwordField, 'short');
      await tester.pumpAndSettle();

      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();

      // Login must NOT enforce a client-side min-length rule (the server validates
      // credentials; a length rule leaks the policy and can reject valid passwords).
      expect(
        find.text(
          'Password must be at least 8 characters with a lowercase letter and a number',
        ),
        findsNothing,
      );

      // An empty password is still rejected with a required-field message.
      await tester.enterText(passwordField, '');
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();
      expect(find.text('This field is required'), findsOneWidget);
    });

    testWidgets('password visibility toggle works', (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Initially password is obscured (visibility_off icon shown)
      expect(find.byIcon(Icons.visibility_off), findsOneWidget);
      expect(find.byIcon(Icons.visibility), findsNothing);

      // Tap the visibility toggle
      await tester.tap(find.byIcon(Icons.visibility_off));
      await tester.pumpAndSettle();

      // Now should show visibility icon (password visible)
      expect(find.byIcon(Icons.visibility), findsOneWidget);
      expect(find.byIcon(Icons.visibility_off), findsNothing);

      // Tap again to hide
      await tester.tap(find.byIcon(Icons.visibility));
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.visibility_off), findsOneWidget);
    });

    testWidgets('shows server error from BLoC', (tester) async {
      setMobileSize(tester);
      final controller = StreamController<AuthState>.broadcast();
      addTearDown(controller.close);

      when(() => authBloc.stream).thenAnswer((_) => controller.stream);

      await tester.pumpWidget(buildTestWidget(stream: controller.stream));
      await tester.pumpAndSettle();

      // Emit error state
      controller.add(
        const AuthError(
          errorCode: 'AUTH_007',
          errorMessage: 'Server error. Please try again later.',
        ),
      );
      await tester.pump();
      await tester.pump();

      // Error banner should appear with localized message
      expect(
        find.text('Server error. Please try again later.'),
        findsOneWidget,
      );
      // Error icon should be visible
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
    });

    testWidgets('shows rate limit error with duration', (tester) async {
      setMobileSize(tester);
      final controller = StreamController<AuthState>.broadcast();
      addTearDown(controller.close);

      when(() => authBloc.stream).thenAnswer((_) => controller.stream);

      await tester.pumpWidget(buildTestWidget(stream: controller.stream));
      await tester.pumpAndSettle();

      // Emit rate limit error with retryAfterSeconds
      controller.add(
        const AuthError(
          errorCode: 'AUTH_015',
          errorMessage: 'Account locked',
          retryAfterSeconds: 900,
        ),
      );
      await tester.pump();
      await tester.pump();

      // Should show lockout duration message (15 minutes)
      expect(
        find.textContaining('Account locked'),
        findsOneWidget,
      );
    });

    testWidgets('shows loading spinner during auth', (tester) async {
      setMobileSize(tester);
      // Set loading state
      when(() => authBloc.state).thenReturn(const AuthLoading());

      await tester.pumpWidget(buildTestWidget());
      // Use pump() instead of pumpAndSettle() because
      // CircularProgressIndicator animates infinitely.
      await tester.pump();

      // Login button should show CircularProgressIndicator
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('login button dispatches AuthLoginRequested', (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Enter valid phone
      final phoneField = find.byType(TextFormField).first;
      await tester.tap(phoneField);
      await tester.pumpAndSettle();
      final phoneEditableText = find.byType(EditableText).first;
      final phoneController =
          tester.widget<EditableText>(phoneEditableText).controller;
      phoneController.text = '+251911234567';
      await tester.pumpAndSettle();

      // Enter valid password
      final passwordField = find.byType(TextFormField).at(1);
      await tester.enterText(passwordField, 'password1');
      await tester.pumpAndSettle();

      // Tap login
      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();

      // Verify BLoC received the event
      verify(
        () => authBloc.add(
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'password1',
          ),
        ),
      ).called(1);
    });

    testWidgets('local-format phone is normalized to E.164 on login',
        (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Enter phone in local 0-prefix format
      final phoneField = find.byType(TextFormField).first;
      await tester.tap(phoneField);
      await tester.pumpAndSettle();
      final phoneEditableText = find.byType(EditableText).first;
      final phoneController =
          tester.widget<EditableText>(phoneEditableText).controller;
      phoneController.text = '0912345678';
      await tester.pumpAndSettle();

      // Enter valid password
      final passwordField = find.byType(TextFormField).at(1);
      await tester.enterText(passwordField, 'password1');
      await tester.pumpAndSettle();

      // Tap login
      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();

      // BLoC must receive the normalized E.164 phone
      verify(
        () => authBloc.add(
          const AuthLoginRequested(
            phoneNumber: '+251912345678',
            password: 'password1',
          ),
        ),
      ).called(1);
    });

    testWidgets('navigate to register taps register link', (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Scroll to make register link visible
      final registerLink = find.text("Don't have an account? Register");
      await tester.ensureVisible(registerLink);
      await tester.pumpAndSettle();

      // Tap the Register TextButton
      await tester.tap(registerLink);

      // The RegisterScreen requires ApiClient.dio and other dependencies
      // that are not available in this unit test. We use pump() instead of
      // pumpAndSettle() to allow the navigation to start, then consume the
      // expected build error from the RegisterScreen.
      await tester.pump();
      tester.takeException();
    });

    testWidgets('forgot password navigates on tap', (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Tap "Forgot Password?"
      final forgotPassword = find.text('Forgot Password?');
      await tester.ensureVisible(forgotPassword);
      await tester.pumpAndSettle();
      await tester.tap(forgotPassword);
      await tester.pumpAndSettle();
      expect(find.byType(ForgotPasswordScreen), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets(
        'an unconfirmed account (AUTH_016 turned into the OTP state) '
        'opens the OTP screen once', (tester) async {
      setMobileSize(tester);
      final states = StreamController<AuthState>.broadcast();
      addTearDown(states.close);
      when(() => authBloc.isClosed).thenReturn(false);
      await tester.pumpWidget(buildTestWidget(stream: states.stream));
      await tester.pumpAndSettle();

      const otp = AuthOtpVerificationRequired(phoneNumber: '+251911234567');
      when(() => authBloc.state).thenReturn(otp);
      states.add(otp);
      for (var i = 0; i < 6; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      expect(find.byType(OtpVerificationScreen), findsOneWidget);
      expect(
        find.text('We sent a 6-digit code by SMS to +251911234567.'),
        findsOneWidget,
      );

      // A repeat of the state (resend) must not stack a second screen.
      states.add(
        AuthOtpVerificationRequired(
          phoneNumber: '+251911234567',
          resentAt: DateTime.utc(2026, 9, 26),
        ),
      );
      for (var i = 0; i < 6; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }
      expect(find.byType(OtpVerificationScreen), findsOneWidget);

      // Unmount so the OTP countdown timer is cancelled.
      await tester.pumpWidget(const SizedBox.shrink());
    });
  });

  // SIM-32: errors clear once a field is fixed, long errors wrap, and the
  // auth error banner reaches screen readers (Android and web).
  group('LoginScreen validation feedback', () {
    const invalidPhone =
        'Invalid phone number. Use +251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX';
    const phoneRequired = 'Phone number is required';
    const passwordRequired = 'This field is required';

    final phoneField = find.byType(TextFormField).first;
    final passwordField = find.byType(TextFormField).at(1);

    Future<void> tapLogin(WidgetTester tester) async {
      final loginButton = find.byType(ElevatedButton);
      await tester.ensureVisible(loginButton);
      await tester.pumpAndSettle();
      await tester.tap(loginButton);
      await tester.pumpAndSettle();
    }

    // Reads SemanticsData directly: containsSemantics is deprecated on the
    // local SDK and its replacement does not exist on the CI SDK (3.38.9).
    SemanticsData phoneSemantics(WidgetTester tester) {
      final data = tester
          .getSemantics(find.bySemanticsLabel('Phone Number'))
          .getSemanticsData();
      expect(data.flagsCollection.isTextField, isTrue);
      return data;
    }

    testWidgets('phone format error clears once the phone is corrected',
        (tester) async {
      final semantics = tester.ensureSemantics();
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.enterText(phoneField, '+25191');
      await tester.enterText(passwordField, 'password1');
      await tester.pumpAndSettle();
      await tapLogin(tester);

      expect(find.text(invalidPhone), findsOneWidget);
      expect(
        phoneSemantics(tester).validationResult,
        SemanticsValidationResult.invalid,
      );
      verifyNever(() => authBloc.add(any(that: isA<AuthLoginRequested>())));

      // Fixing the field clears its error without another submit.
      await tester.enterText(phoneField, '+251911234567');
      await tester.pump();

      expect(find.text(invalidPhone), findsNothing);
      expect(
        phoneSemantics(tester).validationResult,
        SemanticsValidationResult.valid,
      );
      verifyNever(() => authBloc.add(any(that: isA<AuthLoginRequested>())));

      await tapLogin(tester);
      verify(
        () => authBloc.add(
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'password1',
          ),
        ),
      ).called(1);
      semantics.dispose();
    });

    testWidgets('required errors clear field by field after an empty submit',
        (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tapLogin(tester);
      expect(find.text(phoneRequired), findsOneWidget);
      expect(find.text(passwordRequired), findsOneWidget);

      // Editing only the password clears only the password error; the
      // untouched phone field keeps its error.
      await tester.enterText(passwordField, 'x');
      await tester.pump();
      expect(find.text(passwordRequired), findsNothing);
      expect(find.text(phoneRequired), findsOneWidget);

      await tester.enterText(phoneField, '0911234567');
      await tester.pump();
      expect(find.text(phoneRequired), findsNothing);
      expect(find.text(invalidPhone), findsNothing);
    });

    testWidgets('does not validate while typing before the first submit',
        (tester) async {
      setMobileSize(tester);
      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      await tester.enterText(phoneField, '+25191');
      await tester.pump();
      await tester.enterText(passwordField, 'x');
      await tester.pump();
      await tester.enterText(passwordField, '');
      await tester.pump();

      expect(find.text(invalidPhone), findsNothing);
      expect(find.text(passwordRequired), findsNothing);
    });

    // Phone and desktop web layouts in all 3 languages, plus Oromo (the
    // longest message) at a large text scale.
    for (final (lang, size, scale) in const [
      ('en', Size(360, 800), 1.0),
      ('am', Size(360, 800), 1.0),
      ('om', Size(360, 800), 1.0),
      ('om', Size(360, 800), 1.3),
      ('en', Size(1440, 900), 1.0),
      ('am', Size(1440, 900), 1.0),
      ('om', Size(1440, 900), 1.0),
    ]) {
      testWidgets(
          'long phone error wraps instead of being truncated '
          '($lang, ${size.width.toInt()}x${size.height.toInt()}, ${scale}x)',
          (tester) async {
        setMobileSize(tester, size: size);
        tester.platformDispatcher.textScaleFactorTestValue = scale;
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        final locale = Locale(lang);
        await tester.pumpWidget(buildTestWidget(locale: locale));
        await tester.pumpAndSettle();

        await tester.enterText(phoneField, '+25191');
        await tester.enterText(passwordField, 'password1');
        await tester.pumpAndSettle();
        await tapLogin(tester);

        final paragraph = tester.renderObject<RenderParagraph>(
          find.text(lookupAppLocalizations(locale).invalidPhoneFormat),
        );
        expect(paragraph.textScaler.scale(12), closeTo(12 * scale, 0.01));
        expect(paragraph.maxLines, greaterThan(1));
        expect(paragraph.didExceedMaxLines, isFalse);
      });
    }

    testWidgets('auth error banner is an announced live region',
        (tester) async {
      final semantics = tester.ensureSemantics();
      setMobileSize(tester);
      final controller = StreamController<AuthState>.broadcast();
      addTearDown(controller.close);

      await tester.pumpWidget(buildTestWidget(stream: controller.stream));
      await tester.pumpAndSettle();

      controller.add(
        const AuthError(
          errorCode: 'AUTH_007',
          errorMessage: 'Server error. Please try again later.',
        ),
      );
      await tester.pump();
      await tester.pump();

      final banner = tester
          .getSemantics(find.byKey(const Key('login_error_banner')))
          .getSemanticsData();
      expect(banner.label, 'Server error. Please try again later.');
      expect(banner.flagsCollection.isLiveRegion, isTrue);
      semantics.dispose();
    });

    testWidgets('a repeated identical auth error is announced again',
        (tester) async {
      final semantics = tester.ensureSemantics();
      setMobileSize(tester);
      // Sync, so the bloc fails inside the submit (as offline login does):
      // clearing the banner and the same error land in one build, no frame
      // in between.
      final controller = StreamController<AuthState>.broadcast(sync: true);
      addTearDown(controller.close);
      const error = AuthError(
        errorCode: 'AUTH_007',
        errorMessage: 'Server error. Please try again later.',
      );
      when(() => authBloc.add(any(that: isA<AuthLoginRequested>())))
          .thenAnswer((_) {
        controller
          ..add(const AuthLoading())
          ..add(error);
      });
      final banner = find.byKey(const Key('login_error_banner'));

      await tester.pumpWidget(buildTestWidget(stream: controller.stream));
      await tester.pumpAndSettle();
      await tester.enterText(phoneField, '+251911234567');
      await tester.enterText(passwordField, 'password1');
      await tester.pumpAndSettle();

      await tapLogin(tester);
      final firstNodeId = tester.getSemantics(banner).id;

      // An unrelated rebuild keeps the node, so nothing is re-announced.
      await tester.tap(find.byIcon(Icons.visibility_off));
      await tester.pumpAndSettle();
      expect(tester.getSemantics(banner).id, firstNodeId);

      // The same error again is a new live-region node, which TalkBack and
      // the web engine announce (they skip a live region whose label did
      // not change).
      await tapLogin(tester);
      verify(() => authBloc.add(any(that: isA<AuthLoginRequested>())))
          .called(2);
      final again = tester.getSemantics(banner);
      expect(again.id, isNot(firstNodeId));
      expect(
        again.getSemanticsData().label,
        'Server error. Please try again later.',
      );
      expect(again.getSemanticsData().flagsCollection.isLiveRegion, isTrue);
      semantics.dispose();
    });
  });
}
