import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/registration/registration_flow.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

class _FakeAuthEvent extends Fake implements AuthEvent {}

class _MockFaydaOidcService extends Mock implements FaydaOidcService {}

const _contactInText = 'Phone numbers are not allowed here. Remove the '
    'number: contact opens after payment is held in escrow.';

/// Pump frames without waiting on timers that never settle.
Future<void> _settle(WidgetTester tester) async {
  for (var i = 0; i < 8; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

/// FUP-52: the registration form refuses a phone number in the display name,
/// woreda or company name before anything is sent, and explains the server's
/// AUTH_027 refusal in en, am and om. Same Dart on Android and web.
void main() {
  late _MockAuthBloc bloc;
  late StreamController<AuthState> states;
  late _MockFaydaOidcService fayda;
  late StreamController<Uri> links;
  final realService = FaydaOidcService(dio: Dio());

  setUpAll(() {
    registerFallbackValue(_FakeAuthEvent());
    registerFallbackValue(Uri());
    registerFallbackValue(CancelToken());
  });

  setUp(() {
    bloc = _MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    when(() => bloc.state).thenReturn(const AuthUnauthenticated());
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    when(() => bloc.isClosed).thenReturn(false);

    fayda = _MockFaydaOidcService();
    links = StreamController<Uri>.broadcast();
    when(() => fayda.parseCallback(any())).thenAnswer(
      (inv) => realService.parseCallback(inv.positionalArguments.first as Uri),
    );
    when(
      () => fayda.startRegistration(
        phoneNumber: any(named: 'phoneNumber'),
        onRetry: any(named: 'onRetry'),
        cancelToken: any(named: 'cancelToken'),
      ),
    ).thenAnswer(
      (_) async => FaydaStartResult(
        isSuccess: true,
        state: 'st-1',
        authorizeUrl: Uri.parse('https://esignet.test/authorize'),
      ),
    );
    when(
      () => fayda.exchangeCode(
        authCode: any(named: 'authCode'),
        state: any(named: 'state'),
        onRetry: any(named: 'onRetry'),
        cancelToken: any(named: 'cancelToken'),
      ),
    ).thenAnswer(
      (_) async => const FaydaExchangeResult(
        isSuccess: true,
        verificationToken: 'vt-1',
        verifiedName: 'Abebe Kebede',
      ),
    );
  });

  tearDown(() {
    unawaited(states.close());
    unawaited(links.close());
  });

  Widget flow({Locale? locale}) => BlocProvider<AuthBloc>.value(
        value: bloc,
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          home: RegistrationFlow(
            faydaService: fayda,
            faydaLinks: links.stream,
            launcher: (_) async => true,
          ),
        ),
      );

  void useTallPhone(WidgetTester tester) {
    tester.view.physicalSize = const Size(500, 1200);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  /// Role, then a verified Fayda step, then the role details (if any).
  Future<void> passFayda(WidgetTester tester, String role) async {
    useTallPhone(tester);
    await tester.pumpWidget(flow());
    await _settle(tester);
    await tester.tap(find.text(role));
    await _settle(tester);
    await tester.tap(find.text('Continue'));
    await _settle(tester);
    await tester.tap(find.text('Verify with Fayda'));
    await _settle(tester);
    links.add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
    await _settle(tester);
    await tester.tap(find.text('Continue'));
    await _settle(tester);
  }

  Future<void> signUpAsFarmer(
    WidgetTester tester, {
    required String name,
    String woreda = 'Adama',
  }) async {
    await passFayda(tester, 'Farmer');
    await tester.enterText(
      find.byKey(const Key('register_phone')),
      '0912345678',
    );
    await tester.enterText(
      find.byKey(const Key('register_display_name')),
      name,
    );
    await tester.enterText(
      find.byKey(const Key('register_wereda')),
      woreda,
    );
    await tester.enterText(
      find.byKey(const Key('register_password')),
      'secure123',
    );
    await tester.enterText(
      find.byKey(const Key('register_confirm_password')),
      'secure123',
    );
    await _settle(tester);
    await tester.tap(find.text('Register'));
    await _settle(tester);
  }

  List<AuthRegisterRequested> registrations() => verify(
        () => bloc.add(captureAny(that: isA<AuthRegisterRequested>())),
      ).captured.cast<AuthRegisterRequested>();

  for (final name in [
    'Abebe 0911234567',
    'አበበ 0911 23 45 67',
    'Caalaa +251 911 234 567',
    'Abebe 0911​234567',
    'አበበ ፱፩፩፪፫፬፭፮፯',
  ]) {
    testWidgets('a number in the display name blocks sign up: $name',
        (tester) async {
      await signUpAsFarmer(tester, name: name);

      expect(find.text(_contactInText), findsOneWidget);
      verifyNever(
        () => bloc.add(any(that: isA<AuthRegisterRequested>())),
      );
      await tester.pumpWidget(const SizedBox.shrink());
    });
  }

  testWidgets('a number in the woreda blocks sign up', (tester) async {
    await signUpAsFarmer(
      tester,
      name: 'Abebe Kebede',
      woreda: 'Adama 0712-345-678',
    );

    expect(find.text(_contactInText), findsOneWidget);
    verifyNever(() => bloc.add(any(that: isA<AuthRegisterRequested>())));
    await tester.pumpWidget(const SizedBox.shrink());
  });

  for (final (name, woreda) in const [
    ('Abebe Kebede II', 'Adama Kebele 14'),
    ('አበበ ከበደ', 'ቀበሌ 07 ዞን 3'),
    ('Caalaa Guddataa', 'Ganda 07, Godina 3'),
  ]) {
    testWidgets('names and woredas without a phone number sign up: $name',
        (tester) async {
      await signUpAsFarmer(tester, name: name, woreda: woreda);

      expect(find.text(_contactInText), findsNothing);
      final event = registrations().single;
      expect(event.name, name);
      expect(event.woreda, woreda);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  }

  testWidgets('a company name with a number keeps Continue off',
      (tester) async {
    await passFayda(tester, 'Buyer');
    await tester.tap(find.text('Company'));
    await _settle(tester);

    VoidCallback? continuePressed() => tester
        .widget<FilledButton>(
          find.ancestor(
            of: find.text('Continue'),
            matching: find.byType(FilledButton),
          ),
        )
        .onPressed;

    await tester.enterText(find.byType(TextField), 'Abebe Trading 0911234567');
    await _settle(tester);
    expect(find.text(_contactInText), findsOneWidget);
    expect(continuePressed(), isNull);

    await tester.enterText(find.byType(TextField), 'Abebe Trading 2019');
    await _settle(tester);
    expect(find.text(_contactInText), findsNothing);
    expect(continuePressed(), isNotNull);
    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets(
      'AUTH_027 on sign up keeps the user on Personal Info, and the fixed '
      'form is resent with the same Fayda proof', (tester) async {
    await signUpAsFarmer(tester, name: 'Abebe Kebede');

    states.add(
      const AuthError(
        errorCode: AuthErrorCode.profileContactInText,
        errorMessage: 'Remove the phone number from displayName. Contact '
            'opens after payment is held in escrow.',
      ),
    );
    await _settle(tester);

    expect(
      find.text(
        'Remove the phone number from your name, woreda or company name. '
        'Contact opens after payment is held in escrow.',
      ),
      findsOneWidget,
    );
    // The Fayda proof is kept: no way back to the Fayda step.
    expect(find.text('Personal Info'), findsOneWidget);
    expect(find.text('Retry'), findsNothing);
    expect(find.text('Register'), findsOneWidget);

    // The snackbar times out, the user edits the name and signs up again.
    await tester.pump(const Duration(seconds: 6));
    await _settle(tester);
    await tester.enterText(
      find.byKey(const Key('register_display_name')),
      'Abebe K.',
    );
    await _settle(tester);
    await tester.tap(find.text('Register'));
    await _settle(tester);

    final sent = registrations();
    expect(sent, hasLength(2));
    expect(sent.map((e) => e.faydaVerificationToken), everyElement('vt-1'));
    expect(sent.last.name, 'Abebe K.');
    await tester.pumpWidget(const SizedBox.shrink());
  });

  for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
    testWidgets(
        'AUTH_027 is explained in ${locale.languageCode} on the registration '
        'screen', (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(flow(locale: locale));
      await _settle(tester);

      states.add(
        const AuthError(
          errorCode: AuthErrorCode.profileContactInText,
          errorMessage: 'Remove the phone number from woreda.',
        ),
      );
      await _settle(tester);

      final l10n = lookupAppLocalizations(locale);
      expect(find.text(l10n.authProfileContactInText), findsOneWidget);
      expect(find.textContaining('AUTH_'), findsNothing);
      expect(find.text(l10n.genericError), findsNothing);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  }

  // Fieldway Join boards: one localized step counter for the whole flow. A
  // farmer skips the role details step, so their last step is 3 of 3.
  testWidgets('the step counter follows the role', (tester) async {
    useTallPhone(tester);
    await tester.pumpWidget(flow());
    await _settle(tester);
    expect(find.text('Step 1 of 4'), findsOneWidget);

    await tester.tap(find.text('Farmer'));
    await _settle(tester);
    expect(find.text('Step 1 of 3'), findsOneWidget);
    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('a farmer ends on step 3 of 3', (tester) async {
    await passFayda(tester, 'Farmer');

    expect(find.text('Step 3 of 3'), findsOneWidget);
    expect(find.text('Step 4 of 4'), findsNothing);
    await tester.pumpWidget(const SizedBox.shrink());
  });

  // Register personal board: labels above the fields, the display name
  // first, the two passwords side by side.
  testWidgets('personal info: labels above, passwords side by side',
      (tester) async {
    await passFayda(tester, 'Farmer');

    final name = find.byKey(const Key('register_display_name'));
    final phone = find.byKey(const Key('register_phone'));
    expect(
      tester.getBottomLeft(find.text('Display Name')).dy,
      lessThanOrEqualTo(tester.getTopLeft(name).dy),
    );
    expect(tester.getTopLeft(name).dy, lessThan(tester.getTopLeft(phone).dy));
    final password = find.byKey(const Key('register_password'));
    final confirm = find.byKey(const Key('register_confirm_password'));
    expect(tester.getTopLeft(confirm).dy, tester.getTopLeft(password).dy);
    expect(
      tester.getTopLeft(confirm).dx,
      greaterThan(tester.getTopLeft(password).dx),
    );
    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('a trucker sees step 3 of 4 with Fayda already verified',
      (tester) async {
    await passFayda(tester, 'Trucker');

    expect(find.text('Step 3 of 4'), findsOneWidget);
    expect(find.text('Identity Verified'), findsOneWidget);
    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('the step counter is localized', (tester) async {
    useTallPhone(tester);
    await tester.pumpWidget(flow(locale: const Locale('am')));
    await _settle(tester);
    final am = await AppLocalizations.delegate.load(const Locale('am'));
    expect(find.text(am.registrationStepOf(1, 4)), findsOneWidget);
    await tester.pumpWidget(const SizedBox.shrink());
  });
}
