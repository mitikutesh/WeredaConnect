import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';

import 'package:wereda_connect/core/sync/sync_queue_web.dart';

/// GCR-02: web has no queue, so every web repository posts its own `/sync`
/// batch; the interceptor on the API client names the web platform on each
/// operation of it.
void main() {
  group('SyncPlatformInterceptor (GCR-02)', () {
    late Dio dio;
    late DioAdapter adapter;
    Object? sent;

    setUp(() {
      sent = null;
      dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
      adapter = DioAdapter(dio: dio);
      dio.interceptors.addAll([
        SyncPlatformInterceptor(),
        InterceptorsWrapper(
          onRequest: (options, handler) {
            sent = options.data;
            handler.next(options);
          },
        ),
      ]);
      const ok = <String, dynamic>{};
      adapter
        ..onPost('/sync', (s) => s.reply(200, ok), data: Matchers.any)
        ..onPost('/orders', (s) => s.reply(200, ok), data: Matchers.any);
    });

    Map<String, dynamic> operation(String id, {String? platform}) => {
          'operationId': id,
          'entityType': 'delivery',
          'entityId': 'del-1',
          'action': 'update',
          'data': {'status': 'pickedUp', 'version': 1},
          if (platform != null) 'platform': platform,
        };

    test('names web on every operation of a /sync batch', () async {
      await dio.post<dynamic>(
        '/sync',
        data: {
          'syncBatchId': 'b-1',
          'clientId': 'web',
          'operations': [operation('op-1'), operation('op-2')],
        },
      );

      final body = sent! as Map<String, dynamic>;
      expect(body['syncBatchId'], 'b-1');
      expect(body['clientId'], 'web');
      final ops = (body['operations'] as List).cast<Map<String, dynamic>>();
      expect(ops.map((o) => o['platform']), ['web', 'web']);
      expect(ops.first['operationId'], 'op-1');
      expect(ops.first['data'], {'status': 'pickedUp', 'version': 1});
      expect(SyncQueue.platform, 'web');
    });

    test("leaves the caller's maps as they were", () async {
      final op = operation('op-1');
      final batch = <String, dynamic>{
        'syncBatchId': 'b-1',
        'operations': [op],
      };

      await dio.post<dynamic>('/sync', data: batch);

      expect(op.containsKey('platform'), isFalse);
      expect(identical(sent, batch), isFalse);
    });

    test('an operation that names a platform keeps it', () async {
      await dio.post<dynamic>(
        '/sync',
        data: {
          'syncBatchId': 'b-1',
          'operations': [operation('op-1', platform: 'android')],
        },
      );

      final ops = (sent! as Map<String, dynamic>)['operations'] as List;
      expect((ops.single as Map)['platform'], 'android');
    });

    test('another path is left alone', () async {
      final body = <String, dynamic>{
        'operations': [operation('op-1')],
      };

      await dio.post<dynamic>('/orders', data: body);

      expect(identical(sent, body), isTrue);
      expect(
        ((sent! as Map)['operations'] as List).single,
        isNot(contains('platform')),
      );
    });
  });
}
