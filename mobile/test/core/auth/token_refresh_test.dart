import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/session_signals.dart';
import 'package:wereda_connect/core/auth/token_refresh.dart';

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

void main() {
  late Dio dio;
  late DioAdapter adapter;
  late MockSecureStorage storage;
  late SessionSignals signals;
  late List<(String, AuthTokens)> cachedWrites;
  late Future<String?> Function(String) callback;

  setUp(() {
    dio = Dio(BaseOptions(baseUrl: 'https://api.test'));
    adapter = DioAdapter(dio: dio);
    storage = MockSecureStorage();
    when(
      () => storage.write(
        key: any(named: 'key'),
        value: any(named: 'value'),
      ),
    ).thenAnswer((_) async {});
    when(() => storage.read(key: 'current_user_phone'))
        .thenAnswer((_) async => '+251911234567');
    signals = SessionSignals.forTesting();
    cachedWrites = [];
    callback = buildRefreshTokenCallback(
      refreshDio: dio,
      storage: storage,
      signals: signals,
      writeCachedTokens: (phone, tokens) async {
        cachedWrites.add((phone, tokens));
      },
    );
  });

  test('persists the active slots and refreshes the offline cache (48h clock)',
      () async {
    adapter.onPost(
      '/auth/refresh',
      (server) => server.reply(200, {
        'tokens': {
          'accessToken': 'new-access',
          'idToken': 'new-id',
          'expiresIn': 900,
        },
      }),
      data: {'refreshToken': 'old-refresh'},
    );

    final bearer = await callback('old-refresh');

    expect(bearer, 'new-id');
    verify(() => storage.write(key: 'access_token', value: 'new-access'))
        .called(1);
    verify(() => storage.write(key: 'id_token', value: 'new-id')).called(1);
    // No new refresh token in the response: keep the old one.
    verify(() => storage.write(key: 'refresh_token', value: 'old-refresh'))
        .called(1);
    // The offline cache goes through updateTokens, which restamps the
    // session timestamp (the old hand-written JSON skipped it).
    expect(cachedWrites, hasLength(1));
    expect(cachedWrites.single.$1, '+251911234567');
    expect(cachedWrites.single.$2.idToken, 'new-id');
  });

  test('a rejected refresh token (401) returns null and signals logout',
      () async {
    adapter.onPost(
      '/auth/refresh',
      (server) => server.reply(401, {
        'error': {'code': 'AUTH_019', 'message': 'revoked'},
      }),
      data: Matchers.any,
    );
    var expired = 0;
    final sub = signals.onSessionExpired.listen((_) => expired++);

    final bearer = await callback('dead-refresh');
    await Future<void>.delayed(Duration.zero);

    expect(bearer, isNull);
    expect(expired, 1);
    expect(cachedWrites, isEmpty);
    await sub.cancel();
  });

  test('a 400 is treated like a rejection too', () async {
    adapter.onPost(
      '/auth/refresh',
      (server) => server.reply(400, {'message': 'bad'}),
      data: Matchers.any,
    );
    var expired = 0;
    final sub = signals.onSessionExpired.listen((_) => expired++);

    expect(await callback('x'), isNull);
    await Future<void>.delayed(Duration.zero);
    expect(expired, 1);
    await sub.cancel();
  });

  test('transient failures rethrow and do not sign the user out', () async {
    adapter.onPost(
      '/auth/refresh',
      (server) => server.reply(503, {'message': 'down'}),
      data: Matchers.any,
    );
    var expired = 0;
    final sub = signals.onSessionExpired.listen((_) => expired++);

    await expectLater(callback('x'), throwsA(isA<DioException>()));
    await Future<void>.delayed(Duration.zero);
    expect(expired, 0);
    await sub.cancel();
  });
}
