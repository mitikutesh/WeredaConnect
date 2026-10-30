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
    registerFallbackValue(CancelToken());
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
          onRetry: any(named: 'onRetry'),
          cancelToken: any(named: 'cancelToken'),
        ),
      );

      links
          .add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
      await settle(tester);

      verify(
        () => fayda.exchangeCode(
          authCode: 'c-1',
          state: 'st-1',
          onRetry: any(named: 'onRetry'),
          cancelToken: any(named: 'cancelToken'),
        ),
      ).called(1);
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
          onRetry: any(named: 'onRetry'),
          cancelToken: any(named: 'cancelToken'),
        ),
      );
      await tester.pumpWidget(const SizedBox.shrink());
    });
    Future<void> openFaydaStep(WidgetTester tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(flow());
      await settle(tester);
      await tester.tap(find.text('Farmer'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);
    }

    testWidgets('the Fayda step has no Continue until Fayda verifies',
        (tester) async {
      await openFaydaStep(tester);

      expect(find.text('Continue'), findsNothing);
      expect(
        find.text(
          'A verified Fayda ID is required to create an account. '
          'It only takes a minute.',
        ),
        findsOneWidget,
      );

      await tester.tap(find.text('Verify with Fayda'));
      await settle(tester);
      expect(find.text('Continue'), findsNothing);

      links
          .add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
      await settle(tester);

      expect(find.text('Abebe Kebede'), findsOneWidget);
      expect(find.text('Continue'), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    // Register Fayda board: the step starts under the progress bar, not in
    // the middle of the screen.
    testWidgets('the Fayda step sits at the top of the screen', (tester) async {
      await openFaydaStep(tester);

      expect(
        tester.getTopLeft(find.text('Fayda Identity Verification')).dy,
        lessThan(200),
      );
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('shows retry progress, then a clear message and Retry',
        (tester) async {
      final gate = Completer<FaydaStartResult>();
      when(
        () => fayda.startRegistration(
          phoneNumber: any(named: 'phoneNumber'),
          onRetry: any(named: 'onRetry'),
          cancelToken: any(named: 'cancelToken'),
        ),
      ).thenAnswer((inv) {
        final onRetry = inv.namedArguments[#onRetry] as FaydaRetryCallback?;
        onRetry?.call(2, 3);
        return gate.future;
      });
      await openFaydaStep(tester);

      await tester.tap(find.text('Verify with Fayda'));
      await settle(tester);
      expect(find.text('Still trying to reach Fayda (2/3)...'), findsOneWidget);

      gate.complete(
        const FaydaStartResult.failure(
          AuthErrorCode.faydaServiceUnavailable,
          'down',
        ),
      );
      await settle(tester);

      expect(
        find.text(
          "We couldn't reach Fayda, even after several tries. Check your "
          'internet connection and try again in a few minutes.',
        ),
        findsOneWidget,
      );
      expect(find.text('Retry'), findsOneWidget);
      expect(find.text('Continue'), findsNothing);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('AUTH_021 on register sends the user back to the Fayda step',
        (tester) async {
      await openFaydaStep(tester);
      await tester.tap(find.text('Verify with Fayda'));
      await settle(tester);
      links
          .add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);

      states.add(
        const AuthError(
          errorCode: AuthErrorCode.invalidFaydaToken,
          errorMessage: 'Fayda verification is required to register.',
        ),
      );
      await settle(tester);

      expect(
        find.text(
          'Your Fayda verification is missing or has expired. Please verify '
          'with Fayda again to finish creating your account.',
        ),
        findsOneWidget,
      );
      expect(find.text('Retry'), findsOneWidget);
      expect(find.text('Continue'), findsNothing);
      await tester.pumpWidget(const SizedBox.shrink());
    });
    testWidgets('Cancel during a retry stops it and ignores its late result',
        (tester) async {
      final gate = Completer<FaydaStartResult>();
      CancelToken? token;
      var launches = 0;
      when(
        () => fayda.startRegistration(
          phoneNumber: any(named: 'phoneNumber'),
          onRetry: any(named: 'onRetry'),
          cancelToken: any(named: 'cancelToken'),
        ),
      ).thenAnswer((inv) {
        token = inv.namedArguments[#cancelToken] as CancelToken?;
        (inv.namedArguments[#onRetry] as FaydaRetryCallback?)?.call(2, 3);
        return gate.future;
      });
      useTallPhone(tester);
      await tester.pumpWidget(
        BlocProvider<AuthBloc>.value(
          value: bloc,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: RegistrationFlow(
              faydaService: fayda,
              faydaLinks: links.stream,
              launcher: (_) async {
                launches++;
                return true;
              },
            ),
          ),
        ),
      );
      await settle(tester);
      await tester.tap(find.text('Farmer'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);
      await tester.tap(find.text('Verify with Fayda'));
      await settle(tester);
      expect(find.text('Still trying to reach Fayda (2/3)...'), findsOneWidget);

      await tester.tap(find.text('Cancel'));
      await settle(tester);

      expect(token?.isCancelled, isTrue);
      expect(find.text('Verify with Fayda'), findsOneWidget);

      // The abandoned start still answers: nothing may happen.
      gate.complete(
        FaydaStartResult(
          isSuccess: true,
          state: 'st-late',
          authorizeUrl: Uri.parse('https://esignet.test/authorize'),
        ),
      );
      await settle(tester);

      expect(launches, 0);
      expect(find.text('Verify with Fayda'), findsOneWidget);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('FAYDA_004 on register sends the user back to the Fayda step',
        (tester) async {
      await openFaydaStep(tester);
      await tester.tap(find.text('Verify with Fayda'));
      await settle(tester);
      links
          .add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);

      states.add(
        const AuthError(
          errorCode: AuthErrorCode.faydaAlreadyLinked,
          errorMessage: 'This Fayda ID is already linked to another account.',
        ),
      );
      await settle(tester);

      expect(find.text('Retry'), findsOneWidget);
      expect(find.text('Continue'), findsNothing);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('a trucker cannot continue without a driving license',
        (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(flow());
      await settle(tester);
      await tester.tap(find.text('Trucker'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);
      await tester.tap(find.text('Verify with Fayda'));
      await settle(tester);
      links
          .add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);

      expect(find.text('Driving License'), findsOneWidget);
      expect(
        find.text(
          'A driving license photo is required to register as a trucker.',
        ),
        findsOneWidget,
      );
      // The RegisterRoleDetails board puts Scan License at the foot; the
      // flow's Continue appears only once a photo exists.
      expect(find.text('Scan License'), findsOneWidget);
      expect(find.text('Continue'), findsNothing);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('a company buyer cannot continue without a company name',
        (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(flow());
      await settle(tester);
      await tester.tap(find.text('Buyer'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);
      await tester.tap(find.text('Verify with Fayda'));
      await settle(tester);
      links
          .add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);

      VoidCallback? continuePressed() => tester
          .widget<FilledButton>(
            find.ancestor(
              of: find.text('Continue'),
              matching: find.byType(FilledButton),
            ),
          )
          .onPressed;

      // Private buyer (the default) needs nothing more.
      expect(continuePressed(), isNotNull);

      await tester.tap(find.text('Company'));
      await settle(tester);
      expect(find.text('Enter your company name to continue.'), findsOneWidget);
      expect(continuePressed(), isNull);

      await tester.enterText(find.byType(TextField), '   ');
      await settle(tester);
      expect(continuePressed(), isNull);

      await tester.enterText(find.byType(TextField), 'Abebe Trading');
      await settle(tester);
      expect(find.text('Enter your company name to continue.'), findsNothing);
      expect(continuePressed(), isNotNull);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('a buyer is also sent back to the Fayda step on AUTH_021',
        (tester) async {
      useTallPhone(tester);
      await tester.pumpWidget(flow());
      await settle(tester);
      await tester.tap(find.text('Buyer'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);
      expect(find.text('Continue'), findsNothing);
      await tester.tap(find.text('Verify with Fayda'));
      await settle(tester);
      links
          .add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
      await settle(tester);
      // Buyer details, then personal info.
      await tester.tap(find.text('Continue'));
      await settle(tester);
      await tester.tap(find.text('Continue'));
      await settle(tester);

      states.add(
        const AuthError(
          errorCode: AuthErrorCode.invalidFaydaToken,
          errorMessage: 'Fayda verification is required to register.',
        ),
      );
      await settle(tester);

      expect(find.text('Retry'), findsOneWidget);
      expect(find.text('Continue'), findsNothing);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  });
}
