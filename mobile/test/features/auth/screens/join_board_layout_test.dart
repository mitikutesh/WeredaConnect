import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/auth/screens/otp_verification_screen.dart';
import 'package:wereda_connect/features/auth/screens/registration/registration_flow.dart';
import 'package:wereda_connect/features/onboarding/screens/welcome_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/language_segmented_picker.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

/// The Join boards (specs/design-canvas: WelcomeM, RegisterRoleM,
/// RegisterOtpM) are one composition at every width: the phone column,
/// centred on a wide window, with no app bar and the action at the foot.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late MockAuthBloc bloc;
  late StreamController<AuthState> states;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(Uri());
    registerFallbackValue(CancelToken());
    ApiClient().initialize(environment: Environment.development);
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    messenger.setMockStreamHandler(
      const EventChannel('com.llfbandit.app_links/events'),
      MockStreamHandler.inline(onListen: (_, __) {}),
    );
    messenger.setMockMethodCallHandler(
      const MethodChannel('com.llfbandit.app_links/messages'),
      (_) async => null,
    );
  });

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    bloc = MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    when(() => bloc.state).thenReturn(const AuthUnauthenticated());
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    when(() => bloc.isClosed).thenReturn(false);
  });

  tearDown(() => unawaited(states.close()));

  void useWindow(WidgetTester tester, Size size) {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  Widget app(Widget home) => ChangeNotifierProvider<LanguageProvider>(
        create: (_) => LanguageProvider(),
        child: BlocProvider<AuthBloc>.value(
          value: bloc,
          child: MaterialApp(
            theme: AppTheme.light(),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: home,
          ),
        ),
      );

  group('Welcome and login', () {
    testWidgets('the first launch shows the login board', (tester) async {
      useWindow(tester, const Size(390, 844));
      await tester.pumpWidget(app(const WelcomeScreen()));
      await tester.pumpAndSettle();

      expect(find.byType(LoginScreen), findsOneWidget);
      expect(find.text('Welcome to WeredaConnect'), findsOneWidget);
      expect(find.text('Works without network'), findsOneWidget);
      expect(find.byType(LanguageSegmentedPicker), findsOneWidget);
      expect(find.text("Don't have an account? Register"), findsOneWidget);
      // The brand tile; no bar, no guest link: the board has none.
      expect(find.byIcon(Icons.agriculture), findsOneWidget);
      expect(find.byType(AppBar), findsNothing);
      expect(find.text('Explore as Guest'), findsNothing);
    });

    testWidgets('a 1280 px window centres the same phone column',
        (tester) async {
      useWindow(tester, const Size(1280, 900));
      await tester.pumpWidget(app(const LoginScreen()));
      await tester.pumpAndSettle();

      // One composition: the same pieces as on the phone, no hero panel.
      expect(find.text('Welcome to WeredaConnect'), findsOneWidget);
      expect(find.byType(Card), findsNothing);

      final picker = tester.getRect(find.byType(LanguageSegmentedPicker));
      expect(picker.width, lessThanOrEqualTo(480));
      // Centred: as much room on the left as on the right.
      expect((picker.left - (1280 - picker.right)).abs(), lessThan(2));
    });

    testWidgets('Register sits under the form at the foot', (tester) async {
      useWindow(tester, const Size(390, 844));
      await tester.pumpWidget(app(const LoginScreen()));
      await tester.pumpAndSettle();

      final login = tester
          .getRect(find.widgetWithText(ElevatedButton, 'Login as Farmer'));
      final register = tester.getRect(
        find.widgetWithText(OutlinedButton, "Don't have an account? Register"),
      );
      expect(register.top, greaterThan(login.bottom));
      // Both are 54 tall pills: the orange action, the outlined Register.
      expect(register.height, 54);
      expect(login.height, 54);
    });
  });

  group('Register steps', () {
    testWidgets('the step header is the board header, not an app bar',
        (tester) async {
      useWindow(tester, const Size(390, 844));
      await tester.pumpWidget(app(const RegistrationFlow()));
      await tester.pumpAndSettle();

      expect(find.byType(AppBar), findsNothing);
      expect(find.byType(JoinBackButton), findsOneWidget);
      expect(find.text('Step 1 of 4'), findsOneWidget);
      expect(find.text('Select Role'), findsOneWidget);
      // The language switch stays reachable.
      expect(find.byIcon(Icons.language), findsOneWidget);

      // The orange Continue pill (the theme's button, no local style), 54
      // tall, at the foot.
      final button = find.widgetWithText(FilledButton, 'Continue');
      expect(button, findsOneWidget);
      expect(tester.getSize(button).height, 54);
      expect(tester.widget<FilledButton>(button).style, isNull);
      // Step 1 of 4: one black segment, three on the track.
      Color? barColor(int index) => (tester
              .widget<Container>(find.byKey(ValueKey('join_step_bar_$index')))
              .decoration! as BoxDecoration)
          .color;
      expect(barColor(0), AppTheme.ink);
      expect(barColor(1), AppTheme.track);
      expect(barColor(3), AppTheme.track);
      expect(
        tester.getRect(button).bottom,
        greaterThan(tester.getRect(find.text('Select Role')).bottom),
      );
    });

    testWidgets(
        'each role card wears its own colours; the picked one is raised',
        (tester) async {
      useWindow(tester, const Size(390, 844));
      await tester.pumpWidget(app(const RegistrationFlow()));
      await tester.pumpAndSettle();

      Finder card(UserRole role) =>
          find.byKey(ValueKey('register_role_${role.name}'));
      for (final role in UserRole.values) {
        final widget = tester.widget<JoinChoiceCard>(card(role));
        expect(widget.iconBackground, RoleColors.forRole(role).tint);
        expect(widget.selected, isFalse);
      }

      await tester.tap(card(UserRole.trucker));
      await tester.pumpAndSettle();

      expect(
        tester.widget<JoinChoiceCard>(card(UserRole.trucker)).selected,
        isTrue,
      );
      // Raised on paper: the picked card's Material is the paper grey.
      final material = tester.widget<Material>(
        find
            .descendant(
              of: card(UserRole.trucker),
              matching: find.byType(Material),
            )
            .first,
      );
      expect(material.color, AppTheme.paper);
      // The steps now wear the picked role's colours.
      expect(
        RoleColors.of(tester.element(find.text('Continue'))).primary,
        RoleColors.trucker.primary,
      );
      // The other cards keep their own tints.
      expect(
        tester.widget<JoinChoiceCard>(card(UserRole.buyer)).iconBackground,
        RoleColors.buyer.tint,
      );
    });

    testWidgets('a 1280 px window keeps the step at phone width, centred',
        (tester) async {
      useWindow(tester, const Size(1280, 900));
      await tester.pumpWidget(app(const RegistrationFlow()));
      await tester.pumpAndSettle();

      final button =
          tester.getRect(find.widgetWithText(FilledButton, 'Continue'));
      expect(button.width, lessThanOrEqualTo(480));
      expect((button.left - (1280 - button.right)).abs(), lessThan(2));
    });

    testWidgets('the Fayda step keeps its own button at the foot',
        (tester) async {
      // Tall enough for the whole step in the test font (wider than the
      // app's), so the note is not scrolled under the button.
      useWindow(tester, const Size(390, 1100));
      await tester.pumpWidget(app(const RegistrationFlow()));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Farmer'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Continue'));
      await tester.pumpAndSettle();

      expect(find.text('Step 2 of 3'), findsOneWidget);
      expect(find.text('Continue'), findsNothing);
      // FilledButton.icon is a private subclass on some Flutter versions,
      // so match by `is` rather than the exact type.
      final verify = find.ancestor(
        of: find.text('Verify with Fayda'),
        matching: find.byWidgetPredicate((w) => w is FilledButton),
      );
      expect(verify, findsOneWidget);
      expect(find.text('Needs internet for this step.'), findsOneWidget);
      expect(
        tester.getRect(verify).top,
        greaterThan(
          tester
              .getRect(
                find.text(
                  'A verified Fayda ID is required to create an account. '
                  'It only takes a minute.',
                ),
              )
              .bottom,
        ),
      );
    });
  });

  group('Confirm your phone', () {
    testWidgets('no app bar, the back square, Verify at the foot',
        (tester) async {
      useWindow(tester, const Size(390, 844));
      await tester.pumpWidget(
        app(const OtpVerificationScreen(phoneNumber: '+251911234567')),
      );
      await tester.pump();

      expect(find.byType(AppBar), findsNothing);
      expect(find.byType(JoinBackButton), findsOneWidget);
      // The number is part of the sentence (in bold on the board).
      expect(
        find.text(
          'We sent a 6-digit code by SMS to '
          '+251\u00A0911\u00A0234\u00A0567.',
        ),
        findsOneWidget,
      );
      final verify = tester.getRect(find.byKey(const Key('otp_verify_button')));
      final note = tester.getRect(
        find.text(
          'We ask for this only when the number differs from your Fayda '
          'record.',
        ),
      );
      expect(verify.top, greaterThan(note.bottom));
      expect(verify.height, 54);
      // At the foot of an 844 tall screen.
      expect(verify.bottom, greaterThan(780));
    });
  });
}
