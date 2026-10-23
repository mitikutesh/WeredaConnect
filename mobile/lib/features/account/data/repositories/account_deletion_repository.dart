import 'package:dio/dio.dart';
import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/network/json_body.dart';

/// One active item that blocks the deletion (`409 ACCOUNT_002`).
///
/// [type] is an open set: `order`, `escrow`, `delivery`, `dispute` and
/// `payment_review` today, anything else later.
class AccountDeletionBlocker extends Equatable {
  final String type;
  final String id;
  final String status;
  final String role;

  const AccountDeletionBlocker({
    required this.type,
    required this.id,
    this.status = '',
    this.role = '',
  });

  @override
  List<Object?> get props => [type, id, status, role];
}

/// Why a deletion request did not go through.
enum AccountDeletionFailure {
  /// The device has no network, so nothing was sent.
  offline,

  /// The request got no answer. The server may or may not have deleted the
  /// account; sending it again is safe (a finished deletion replays `200`).
  network,

  /// The server started the deletion but kept answering `503 ACCOUNT_005`.
  incomplete,

  /// `403 ACCOUNT_003`: a suspended account cannot delete itself.
  suspended,

  /// `404 AUTH_013`: the server has no account for this session.
  accountNotFound,

  /// Anything else (`400 ACCOUNT_001`, `500 AUTH_007`, an unknown answer).
  unexpected,
}

/// The answer to one `DELETE /user/account`.
sealed class DeleteAccountResult extends Equatable {
  const DeleteAccountResult();

  @override
  List<Object?> get props => [];
}

/// `200`, or `410 ACCOUNT_004` in a state other than `in_progress`: the
/// account is gone and the device must wipe what it holds.
final class DeleteAccountDone extends DeleteAccountResult {
  const DeleteAccountDone();
}

/// `409 ACCOUNT_002`: active items must be finished first. At most 20 are
/// listed; [blockerCount] is the full count.
final class DeleteAccountBlocked extends DeleteAccountResult {
  final List<AccountDeletionBlocker> blockers;
  final int blockerCount;

  const DeleteAccountBlocked({
    required this.blockers,
    required this.blockerCount,
  });

  @override
  List<Object?> get props => [blockers, blockerCount];
}

/// `403 AUTH_040`: the password must be entered again first.
final class DeleteAccountStepUpRequired extends DeleteAccountResult {
  const DeleteAccountStepUpRequired();
}

/// `503 ACCOUNT_005` (or `410 ACCOUNT_004` while `in_progress`): the
/// deletion started and resumes when the same request is sent again.
final class DeleteAccountPending extends DeleteAccountResult {
  final int retryAfterSeconds;

  const DeleteAccountPending({this.retryAfterSeconds = 2});

  @override
  List<Object?> get props => [retryAfterSeconds];
}

final class DeleteAccountFailed extends DeleteAccountResult {
  final AccountDeletionFailure reason;

  const DeleteAccountFailed(this.reason);

  @override
  List<Object?> get props => [reason];
}

/// Calls `DELETE /user/account` (specs/contracts/account-deletion.md).
///
/// Online only: the request is never put in the sync queue. Shared by mobile
/// and web, since nothing here touches local storage. It uses the app's Dio
/// (auth and data usage interceptors) directly because the error body
/// carries what the screen needs: the blocker list, `retryAfterSeconds` and
/// `deletionState`.
class AccountDeletionRepository {
  final Dio _dio;

  AccountDeletionRepository({Dio? dio}) : _dio = dio ?? ApiClient().dio;

  Future<DeleteAccountResult> deleteAccount() async {
    try {
      final response = await _dio.delete<dynamic>(
        ApiConfig.userAccountEndpoint,
        data: {'confirm': true},
      );
      // Only an explicit confirmation counts: an unexpected 2xx body must
      // never read as a deleted account.
      final body = decodeJsonMap(response.data);
      return body?['deleted'] == true
          ? const DeleteAccountDone()
          : const DeleteAccountFailed(AccountDeletionFailure.unexpected);
    } on DioException catch (e) {
      final response = e.response;
      if (response == null) {
        // A failed TLS handshake sent nothing; any other missing answer may
        // have reached the server.
        return DeleteAccountFailed(
          isSecureConnectionFailure(e)
              ? AccountDeletionFailure.unexpected
              : AccountDeletionFailure.network,
        );
      }
      return _fromError(response.statusCode, decodeJsonMap(response.data));
    } on Object {
      return const DeleteAccountFailed(AccountDeletionFailure.unexpected);
    }
  }

  static DeleteAccountResult _fromError(
    int? status,
    Map<String, dynamic>? body,
  ) {
    final error = body?['error'];
    final details = error is Map ? error : const <String, dynamic>{};
    final code = details['code'];

    if (status == 409 && code == 'ACCOUNT_002') {
      final blockers = _blockers(details['blockers']);
      final count = details['blockerCount'];
      return DeleteAccountBlocked(
        blockers: blockers,
        blockerCount: count is num && count >= blockers.length
            ? count.toInt()
            : blockers.length,
      );
    }
    if (status == 403 && code == stepUpRequiredCode) {
      return const DeleteAccountStepUpRequired();
    }
    if (status == 503 && code == 'ACCOUNT_005') {
      final retryAfter = details['retryAfterSeconds'];
      return DeleteAccountPending(
        retryAfterSeconds:
            retryAfter is num && retryAfter > 0 ? retryAfter.ceil() : 2,
      );
    }
    if (status == 410 && code == 'ACCOUNT_004') {
      return details['deletionState'] == 'in_progress'
          ? const DeleteAccountPending()
          : const DeleteAccountDone();
    }
    if (status == 403 && code == 'ACCOUNT_003') {
      return const DeleteAccountFailed(AccountDeletionFailure.suspended);
    }
    if (status == 404 && code == 'AUTH_013') {
      return const DeleteAccountFailed(AccountDeletionFailure.accountNotFound);
    }
    return const DeleteAccountFailed(AccountDeletionFailure.unexpected);
  }

  static List<AccountDeletionBlocker> _blockers(Object? raw) {
    if (raw is! List) return const [];
    return [
      for (final item in raw)
        if (item is Map && item['type'] is String)
          AccountDeletionBlocker(
            type: item['type'] as String,
            id: item['id'] is String ? item['id'] as String : '',
            status: item['status'] is String ? item['status'] as String : '',
            role: item['role'] is String ? item['role'] as String : '',
          ),
    ];
  }
}
