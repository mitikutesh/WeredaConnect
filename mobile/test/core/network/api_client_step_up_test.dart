import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';

void main() {
  late DioAdapter dioAdapter;

  setUp(() {
    ApiClient().initialize(environment: Environment.development);
    dioAdapter = DioAdapter(dio: ApiClient().dio);
  });

  test('403 AUTH_040 becomes StepUpRequiredException, not AuthException',
      () async {
    dioAdapter.onPost(
      '/escrow/initiate',
      (server) => server.reply(403, {
        'error': {'code': 'AUTH_040', 'message': 'step-up required'},
      }),
      data: Matchers.any,
    );

    await expectLater(
      ApiClient().post<Map<String, dynamic>>(
        '/escrow/initiate',
        data: <String, dynamic>{},
      ),
      throwsA(
        isA<StepUpRequiredException>()
            .having((e) => e.message, 'message', 'step-up required')
            .having((e) => e.statusCode, 'statusCode', 403)
            .having((e) => e.requiresFayda, 'requiresFayda', isFalse),
      ),
    );
  });

  test('403 AUTH_041 becomes a Fayda StepUpRequiredException', () async {
    dioAdapter.onPost(
      '/escrow/initiate',
      (server) => server.reply(403, {
        'error': {'code': 'AUTH_041', 'message': 'Fayda needed'},
      }),
      data: Matchers.any,
    );

    await expectLater(
      ApiClient().post<Map<String, dynamic>>(
        '/escrow/initiate',
        data: <String, dynamic>{},
      ),
      throwsA(
        isA<StepUpRequiredException>()
            .having((e) => e.requiresFayda, 'requiresFayda', isTrue)
            .having((e) => e.code, 'code', 'AUTH_041'),
      ),
    );
  });

  test('a plain 403 is still an AuthException', () async {
    dioAdapter.onPost(
      '/escrow/dispute',
      (server) => server.reply(403, {
        'error': {'code': 'ESCROW_005', 'message': 'not yours'},
      }),
      data: Matchers.any,
    );

    await expectLater(
      ApiClient().post<Map<String, dynamic>>(
        '/escrow/dispute',
        data: <String, dynamic>{},
      ),
      throwsA(
        isA<AuthException>().having((e) => e.message, 'message', 'not yours'),
      ),
    );
  });

  test('a structured error body no longer breaks message extraction', () async {
    dioAdapter.onGet(
      '/escrow/x',
      (server) => server.reply(400, {
        'error': {'code': 'ESCROW_VALIDATION', 'message': 'bad amount'},
      }),
    );

    await expectLater(
      ApiClient().get<Map<String, dynamic>>('/escrow/x'),
      throwsA(
        isA<ValidationException>()
            .having((e) => e.message, 'message', 'bad amount'),
      ),
    );
  });
}
