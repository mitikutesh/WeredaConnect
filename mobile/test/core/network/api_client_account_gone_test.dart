import 'dart:async';
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';

import 'package:wereda_connect/core/auth/session_signals.dart';
import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';

Map<String, dynamic> _gone(String state) => {
      'error': {
        'code': 'ACCOUNT_004',
        'message': 'This account has been deleted.',
        'deletionState': state,
      },
    };

/// ACC-03: every REST write (the web /sync calls, escrow, device tokens)
/// refused with 410 ACCOUNT_004 tells the AuthBloc, which decides whether to
/// wipe. The caller still gets the error.
void main() {
  late DioAdapter dioAdapter;
  late List<String> signalled;
  late StreamSubscription<String> sub;

  setUp(() {
    ApiClient().initialize(environment: Environment.development);
    dioAdapter = DioAdapter(dio: ApiClient().dio);
    signalled = [];
    sub = SessionSignals.instance.onAccountGone.listen(signalled.add);
  });

  tearDown(() => sub.cancel());

  Future<Object?> post(String path) => ApiClient()
      .post<Map<String, dynamic>>(path, data: <String, dynamic>{}).then<
          Object?>((_) => null, onError: (Object e) => e);

  for (final state in ['completed', 'in_progress']) {
    test('410 ACCOUNT_004 ($state) on /sync raises the signal', () async {
      dioAdapter.onPost(
        '/sync',
        (server) => server.reply(410, _gone(state)),
        data: Matchers.any,
      );

      final error = await post('/sync');
      await Future<void>.delayed(Duration.zero);

      expect(
        error,
        isA<ServerException>()
            .having((e) => e.statusCode, 'statusCode', 410)
            .having((e) => e.code, 'code', accountGoneCode),
      );
      expect(signalled, [state]);
    });
  }

  test('other refusals raise nothing', () async {
    dioAdapter
      ..onPost(
        '/escrow/initiate',
        (server) => server.reply(403, {
          'error': {'code': 'ACCOUNT_003', 'message': 'suspended'},
        }),
        data: Matchers.any,
      )
      ..onPost(
        '/device-tokens',
        (server) => server.reply(410, {
          'error': {'code': 'SOMETHING_ELSE', 'message': 'gone'},
        }),
        data: Matchers.any,
      );

    await post('/escrow/initiate');
    await post('/device-tokens');
    await Future<void>.delayed(Duration.zero);

    expect(signalled, isEmpty);
  });

  group('accountGoneState', () {
    test('reads the state from a decoded (mobile) and a String (web) body', () {
      expect(accountGoneState(410, _gone('completed')), 'completed');
      expect(
        accountGoneState(410, jsonEncode(_gone('cognito_pending'))),
        'cognito_pending',
      );
    });

    test('a refusal without a state reads as empty', () {
      expect(
        accountGoneState(410, {
          'error': {'code': 'ACCOUNT_004'},
        }),
        '',
      );
    });

    test('anything else is null', () {
      expect(accountGoneState(409, _gone('completed')), isNull);
      expect(accountGoneState(410, null), isNull);
      expect(accountGoneState(410, 'not json'), isNull);
      expect(
        accountGoneState(410, {
          'error': {'code': 'ACCOUNT_005'},
        }),
        isNull,
      );
    });
  });
}
