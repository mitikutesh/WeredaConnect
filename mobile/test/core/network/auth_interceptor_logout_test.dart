import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/core/network/auth_interceptor.dart';

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

/// Answers every request with [status] and records the bearer it carried.
class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter(this.status);

  final int status;
  final bearers = <String?>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    bearers.add(options.headers['Authorization'] as String?);
    return ResponseBody.fromString(
      jsonEncode({'message': status == 200 ? 'ok' : 'Unauthorized'}),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

/// SIM-55: POST /auth/logout brings its own bearer and runs while the session
/// is being cleared, so the interceptor must leave it and the token slots
/// alone.
void main() {
  late MockSecureStorage storage;
  late int refreshCalls;

  setUp(() {
    storage = MockSecureStorage();
    when(() => storage.read(key: 'id_token'))
        .thenAnswer((_) async => 'stored-id');
    when(() => storage.read(key: 'refresh_token'))
        .thenAnswer((_) async => 'stored-refresh');
    when(() => storage.delete(key: any(named: 'key'))).thenAnswer((_) async {});
    when(
      () => storage.write(key: any(named: 'key'), value: any(named: 'value')),
    ).thenAnswer((_) async {});
    refreshCalls = 0;
  });

  Dio dioWith(_RecordingAdapter adapter) {
    final dio = Dio(BaseOptions(baseUrl: 'https://api.test'))
      ..httpClientAdapter = adapter;
    dio.interceptors.add(
      AuthInterceptor(
        storage: storage,
        dio: dio,
        refreshTokenCallback: (_) async {
          refreshCalls++;
          return 'refreshed-id';
        },
      ),
    );
    return dio;
  }

  Future<Response<dynamic>> postLogout(Dio dio) => dio.post<dynamic>(
        ApiConfig.logoutEndpoint,
        data: {'refreshToken': 'leaving-refresh'},
        options: Options(headers: {'Authorization': 'Bearer minted'}),
      );

  test('logout keeps its own bearer; the stored one is not injected', () async {
    final adapter = _RecordingAdapter(200);

    await postLogout(dioWith(adapter));

    expect(adapter.bearers, ['Bearer minted']);
    verifyNever(() => storage.read(key: any(named: 'key')));
  });

  test('a 401 on logout neither refreshes nor clears the stored tokens',
      () async {
    final adapter = _RecordingAdapter(401);

    await expectLater(
      postLogout(dioWith(adapter)),
      throwsA(
        isA<DioException>()
            .having((e) => e.response?.statusCode, 'status', 401),
      ),
    );

    expect(adapter.bearers, ['Bearer minted']);
    expect(refreshCalls, 0);
    verifyNever(() => storage.delete(key: any(named: 'key')));
    verifyNever(
      () => storage.write(key: any(named: 'key'), value: any(named: 'value')),
    );
  });

  test('other requests still get the stored bearer (control)', () async {
    final adapter = _RecordingAdapter(200);

    await dioWith(adapter).get<dynamic>('/crops');

    expect(adapter.bearers, ['Bearer stored-id']);
  });
}
