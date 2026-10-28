import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/otp_verification_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

void main() {
  late MockAuthBloc bloc;
  late StreamController<AuthState> states;

  setUpAll(() => registerFallbackValue(FakeAuthEvent()));

  setUp(() {
    bloc = MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    when(() => bloc.state).thenReturn(
      const AuthOtpVerificationRequired(phoneNumber: '+251911234567'),
    );
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    when(() => bloc.isClosed).thenReturn(false);
  });

  tearDown(() => states.close());

  Widget app({
    Duration cooldown = const Duration(seconds: 60),
    DateTime? expiresAt,
    Locale locale = const Locale('en'),
  }) =>
      BlocProvider<AuthBloc>.value(
        value: bloc,
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: OtpVerificationScreen(
            phoneNumber: '+251911234567',
            resendCooldown: cooldown,
            expiresAt: expiresAt,
          ),
        ),
      );

  testWidgets('shows the phone the code went to', (tester) async {
    await tester.pumpWidget(app());
    await tester.pump();

    expect(
      find.text('We sent a 6-digit code by SMS to +251911234567.'),
      findsOneWidget,
    );
  });

  // LT-03: the expiry on the viewer's clock, in Ethiopia time. 10:05 UTC is
  // 13:05 in Ethiopia, 7:05 in the day on the Ethiopian clock.
  testWidgets('English shows the expiry on the 24-hour clock', (tester) async {
    await tester.pumpWidget(app(expiresAt: DateTime.utc(2026, 9, 25, 10, 5)));
    await tester.pumpAndSettle();

    expect(find.text('The code expires at 13:05.'), findsOneWidget);
  });

  testWidgets('Amharic shows the expiry on the Ethiopian clock, 24-hour small',
      (tester) async {
    await tester.pumpWidget(
      app(
        expiresAt: DateTime.utc(2026, 9, 25, 10, 5),
        locale: const Locale('am'),
      ),
    );
    await tester.pumpAndSettle();

    final am = lookupAppLocalizations(const Locale('am'));
    expect(
      find.text(
        am.otpExpiresAt('ከቀኑ 7:05').replaceFirst('7:05', '7:05 (13:05)'),
      ),
      findsOneWidget,
    );
  });

  testWidgets('auto-submits once 6 digits are entered', (tester) async {
    await tester.pumpWidget(app());
    await tester.pump();

    await tester.enterText(find.byKey(const Key('otp_code_field')), '12345');
    await tester.pump();
    verifyNever(() => bloc.add(any()));

    await tester.enterText(find.byKey(const Key('otp_code_field')), '123456');
    await tester.pump();

    verify(
      () => bloc.add(
        const AuthVerifyOtpRequested(
          otpCode: '123456',
          phoneNumber: '+251911234567',
        ),
      ),
    ).called(1);
  });

  testWidgets('ignores non-digits', (tester) async {
    await tester.pumpWidget(app());
    await tester.pump();

    await tester.enterText(find.byKey(const Key('otp_code_field')), '12ab34');
    await tester.pump();

    final field =
        tester.widget<TextField>(find.byKey(const Key('otp_code_field')));
    expect(field.controller!.text, '1234');
  });

  testWidgets('resend is locked during the countdown, then sends',
      (tester) async {
    await tester.pumpWidget(app(cooldown: const Duration(seconds: 2)));
    await tester.pump();

    expect(find.text('Resend in 2s'), findsOneWidget);
    final locked = tester.widget<TextButton>(
      find.byKey(const Key('otp_resend_button')),
    );
    expect(locked.onPressed, isNull);

    await tester.pump(const Duration(seconds: 2));
    await tester.tap(find.byKey(const Key('otp_resend_button')));
    await tester.pump();

    verify(
      () => bloc.add(
        const AuthResendOtpRequested(phoneNumber: '+251911234567'),
      ),
    ).called(1);
  });

  testWidgets('a wrong code shows the localized error and clears the field',
      (tester) async {
    await tester.pumpWidget(app());
    await tester.pump();
    await tester.enterText(find.byKey(const Key('otp_code_field')), '000000');
    await tester.pump();

    states.add(
      const AuthError(
        errorCode: AuthErrorCode.otpInvalidOrExpired,
        errorMessage: 'bad',
      ),
    );
    await tester.pump();

    expect(
      find.text(
        'That code is wrong or has expired. Check the SMS or request a new '
        'code.',
      ),
      findsOneWidget,
    );
    final field =
        tester.widget<TextField>(find.byKey(const Key('otp_code_field')));
    expect(field.controller!.text, isEmpty);
  });

  testWidgets('a rate limit restarts the countdown from retryAfterSeconds',
      (tester) async {
    await tester.pumpWidget(app(cooldown: Duration.zero));
    await tester.pump();

    states.add(
      const AuthError(
        errorCode: AuthErrorCode.rateLimitExceeded,
        errorMessage: 'slow down',
        retryAfterSeconds: 30,
      ),
    );
    await tester.pump();

    expect(find.text('Resend in 30s'), findsOneWidget);
    expect(
      find.text('Too many attempts. Try again in 30 seconds.'),
      findsOneWidget,
    );
  });
}
