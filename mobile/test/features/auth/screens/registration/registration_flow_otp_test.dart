import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/otp_verification_screen.dart';
import 'package:wereda_connect/features/auth/screens/registration/registration_flow.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class MockFaydaOidcService extends Mock implements FaydaOidcService {}

/// Pump frames without waiting for the OTP countdown to finish.
Future<void> settle(WidgetTester tester) async {
  for (var i = 0; i < 8; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late MockAuthBloc bloc;
  late StreamController<AuthState> states;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(Uri());
    // RegistrationFlow builds a FaydaOidcService on the shared client.
    ApiClient().initialize(environment: Environment.development);
    // RegistrationFlow listens for the Fayda deep link through app_links.
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
    bloc = MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    when(() => bloc.state).thenReturn(const AuthUnauthenticated());
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    when(() => bloc.isClosed).thenReturn(false);
  });

  tearDown(() => unawaited(states.close()));

  Widget app() => BlocProvider<AuthBloc>.value(
        value: bloc,
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: RegistrationFlow(),
        ),
      );

  void useTallPhone(WidgetTester tester) {
    tester.view.physicalSize = const Size(500, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  testWidgets('a 202 register (otpRequired) opens the OTP screen once',
      (tester) async {
    useTallPhone(tester);
    await tester.pumpWidget(app());
    await settle(tester);

    final otp = AuthOtpVerificationRequired(
      phoneNumber: '+251911234567',
      expiresAt: DateTime.now().toUtc().add(const Duration(minutes: 5)),
    );
    when(() => bloc.state).thenReturn(otp);
    states.add(otp);
    await settle(tester);

    expect(find.byType(OtpVerificationScreen), findsOneWidget);
    expect(
      find.text('We sent a 6-digit code by SMS to +251911234567.'),
      findsOneWidget,
    );

    // The resend state while the screen is open must not stack another.
    states.add(
      AuthOtpVerificationRequired(
        phoneNumber: '+251911234567',
        resentAt: DateTime.now().toUtc(),
      ),
    );
    await settle(tester);
    expect(find.byType(OtpVerificationScreen), findsOneWidget);

    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('FAYDA_003 tells the user they can continue and verify later',
      (tester) async {
    useTallPhone(tester);
    await tester.pumpWidget(app());
    await settle(tester);

    states.add(
      const AuthError(
        errorCode: AuthErrorCode.faydaServiceUnavailable,
        errorMessage: 'Fayda down',
      ),
    );
    await settle(tester);

    expect(
      find.text(
        'Fayda is unavailable right now. You can continue and verify '
        'later from your profile.',
      ),
      findsOneWidget,
    );
    // Still on the registration flow, not an OTP or error screen.
    expect(find.byType(RegistrationFlow), findsOneWidget);
    expect(find.byType(OtpVerificationScreen), findsNothing);
  });

  group('Fayda callback', () {
    late MockFaydaOidcService fayda;
    late StreamController<Uri> links;
    final realService = FaydaOidcService(dio: Dio());

    setUp(() {
      fayda = MockFaydaOidcService();
      links = StreamController<Uri>.broadcast();
      when(() => fayda.parseCallback(any())).thenAnswer(
        (inv) =>
            realService.parseCallback(inv.positionalArguments.first as Uri),
      );
      when(
        () => fayda.startRegistration(phoneNumber: any(named: 'phoneNumber')),
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
        ),
      ).thenAnswer(
        (_) async => const FaydaExchangeResult(
          isSuccess: true,
          verificationToken: 'vt-1',
          verifiedName: 'Abebe Kebede',
        ),
      );
    });

    tearDown(() => unawaited(links.close()));

    Widget flow() => BlocProvider<AuthBloc>.value(
          value: bloc,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: RegistrationFlow(
              faydaService: fayda,
              faydaLinks: links.stream,
              launcher: (_) async => true,
            ),
          ),
        );

    Future<void> startFayda(WidgetTester tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(flow());
      await settle(tester);
      await tester.tap(find.text('Farmer'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);
      await tester.tap(find.text('Verify with Fayda'));
      await settle(tester);
    }

    const mismatch =
        'The Fayda session expired. Please start verification again.';

    testWidgets('a stateless callback is ignored and the wait goes on',
        (tester) async {
      await startFayda(tester);

      links.add(
        Uri.parse('weredaconnect://fayda-callback'),
      );
      await settle(tester);

      expect(find.text(mismatch), findsNothing);
      verifyNever(
        () => fayda.exchangeCode(
          authCode: any(named: 'authCode'),
          state: any(named: 'state'),
        ),
      );

      links
          .add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
      await settle(tester);

      verify(() => fayda.exchangeCode(authCode: 'c-1', state: 'st-1'))
          .called(1);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('a callback with a foreign state is refused', (tester) async {
      await startFayda(tester);

      links.add(Uri.parse('weredaconnect://fayda-callback?code=c&state=evil'));
      await settle(tester);

      expect(find.text(mismatch), findsOneWidget);
      verifyNever(
        () => fayda.exchangeCode(
          authCode: any(named: 'authCode'),
          state: any(named: 'state'),
        ),
      );
      await tester.pumpWidget(const SizedBox.shrink());
    });
  });
}
