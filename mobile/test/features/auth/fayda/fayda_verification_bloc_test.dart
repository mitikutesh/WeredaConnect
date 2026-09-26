import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/features/auth/fayda/bloc/fayda_verification_bloc.dart';

class MockFaydaOidcService extends Mock implements FaydaOidcService {}

void main() {
  late MockFaydaOidcService service;
  late List<FaydaLinkResult> recorded;
  late FaydaVerificationBloc bloc;
  final realService = FaydaOidcService(dio: Dio());

  setUp(() {
    service = MockFaydaOidcService();
    // Callback parsing is pure: use the real implementation.
    when(() => service.parseCallback(any())).thenAnswer(
      (inv) => realService.parseCallback(inv.positionalArguments.first as Uri),
    );
    recorded = [];
    bloc = FaydaVerificationBloc(
      service: service,
      recordVerified: (r) async => recorded.add(r),
    );
  });

  setUpAll(() => registerFallbackValue(Uri()));

  tearDown(() => bloc.close());

  Future<FaydaVerificationState> settle(
    bool Function(FaydaVerificationState) done,
  ) =>
      bloc.stream.firstWhere(done).timeout(const Duration(seconds: 2));

  void stubStart() {
    when(() => service.startLink()).thenAnswer(
      (_) async => FaydaStartResult(
        isSuccess: true,
        state: 'st-1',
        authorizeUrl: Uri.parse('https://esignet.test/authorize'),
      ),
    );
  }

  test('start asks the server and waits for the callback', () async {
    stubStart();
    bloc.add(const FaydaVerificationStarted());

    final state = await settle((s) => s is FaydaAwaitingCallback);

    expect(
      state,
      FaydaAwaitingCallback(
        authorizeUrl: Uri.parse('https://esignet.test/authorize'),
        state: 'st-1',
      ),
    );
  });

  test('a matching callback links the account and records it', () async {
    stubStart();
    when(() => service.link(authCode: 'code-1', state: 'st-1')).thenAnswer(
      (_) async => FaydaLinkResult(
        isSuccess: true,
        faydaVerified: true,
        faydaVerifiedAt: DateTime.utc(2026, 9, 25),
        verifiedName: 'Abebe Kebede',
      ),
    );
    bloc.add(const FaydaVerificationStarted());
    await settle((s) => s is FaydaAwaitingCallback);

    bloc.add(
      FaydaCallbackReceived(
        Uri.parse('weredaconnect://fayda-callback?code=code-1&state=st-1'),
      ),
    );
    final state = await settle((s) => s is FaydaVerificationSucceeded);

    expect((state as FaydaVerificationSucceeded).verifiedName, 'Abebe Kebede');
    expect(recorded.single.faydaVerified, isTrue);
  });

  test('a callback with the wrong state is refused (CSRF)', () async {
    stubStart();
    bloc.add(const FaydaVerificationStarted());
    await settle((s) => s is FaydaAwaitingCallback);

    bloc.add(
      FaydaCallbackReceived(
        Uri.parse('weredaconnect://fayda-callback?code=c&state=forged'),
      ),
    );
    final state = await settle((s) => s is FaydaVerificationFailure);

    expect(
      (state as FaydaVerificationFailure).errorCode,
      AuthErrorCode.faydaTokenExpired,
    );
    verifyNever(
      () => service.link(
        authCode: any(named: 'authCode'),
        state: any(named: 'state'),
      ),
    );
    expect(recorded, isEmpty);
  });

  test('a callback nobody started is ignored', () async {
    bloc.add(
      FaydaCallbackReceived(
        Uri.parse('weredaconnect://fayda-callback?code=c&state=s'),
      ),
    );
    await Future<void>.delayed(const Duration(milliseconds: 20));
    expect(bloc.state, const FaydaVerificationIdle());
  });

  test('FAYDA_004 from link is surfaced', () async {
    stubStart();
    when(
      () => service.link(
        authCode: any(named: 'authCode'),
        state: any(named: 'state'),
      ),
    ).thenAnswer(
      (_) async => const FaydaLinkResult(
        isSuccess: false,
        errorCode: AuthErrorCode.faydaAlreadyLinked,
      ),
    );
    bloc.add(const FaydaVerificationStarted());
    await settle((s) => s is FaydaAwaitingCallback);

    bloc.add(
      FaydaCallbackReceived(
        Uri.parse('weredaconnect://fayda-callback?code=c&state=st-1'),
      ),
    );
    final state = await settle((s) => s is FaydaVerificationFailure);

    expect(
      (state as FaydaVerificationFailure).errorCode,
      AuthErrorCode.faydaAlreadyLinked,
    );
  });

  test('a start failure (service down) is reported', () async {
    when(() => service.startLink()).thenAnswer(
      (_) async => const FaydaStartResult.failure(
        AuthErrorCode.faydaServiceUnavailable,
        'down',
      ),
    );
    bloc.add(const FaydaVerificationStarted());

    final state = await settle((s) => s is FaydaVerificationFailure);

    expect(
      (state as FaydaVerificationFailure).errorCode,
      AuthErrorCode.faydaServiceUnavailable,
    );
  });

  test('coming back without a callback keeps waiting for a late one',
      () async {
    stubStart();
    when(() => service.link(authCode: 'code-1', state: 'st-1')).thenAnswer(
      (_) async => const FaydaLinkResult(isSuccess: true, faydaVerified: true),
    );
    bloc.add(const FaydaVerificationStarted());
    await settle((s) => s is FaydaAwaitingCallback);

    bloc.add(const FaydaBrowserReturnedWithoutCallback());
    final waiting = await settle(
      (s) => s is FaydaAwaitingCallback && s.browserReturned,
    );
    expect((waiting as FaydaAwaitingCallback).state, 'st-1');

    bloc.add(
      FaydaCallbackReceived(
        Uri.parse('weredaconnect://fayda-callback?code=code-1&state=st-1'),
      ),
    );
    final done = await settle((s) => s is FaydaVerificationSucceeded);
    expect(done, isA<FaydaVerificationSucceeded>());
  });

  test('the state expiring without a callback fails as expired', () async {
    stubStart();
    bloc.add(const FaydaVerificationStarted());
    await settle((s) => s is FaydaAwaitingCallback);

    bloc.add(const FaydaCallbackTimedOut());
    final state = await settle((s) => s is FaydaVerificationFailure);

    expect(
      (state as FaydaVerificationFailure).errorCode,
      AuthErrorCode.faydaTokenExpired,
    );
  });
}
