import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';

/// Answers every request with [status] and [body], or throws [error]; keeps
/// what was sent.
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter({this.status = 200, this.body, this.error});

  final int status;
  final Object? body;
  final DioExceptionType? error;

  final requests = <RequestOptions>[];
  final sentBodies = <String>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    if (requestStream != null) {
      final bytes = await requestStream.expand((chunk) => chunk).toList();
      sentBodies.add(utf8.decode(bytes));
    }
    if (error != null) {
      throw DioException(requestOptions: options, type: error!);
    }
    return ResponseBody.fromString(
      body == null ? '' : jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

/// [plain] mirrors the web client, whose Dio hands back the raw String.
({AccountDeletionRepository repo, _StubAdapter adapter}) _build({
  int status = 200,
  Object? body,
  DioExceptionType? error,
  bool plain = false,
}) {
  final adapter = _StubAdapter(status: status, body: body, error: error);
  final dio = Dio(
    BaseOptions(
      baseUrl: 'https://api.test',
      responseType: plain ? ResponseType.plain : ResponseType.json,
    ),
  )..httpClientAdapter = adapter;
  return (repo: AccountDeletionRepository(dio: dio), adapter: adapter);
}

Map<String, dynamic> _error(String code, [Map<String, dynamic>? extra]) => {
      'error': {'code': code, 'message': 'server text', ...?extra},
    };

const _blockedBody = {
  'error': {
    'code': 'ACCOUNT_002',
    'message': 'Finish or cancel your active orders first.',
    'blockers': [
      {'type': 'order', 'id': 'ord-1', 'status': 'funded', 'role': 'buyer'},
      {
        'type': 'escrow',
        'id': 'escrow-order-ord-1',
        'status': 'escrowed',
        'role': 'buyer',
      },
      {'type': 'delivery', 'id': 'del-7', 'status': 'pickedUp'},
      {'type': 'dispute', 'id': 'ord-4', 'status': 'disputed'},
      {
        'type': 'payment_review',
        'id': 'ord-9',
        'status': 'order_payment_unmatched',
      },
      {'type': 'loan', 'id': 'x-1'},
      'not a blocker',
    ],
    'blockerCount': 25,
  },
};

void main() {
  group('request', () {
    test('sends DELETE /user/account with {"confirm": true}', () async {
      final built = _build(
        body: {
          'deleted': true,
          'userId': 'u-1',
          'deletionState': 'completed',
          'deletedAt': '2026-10-06T09:15:02.000Z',
        },
      );

      await built.repo.deleteAccount();

      expect(built.adapter.requests, hasLength(1));
      final request = built.adapter.requests.single;
      expect(request.method, 'DELETE');
      expect(request.path, '/user/account');
      expect(request.queryParameters, isEmpty);
      expect(jsonDecode(built.adapter.sentBodies.single), {'confirm': true});
    });
  });

  for (final plain in [false, true]) {
    final platform = plain ? 'web (String body)' : 'mobile (decoded body)';

    group(platform, () {
      test('200 with deleted: true is done', () async {
        final built = _build(
          plain: plain,
          body: {'deleted': true, 'deletionState': 'completed'},
        );
        expect(await built.repo.deleteAccount(), const DeleteAccountDone());
      });

      test('a 200 without deleted: true is never read as done', () async {
        final built = _build(plain: plain, body: {'ok': true});
        expect(
          await built.repo.deleteAccount(),
          const DeleteAccountFailed(AccountDeletionFailure.unexpected),
        );
      });

      test('409 ACCOUNT_002 carries every listed blocker and the full count',
          () async {
        final built = _build(plain: plain, status: 409, body: _blockedBody);

        final result = await built.repo.deleteAccount();

        expect(
          result,
          const DeleteAccountBlocked(
            blockers: [
              AccountDeletionBlocker(
                type: 'order',
                id: 'ord-1',
                status: 'funded',
                role: 'buyer',
              ),
              AccountDeletionBlocker(
                type: 'escrow',
                id: 'escrow-order-ord-1',
                status: 'escrowed',
                role: 'buyer',
              ),
              AccountDeletionBlocker(
                type: 'delivery',
                id: 'del-7',
                status: 'pickedUp',
              ),
              AccountDeletionBlocker(
                type: 'dispute',
                id: 'ord-4',
                status: 'disputed',
              ),
              AccountDeletionBlocker(
                type: 'payment_review',
                id: 'ord-9',
                status: 'order_payment_unmatched',
              ),
              // Unknown types are kept: the set is open.
              AccountDeletionBlocker(type: 'loan', id: 'x-1'),
            ],
            blockerCount: 25,
          ),
        );
      });

      test('403 AUTH_040 asks for the password', () async {
        final built =
            _build(plain: plain, status: 403, body: _error('AUTH_040'));
        expect(
          await built.repo.deleteAccount(),
          const DeleteAccountStepUpRequired(),
        );
      });

      test('503 ACCOUNT_005 is pending with the server retry hint', () async {
        final built = _build(
          plain: plain,
          status: 503,
          body: {
            ..._error('ACCOUNT_005', {'retryAfterSeconds': 5}),
            'deletionState': 'in_progress',
          },
        );
        expect(
          await built.repo.deleteAccount(),
          const DeleteAccountPending(retryAfterSeconds: 5),
        );
      });

      test('410 ACCOUNT_004 completed means the account is gone', () async {
        final built = _build(
          plain: plain,
          status: 410,
          body: _error('ACCOUNT_004', {'deletionState': 'completed'}),
        );
        expect(await built.repo.deleteAccount(), const DeleteAccountDone());
      });

      test('410 ACCOUNT_004 in_progress resumes like a 503', () async {
        final built = _build(
          plain: plain,
          status: 410,
          body: _error('ACCOUNT_004', {'deletionState': 'in_progress'}),
        );
        expect(await built.repo.deleteAccount(), const DeleteAccountPending());
      });

      test('403 ACCOUNT_003 is a suspended account', () async {
        final built =
            _build(plain: plain, status: 403, body: _error('ACCOUNT_003'));
        expect(
          await built.repo.deleteAccount(),
          const DeleteAccountFailed(AccountDeletionFailure.suspended),
        );
      });

      test('404 AUTH_013 is a missing account', () async {
        final built =
            _build(plain: plain, status: 404, body: _error('AUTH_013'));
        expect(
          await built.repo.deleteAccount(),
          const DeleteAccountFailed(AccountDeletionFailure.accountNotFound),
        );
      });
    });
  }

  group('other answers', () {
    for (final (status, code) in [
      (400, 'ACCOUNT_001'),
      (500, 'AUTH_007'),
      (403, 'AUTH_041'),
      (409, 'SOMETHING_ELSE'),
      (502, null),
    ]) {
      test('$status $code is unexpected', () async {
        final built = _build(
          status: status,
          body: code == null ? null : _error(code),
        );
        expect(
          await built.repo.deleteAccount(),
          const DeleteAccountFailed(AccountDeletionFailure.unexpected),
        );
      });
    }

    test('503 ACCOUNT_005 without a hint waits the default 2 s', () async {
      final built = _build(status: 503, body: _error('ACCOUNT_005'));
      expect(
        await built.repo.deleteAccount(),
        const DeleteAccountPending(),
      );
    });

    for (final type in [
      DioExceptionType.connectionError,
      DioExceptionType.connectionTimeout,
      DioExceptionType.receiveTimeout,
      DioExceptionType.unknown,
    ]) {
      test('no answer (${type.name}) is a network failure, never success',
          () async {
        final built = _build(error: type);
        expect(
          await built.repo.deleteAccount(),
          const DeleteAccountFailed(AccountDeletionFailure.network),
        );
      });
    }

    test('a rejected certificate is not reported as a network failure',
        () async {
      final built = _build(error: DioExceptionType.badCertificate);
      expect(
        await built.repo.deleteAccount(),
        const DeleteAccountFailed(AccountDeletionFailure.unexpected),
      );
    });
  });
}
