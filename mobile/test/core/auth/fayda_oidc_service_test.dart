import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';

import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';

void main() {
  late Dio dio;
  late DioAdapter adapter;
  late FaydaOidcService service;

  setUp(() {
    dio = Dio(BaseOptions(baseUrl: 'https://api.test'));
    adapter = DioAdapter(dio: dio);
    service = FaydaOidcService(dio: dio);
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
        data: {'phoneNumber': '+251911234567'},
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
        data: <String, dynamic>{},
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
      data: <String, dynamic>{},
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
  });

  group('link', () {
    test('returns the verified status on success', () async {
      adapter.onPost(
        '/user/fayda-link',
        (server) => server.reply(200, {
          'faydaVerified': true,
          'faydaVerifiedAt': '2026-09-25T09:00:00Z',
          'verifiedName': 'Abebe Kebede',
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
}
