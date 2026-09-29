import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/features/auth/fayda/screens/fayda_verification_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockFaydaOidcService extends Mock implements FaydaOidcService {}

void main() {
  late MockFaydaOidcService service;
  late StreamController<Uri> links;
  late List<Uri> launched;
  late List<FaydaLinkResult> recorded;
  final realService = FaydaOidcService(dio: Dio());

  setUpAll(() => registerFallbackValue(Uri()));

  setUp(() {
    AppLockService.resetExternalFlows();
    service = MockFaydaOidcService();
    links = StreamController<Uri>.broadcast();
    launched = [];
    recorded = [];
    when(() => service.parseCallback(any())).thenAnswer(
      (inv) => realService.parseCallback(inv.positionalArguments.first as Uri),
    );
    when(() => service.startLink()).thenAnswer(
      (_) async => FaydaStartResult(
        isSuccess: true,
        state: 'st-1',
        authorizeUrl: Uri.parse('https://esignet.test/authorize?x=1'),
      ),
    );
    when(
      () => service.link(
        authCode: any(named: 'authCode'),
        state: any(named: 'state'),
      ),
    ).thenAnswer(
      (_) async => FaydaLinkResult(
        isSuccess: true,
        faydaVerified: true,
        faydaVerifiedAt: DateTime.utc(2026, 9, 26),
        verifiedName: 'Abebe Kebede',
      ),
    );
  });

  tearDown(() {
    unawaited(links.close());
    AppLockService.resetExternalFlows();
  });

  Widget app() => MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: FaydaVerificationScreen(
          service: service,
          deepLinks: links.stream,
          launcher: (url) async {
            launched.add(url);
            return true;
          },
          recordVerified: (r) async => recorded.add(r),
        ),
      );

  Future<void> startFlow(WidgetTester tester) async {
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();
    await tester.tap(find.text('Verify with Fayda'));
    await tester.pump();
    await tester.pump();
  }

  testWidgets('opens the server authorize URL and links on the callback',
      (tester) async {
    await startFlow(tester);

    expect(launched, [Uri.parse('https://esignet.test/authorize?x=1')]);
    // The app lock must not fire on the way back from the browser.
    expect(AppLockService.externalFlowActive, isTrue);

    links.add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
    await tester.pumpAndSettle();

    verify(() => service.link(authCode: 'c-1', state: 'st-1')).called(1);
    expect(recorded.single.faydaVerified, isTrue);
    expect(AppLockService.externalFlowActive, isFalse);
    expect(
      find.text('Your Fayda ID is now linked to your account.'),
      findsOneWidget,
    );
  });

  testWidgets('a callback with the wrong state is refused', (tester) async {
    await startFlow(tester);

    links.add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=evil'));
    await tester.pumpAndSettle();

    verifyNever(
      () => service.link(
        authCode: any(named: 'authCode'),
        state: any(named: 'state'),
      ),
    );
    expect(recorded, isEmpty);
    expect(
      find.text('The Fayda session expired. Please start verification again.'),
      findsOneWidget,
    );
  });

  testWidgets('a deep link arriving after the return timer still links',
      (tester) async {
    await startFlow(tester);

    // Back in the app, no deep link yet.
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pump(const Duration(seconds: 3));
    await tester.pump();

    expect(
      find.text(
        'Still waiting for Fayda. Finish in the browser and you will be '
        'brought back here.',
      ),
      findsOneWidget,
    );
    expect(find.text('Fayda verification failed'), findsNothing);
    // Still inside the external flow: going back to the browser never locks.
    expect(AppLockService.externalFlowActive, isTrue);
    // The browser is not reopened for the "still waiting" update.
    expect(launched, hasLength(1));

    links.add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
    await tester.pumpAndSettle();

    verify(() => service.link(authCode: 'c-1', state: 'st-1')).called(1);
    expect(AppLockService.externalFlowActive, isFalse);
  });

  testWidgets('the wait fails only once the Fayda state expires',
      (tester) async {
    when(() => service.startLink()).thenAnswer(
      (_) async => FaydaStartResult(
        isSuccess: true,
        state: 'st-1',
        authorizeUrl: Uri.parse('https://esignet.test/authorize?x=1'),
        expiresAt: DateTime.now().toUtc().add(const Duration(minutes: 1)),
      ),
    );
    await startFlow(tester);

    await tester.pump(const Duration(seconds: 30));
    expect(
      find.text('The Fayda session expired. Please start verification again.'),
      findsNothing,
    );

    await tester.pump(const Duration(seconds: 31));
    await tester.pump();
    expect(
      find.text('The Fayda session expired. Please start verification again.'),
      findsOneWidget,
    );
    expect(AppLockService.externalFlowActive, isFalse);
  });

  testWidgets('a stateless callback does not stop the expiry timer',
      (tester) async {
    when(() => service.startLink()).thenAnswer(
      (_) async => FaydaStartResult(
        isSuccess: true,
        state: 'st-1',
        authorizeUrl: Uri.parse('https://esignet.test/authorize?x=1'),
        expiresAt: DateTime.now().toUtc().add(const Duration(minutes: 1)),
      ),
    );
    await startFlow(tester);

    links.add(Uri.parse('weredaconnect://fayda-callback'));
    await tester.pump();
    // Still inside the external flow: the stray link ended nothing.
    expect(AppLockService.externalFlowActive, isTrue);

    await tester.pump(const Duration(seconds: 61));
    await tester.pump();
    expect(
      find.text('The Fayda session expired. Please start verification again.'),
      findsOneWidget,
    );
    expect(AppLockService.externalFlowActive, isFalse);
  });

  testWidgets('leaving the screen ends the external flow', (tester) async {
    await startFlow(tester);
    expect(AppLockService.externalFlowActive, isTrue);

    await tester.pumpWidget(const SizedBox());
    expect(AppLockService.externalFlowActive, isFalse);
  });

  // SIM-45: web runs the real link flow instead of a mobile-only dead end.
  testWidgets('a start refused for the web redirect says to use the app',
      (tester) async {
    when(() => service.startLink()).thenAnswer(
      (_) async => const FaydaStartResult.failure(
        AuthErrorCode.faydaUnavailableOnWeb,
        'Fayda verification is not available on the web',
      ),
    );
    await startFlow(tester);
    await tester.pumpAndSettle();

    expect(launched, isEmpty);
    expect(
      find.text(
        "Fayda verification isn't available in the browser yet. Please use "
        'the WeredaConnect mobile app.',
      ),
      findsOneWidget,
    );
  });

  // ===== payment mode (opened by the KYC payment prompt) =====

  /// Pushes the screen in payment mode from a host page and records what it
  /// popped with.
  Future<List<bool?>> startPaymentFlow(
    WidgetTester tester, {
    bool requirePhoneProof = false,
  }) async {
    final popped = <bool?>[];
    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () async {
                popped.add(
                  await Navigator.of(context).push<bool>(
                    MaterialPageRoute<bool>(
                      builder: (_) => FaydaVerificationScreen(
                        service: service,
                        deepLinks: links.stream,
                        launcher: (url) async => true,
                        recordVerified: (r) async => recorded.add(r),
                        forPayment: true,
                        requirePhoneProof: requirePhoneProof,
                      ),
                    ),
                  ),
                );
              },
              child: const Text('host'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('host'));
    await tester.pumpAndSettle();
    expect(
      find.textContaining('Use the Fayda ID that belongs to this account'),
      findsOneWidget,
    );
    await tester.tap(find.text('Verify with Fayda'));
    await tester.pump();
    await tester.pump();
    return popped;
  }

  testWidgets('payment mode: verified as the owner closes itself with true',
      (tester) async {
    final popped = await startPaymentFlow(tester);

    links.add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
    await tester.pumpAndSettle();

    expect(popped, [true]);
    expect(find.byType(FaydaVerificationScreen), findsNothing);
    expect(
      find.text('Identity verified. Continuing your payment...'),
      findsOneWidget,
    );
  });

  for (final (code, text) in [
    (AuthErrorCode.faydaIdMismatch, 'does not match the one on your account'),
    (AuthErrorCode.faydaPhoneMismatch, 'phone number on this Fayda ID'),
    (AuthErrorCode.faydaIdentityUnconfirmed, 'could not confirm this Fayda ID'),
  ]) {
    testWidgets('payment mode: $code keeps the screen open, pops false on back',
        (tester) async {
      when(
        () => service.link(
          authCode: any(named: 'authCode'),
          state: any(named: 'state'),
        ),
      ).thenAnswer(
        (_) async => FaydaLinkResult(isSuccess: false, errorCode: code),
      );
      final popped = await startPaymentFlow(tester);

      links.add(
        Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'),
      );
      await tester.pumpAndSettle();

      expect(popped, isEmpty);
      expect(find.textContaining(text), findsOneWidget);
      expect(recorded, isEmpty);

      await tester.pageBack();
      await tester.pumpAndSettle();
      expect(popped, [false]);
    });
  }

  void linkReturns({required bool phoneVerified}) => when(
        () => service.link(
          authCode: any(named: 'authCode'),
          state: any(named: 'state'),
        ),
      ).thenAnswer(
        (_) async => FaydaLinkResult(
          isSuccess: true,
          faydaVerified: true,
          phoneVerified: phoneVerified,
        ),
      );

  testWidgets(
      'phone reason: linked but phone still unproven stays open, pops false',
      (tester) async {
    linkReturns(phoneVerified: false);
    final popped = await startPaymentFlow(tester, requirePhoneProof: true);

    links.add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
    await tester.pumpAndSettle();

    expect(popped, isEmpty);
    expect(
      find.textContaining("does not list this account's phone number"),
      findsOneWidget,
    );
    expect(find.text('Identity Verified'), findsNothing);

    await tester.pageBack();
    await tester.pumpAndSettle();
    expect(popped, [false]);
  });

  testWidgets('phone reason: link that proves the phone closes with true',
      (tester) async {
    linkReturns(phoneVerified: true);
    final popped = await startPaymentFlow(tester, requirePhoneProof: true);

    links.add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
    await tester.pumpAndSettle();

    expect(popped, [true]);
  });

  testWidgets('Fayda reason: an unproven phone does not matter',
      (tester) async {
    linkReturns(phoneVerified: false);
    final popped = await startPaymentFlow(tester);

    links.add(Uri.parse('weredaconnect://fayda-callback?code=c-1&state=st-1'));
    await tester.pumpAndSettle();

    expect(popped, [true]);
  });
}
