import 'dart:convert';
import 'dart:typed_data';

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
  // The refresh token in the shared slot: the one being refreshed unless a
  // test says otherwise.
  String? storedRefresh;

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
    storedRefresh = null;
    when(() => storage.read(key: 'refresh_token'))
        .thenAnswer((_) async => storedRefresh);
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
    storedRefresh = 'old-refresh';

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
    storedRefresh = 'dead-refresh';

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
    storedRefresh = 'x';

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

  // FUP-82: the answer came after the session it refreshed was gone.
  group('a session that ended while the refresh ran', () {
    void replyTokens({void Function()? whileOut}) {
      dio.httpClientAdapter = _HookAdapter(
        onFetch: whileOut,
        status: 200,
        body: {
          'tokens': {
            'accessToken': 'late-access',
            'idToken': 'late-id',
            'expiresIn': 900,
          },
        },
      );
    }

    void replyRejected({void Function()? whileOut}) {
      dio.httpClientAdapter = _HookAdapter(
        onFetch: whileOut,
        status: 401,
        body: {
          'error': {'code': 'AUTH_019', 'message': 'revoked'},
        },
      );
    }

    void expectNothingWritten() {
      verifyNever(
        () => storage.write(
          key: any(named: 'key'),
          value: any(named: 'value'),
        ),
      );
      expect(cachedWrites, isEmpty);
    }

    test('a logout here meanwhile: nothing is written, the bearer comes back',
        () async {
      storedRefresh = 'r1';
      // The logout moves the session count on; the slot may still hold the
      // token for a moment.
      replyTokens(whileOut: signals.sessionChanged);

      final bearer = await callback('r1');

      expect(bearer, 'late-id', reason: 'the old request can still finish');
      expectNothingWritten();
    });

    test('another tab signed someone in: the slot changed, nothing written',
        () async {
      storedRefresh = 'r1';
      replyTokens(whileOut: () => storedRefresh = 'someone-else');

      expect(await callback('r1'), 'late-id');
      expectNothingWritten();
    });

    test('a logout that starts between the writes stops the rest', () async {
      storedRefresh = 'r1';
      replyTokens();
      when(
        () => storage.write(key: 'access_token', value: 'late-access'),
      ).thenAnswer((_) async => signals.sessionChanged());

      await callback('r1');

      verify(() => storage.write(key: 'access_token', value: 'late-access'))
          .called(1);
      verifyNever(
        () => storage.write(key: 'id_token', value: any(named: 'value')),
      );
      verifyNever(
        () => storage.write(key: 'refresh_token', value: any(named: 'value')),
      );
      expect(cachedWrites, isEmpty);
    });

    test('a refused refresh of an ended session signs nobody out', () async {
      var expired = 0;
      final sub = signals.onSessionExpired.listen((_) => expired++);
      addTearDown(sub.cancel);
      storedRefresh = 'r1';
      replyRejected(whileOut: signals.sessionChanged);

      expect(await callback('r1'), isNull);
      await Future<void>.delayed(Duration.zero);

      expect(expired, 0);
    });

    test('a refused refresh after another sign-in signs nobody out', () async {
      var expired = 0;
      final sub = signals.onSessionExpired.listen((_) => expired++);
      addTearDown(sub.cancel);
      storedRefresh = 'next-user-refresh';
      replyRejected();

      expect(await callback('r1'), isNull);
      await Future<void>.delayed(Duration.zero);

      expect(expired, 0);
    });

    // Two web tabs on one account, its refresh token dead: the first tab's
    // refusal empties the shared slot before the second tab's comes back.
    for (final emptied in [null, '']) {
      test(
          'a refused refresh after another tab emptied the slot ("$emptied") '
          'still signs out', () async {
        var expired = 0;
        final sub = signals.onSessionExpired.listen((_) => expired++);
        addTearDown(sub.cancel);
        storedRefresh = 'r1';
        replyRejected(whileOut: () => storedRefresh = emptied);

        expect(await callback('r1'), isNull);
        await Future<void>.delayed(Duration.zero);

        expect(expired, 1);
      });
    }

    test('an emptied slot is not written by a successful refresh', () async {
      storedRefresh = 'r1';
      replyTokens(whileOut: () => storedRefresh = null);

      expect(await callback('r1'), 'late-id');
      expectNothingWritten();
    });

    test('an unreadable slot leaves the decision to the session count',
        () async {
      when(() => storage.read(key: 'refresh_token'))
          .thenThrow(Exception('keystore'));
      var expired = 0;
      final sub = signals.onSessionExpired.listen((_) => expired++);
      addTearDown(sub.cancel);
      replyRejected();

      expect(await callback('r1'), isNull);
      await Future<void>.delayed(Duration.zero);
      expect(expired, 1);

      replyTokens();
      expect(await callback('r1'), 'late-id');
      verify(() => storage.write(key: 'id_token', value: 'late-id')).called(1);
    });
  });
}

/// Replies [status] with [body], running [onFetch] while the request is out.
class _HookAdapter implements HttpClientAdapter {
  _HookAdapter({required this.status, required this.body, this.onFetch});

  final int status;
  final Map<String, dynamic> body;
  final void Function()? onFetch;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    onFetch?.call();
    return ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
