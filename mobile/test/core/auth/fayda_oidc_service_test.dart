import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';

import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/session_signals.dart';

const noDelay = [Duration.zero, Duration.zero];

/// A Dio whose [n]th request (1-based) is answered by [answer].
({Dio dio, List<int> calls}) scripted(
  void Function(int n, RequestOptions o, RequestInterceptorHandler h) answer,
) {
  final dio = Dio(BaseOptions(baseUrl: 'https://api.test'));
  final calls = <int>[];
  dio.interceptors.add(
    InterceptorsWrapper(
      onRequest: (o, h) {
        calls.add(calls.length + 1);
        answer(calls.length, o, h);
      },
    ),
  );
  return (dio: dio, calls: calls);
}

DioException offline(RequestOptions o) =>
    DioException(requestOptions: o, type: DioExceptionType.connectionError);

Response<dynamic> ok(RequestOptions o, Map<String, dynamic> body) =>
    Response<dynamic>(requestOptions: o, statusCode: 200, data: body);

void main() {
  late Dio dio;
  late DioAdapter adapter;
  late FaydaOidcService service;

  setUp(() {
    dio = Dio(BaseOptions(baseUrl: 'https://api.test'));
    adapter = DioAdapter(dio: dio);
    // No pauses between automatic retries in tests.
    service = FaydaOidcService(dio: dio, retryDelays: noDelay);
  });

  group('startRegistration', () {
    test('returns the server-built authorize URL and state', () async {
      adapter.onPost(
        '/auth/fayda-start',
        (server) => server.reply(200, {
          'state': 'st-1',
          'authorizeUrl': 'https://esignet.test/authorize?state=st-1',
          'expiresAt': '2026-09-25T10:10:00Z',
        }),
        data: {
          'phoneNumber': '+251911234567',
          'redirectUri': 'weredaconnect://fayda-callback',
        },
      );

      final result =
          await service.startRegistration(phoneNumber: '+251911234567');

      expect(result.isSuccess, isTrue);
      expect(result.state, 'st-1');
      expect(
        result.authorizeUrl.toString(),
        'https://esignet.test/authorize?state=st-1',
      );
      expect(result.expiresAt, DateTime.utc(2026, 9, 25, 10, 10));
    });

    test('omits the phone when it is not known yet', () async {
      adapter.onPost(
        '/auth/fayda-start',
        (server) => server.reply(200, {
          'state': 'st-2',
          'authorizeUrl': 'https://esignet.test/authorize',
        }),
        data: {'redirectUri': 'weredaconnect://fayda-callback'},
      );

      final result = await service.startRegistration();

      expect(result.isSuccess, isTrue);
    });

    test('FAYDA_003 is reported as service unavailable', () async {
      adapter.onPost(
        '/auth/fayda-start',
        (server) => server.reply(503, {
          'error': {'code': 'FAYDA_003', 'message': 'Fayda down'},
        }),
        data: Matchers.any,
      );

      final result = await service.startRegistration();

      expect(result.isSuccess, isFalse);
      expect(result.errorCode, AuthErrorCode.faydaServiceUnavailable);
    });

    test('429 AUTH_006 keeps retryAfterSeconds', () async {
      adapter.onPost(
        '/auth/fayda-start',
        (server) => server.reply(429, {
          'error': {
            'code': 'AUTH_006',
            'message': 'slow down',
            'retryAfterSeconds': 30,
          },
        }),
        data: Matchers.any,
      );

      final result = await service.startRegistration();

      expect(result.errorCode, AuthErrorCode.rateLimitExceeded);
      expect(result.retryAfterSeconds, 30);
    });

    test('a refused redirect off the web keeps its own error', () async {
      adapter.onPost(
        '/auth/fayda-start',
        (server) => server.reply(400, {
          'error': {
            'code': 'AUTH_007',
            'message': 'redirectUri is not allowed.',
            'field': 'redirectUri',
          },
        }),
        data: Matchers.any,
      );

      final result = await service.startRegistration();

      expect(result.errorCode, AuthErrorCode.internalServerError);
    });

    test('on web a refused redirect says to use the mobile app', () async {
      final web = FaydaOidcService(
        dio: dio,
        retryDelays: noDelay,
        webRedirect: () =>
            'https://dev-app.weredaconnect.com/fayda-callback.html',
      );
      adapter.onPost(
        '/auth/fayda-start',
        (server) => server.reply(400, {
          'error': {
            'code': 'AUTH_007',
            'message': 'redirectUri is not allowed.',
            'field': 'redirectUri',
          },
        }),
        data: {
          'redirectUri':
              'https://dev-app.weredaconnect.com/fayda-callback.html',
        },
      );

      final result = await web.startRegistration();

      expect(result.errorCode, AuthErrorCode.faydaUnavailableOnWeb);
    });

    test('on web other AUTH_007 errors keep their code', () async {
      final web = FaydaOidcService(
        dio: dio,
        retryDelays: noDelay,
        webRedirect: () =>
            'https://dev-app.weredaconnect.com/fayda-callback.html',
      );
      adapter.onPost(
        '/auth/fayda-start',
        (server) => server.reply(400, {
          'error': {'code': 'AUTH_007', 'message': 'Invalid JSON'},
        }),
        data: Matchers.any,
      );

      final result = await web.startRegistration();

      expect(result.errorCode, AuthErrorCode.internalServerError);
    });

    test('retries a dropped connection, then succeeds', () async {
      final s = scripted(
        (n, o, h) => n < 3
            ? h.reject(offline(o))
            : h.resolve(
                ok(o, {
                  'state': 'st-9',
                  'authorizeUrl': 'https://esignet.test/authorize',
                }),
              ),
      );
      final retries = <(int, int)>[];

      final result = await FaydaOidcService(dio: s.dio, retryDelays: noDelay)
          .startRegistration(onRetry: (a, t) => retries.add((a, t)));

      expect(result.isSuccess, isTrue);
      expect(result.state, 'st-9');
      expect(s.calls, hasLength(3));
      expect(retries, [(2, 3), (3, 3)]);
    });

    test('gives up after three attempts when Fayda stays down', () async {
      final s = scripted((n, o, h) => h.reject(offline(o)));

      final result = await FaydaOidcService(dio: s.dio, retryDelays: noDelay)
          .startRegistration();

      expect(result.errorCode, AuthErrorCode.faydaServiceUnavailable);
      expect(s.calls, hasLength(3));
    });

    test('does not retry a rate limit or a refusal', () async {
      final s = scripted(
        (n, o, h) => h.reject(
          DioException(
            requestOptions: o,
            type: DioExceptionType.badResponse,
            response: Response<dynamic>(
              requestOptions: o,
              statusCode: 429,
              data: {
                'error': {'code': 'AUTH_006', 'message': 'slow down'},
              },
            ),
          ),
        ),
      );

      final result = await FaydaOidcService(dio: s.dio, retryDelays: noDelay)
          .startRegistration();

      expect(result.errorCode, AuthErrorCode.rateLimitExceeded);
      expect(s.calls, hasLength(1));
    });

    test('cancelling stops further retries', () async {
      final s = scripted((n, o, h) => h.reject(offline(o)));
      final cancel = CancelToken();

      await FaydaOidcService(dio: s.dio, retryDelays: noDelay)
          .startRegistration(
        cancelToken: cancel,
        onRetry: (_, __) => cancel.cancel(),
      );

      expect(s.calls, hasLength(1));
    });

    test('a malformed response is treated as unavailable', () async {
      adapter.onPost(
        '/auth/fayda-start',
        (server) => server.reply(200, {'state': 'st-3'}),
        data: Matchers.any,
      );

      final result = await service.startRegistration();

      expect(result.isSuccess, isFalse);
      expect(result.errorCode, AuthErrorCode.faydaServiceUnavailable);
    });
  });

  test('startLink calls the authenticated endpoint', () async {
    adapter.onPost(
      '/user/fayda-start',
      (server) => server.reply(200, {
        'state': 'link-1',
        'authorizeUrl': 'https://esignet.test/authorize',
      }),
      data: {'redirectUri': 'weredaconnect://fayda-callback'},
    );

    final result = await service.startLink();

    expect(result.isSuccess, isTrue);
    expect(result.state, 'link-1');
  });

  group('exchangeCode', () {
    test('sends code, state and the deep-link redirect', () async {
      adapter.onPost(
        '/auth/fayda-verify',
        (server) => server.reply(200, {
          'verified': true,
          'verifiedName': 'Abebe Kebede',
          'verificationToken': 'vt-1',
        }),
        data: {
          'authorizationCode': 'code-1',
          'state': 'st-1',
          'redirectUri': FaydaOidcService.redirectUri,
        },
      );

      final result =
          await service.exchangeCode(authCode: 'code-1', state: 'st-1');

      expect(result.isSuccess, isTrue);
      expect(result.verificationToken, 'vt-1');
      expect(result.verifiedName, 'Abebe Kebede');
    });

    test('maps FAYDA_002 (state invalid or expired)', () async {
      adapter.onPost(
        '/auth/fayda-verify',
        (server) => server.reply(400, {
          'error': {'code': 'FAYDA_002', 'message': 'state expired'},
        }),
        data: Matchers.any,
      );

      final result =
          await service.exchangeCode(authCode: 'code-1', state: 'old');

      expect(result.errorCode, AuthErrorCode.faydaTokenExpired);
    });

    test('retries only when the request never left the device', () async {
      final s = scripted(
        (n, o, h) => n == 1
            ? h.reject(offline(o))
            : h.resolve(ok(o, {'verified': true, 'verificationToken': 'vt'})),
      );

      final result = await FaydaOidcService(dio: s.dio, retryDelays: noDelay)
          .exchangeCode(authCode: 'c', state: 'st');

      expect(result.isSuccess, isTrue);
      expect(s.calls, hasLength(2));
    });

    test('on web a lost connection is not retried', () async {
      // The browser reports a response lost after the server used the code
      // exactly like a refused connection.
      final s = scripted((n, o, h) => h.reject(offline(o)));

      final result = await FaydaOidcService(
        dio: s.dio,
        retryDelays: noDelay,
        webRedirect: () =>
            'https://dev-app.weredaconnect.com/fayda-callback.html',
      ).exchangeCode(authCode: 'c', state: 'st');

      expect(result.errorCode, AuthErrorCode.faydaServiceUnavailable);
      expect(s.calls, hasLength(1));
    });

    test('never repeats a code the server may have used', () async {
      final s = scripted(
        (n, o, h) => h.reject(
          DioException(
            requestOptions: o,
            type: DioExceptionType.receiveTimeout,
          ),
        ),
      );

      final result = await FaydaOidcService(dio: s.dio, retryDelays: noDelay)
          .exchangeCode(authCode: 'c', state: 'st');

      expect(result.errorCode, AuthErrorCode.faydaServiceUnavailable);
      expect(s.calls, hasLength(1));
    });
  });

  group('link', () {
    test('returns the verified status on success', () async {
      adapter.onPost(
        '/user/fayda-link',
        (server) => server.reply(200, {
          'faydaVerified': true,
          'faydaVerifiedAt': '2026-09-25T09:00:00Z',
          'verifiedName': 'Abebe Kebede',
          'phoneVerified': true,
        }),
        data: {
          'authorizationCode': 'code-9',
          'state': 'link-1',
          'redirectUri': FaydaOidcService.redirectUri,
        },
      );

      final result = await service.link(authCode: 'code-9', state: 'link-1');

      expect(result.isSuccess, isTrue);
      expect(result.faydaVerified, isTrue);
      expect(result.faydaVerifiedAt, DateTime.utc(2026, 9, 25, 9));
      expect(result.verifiedName, 'Abebe Kebede');
      expect(result.phoneVerified, isTrue);
    });

    test('maps FAYDA_004 (already linked to another account)', () async {
      adapter.onPost(
        '/user/fayda-link',
        (server) => server.reply(409, {
          'error': {'code': 'FAYDA_004', 'message': 'already linked'},
        }),
        data: Matchers.any,
      );

      final result = await service.link(authCode: 'c', state: 's');

      expect(result.isSuccess, isFalse);
      expect(result.errorCode, AuthErrorCode.faydaAlreadyLinked);
    });
  });

  // ACC-03: these calls skip the API client, so they report a deleted
  // account themselves (account-deletion.md 1.3.0).
  group('410 ACCOUNT_004', () {
    Map<String, dynamic> gone(String state) => {
          'error': {
            'code': 'ACCOUNT_004',
            'message': 'This account has been deleted.',
            'deletionState': state,
          },
        };

    test('from /user/fayda-start and /user/fayda-link tells the AuthBloc',
        () async {
      final signalled = <String>[];
      final sub = SessionSignals.instance.onAccountGone.listen(signalled.add);
      addTearDown(sub.cancel);
      adapter
        ..onPost(
          '/user/fayda-start',
          (server) => server.reply(410, gone('completed')),
          data: Matchers.any,
        )
        ..onPost(
          '/user/fayda-link',
          (server) => server.reply(410, gone('in_progress')),
          data: Matchers.any,
        );

      final start = await service.startLink();
      final link = await service.link(authCode: 'c', state: 's');
      await Future<void>.delayed(Duration.zero);

      expect(start.isSuccess, isFalse);
      expect(link.isSuccess, isFalse);
      expect(signalled, ['completed', 'in_progress']);
    });

    test('other Fayda refusals tell nothing', () async {
      final signalled = <String>[];
      final sub = SessionSignals.instance.onAccountGone.listen(signalled.add);
      addTearDown(sub.cancel);
      adapter.onPost(
        '/user/fayda-link',
        (server) => server.reply(409, {
          'error': {'code': 'FAYDA_004', 'message': 'already linked'},
        }),
        data: Matchers.any,
      );

      await service.link(authCode: 'c', state: 's');
      await Future<void>.delayed(Duration.zero);

      expect(signalled, isEmpty);
    });
  });

  test('isCallbackUri only accepts the Fayda deep link', () {
    expect(
      FaydaOidcService.isCallbackUri(
        Uri.parse('weredaconnect://fayda-callback?code=a&state=b'),
      ),
      isTrue,
    );
    expect(
      FaydaOidcService.isCallbackUri(Uri.parse('weredaconnect://payment?x=1')),
      isFalse,
    );
    expect(
      FaydaOidcService.isCallbackUri(Uri.parse('https://fayda-callback/')),
      isFalse,
    );
  });

  test('on web verify sends the web callback page', () async {
    const page = 'https://dev-app.weredaconnect.com/fayda-callback.html';
    final web = FaydaOidcService(dio: dio, webRedirect: () => page);
    adapter.onPost(
      '/auth/fayda-verify',
      (server) => server.reply(200, {'verificationToken': 'vt-1'}),
      data: {'authorizationCode': 'c', 'state': 's', 'redirectUri': page},
    );

    final result = await web.exchangeCode(authCode: 'c', state: 's');

    expect(result.isSuccess, isTrue);
  });

  test('isCallbackResponse needs a state', () {
    expect(
      FaydaOidcService.isCallbackResponse(
        Uri.parse('weredaconnect://fayda-callback?code=a&state=b'),
      ),
      isTrue,
    );
    expect(
      FaydaOidcService.isCallbackResponse(
        Uri.parse('weredaconnect://fayda-callback?code=a'),
      ),
      isFalse,
    );
    expect(
      FaydaOidcService.isCallbackResponse(
        Uri.parse('weredaconnect://fayda-callback?state='),
      ),
      isFalse,
    );
    expect(
      FaydaOidcService.isCallbackResponse(
        Uri.parse('https://dev-app.weredaconnect.com/fayda-callback.html'),
        webRedirect: 'https://dev-app.weredaconnect.com/fayda-callback.html',
      ),
      isFalse,
    );
  });

  test('redirectUri is the deep link off the web', () {
    expect(FaydaOidcService.redirectUri, 'weredaconnect://fayda-callback');
  });

  test('isCallbackUri accepts only the exact web callback page', () {
    const web = 'https://dev-app.weredaconnect.com/fayda-callback.html';
    bool accepts(String uri) =>
        FaydaOidcService.isCallbackUri(Uri.parse(uri), webRedirect: web);

    expect(accepts('$web?code=a&state=b'), isTrue);
    expect(accepts('weredaconnect://fayda-callback?code=a'), isTrue);
    expect(
      accepts('https://evil.example/fayda-callback.html?code=a&state=b'),
      isFalse,
    );
    expect(
      accepts('http://dev-app.weredaconnect.com/fayda-callback.html?code=a'),
      isFalse,
    );
    expect(
      accepts('https://dev-app.weredaconnect.com/other.html?code=a'),
      isFalse,
    );
  });
}
