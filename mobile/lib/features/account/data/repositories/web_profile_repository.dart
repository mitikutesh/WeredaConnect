import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/account/domain/profile_edit.dart';

/// Web profile edits (SIM-56): no queue on web, so the `user` update goes
/// straight to /sync. A refusal throws [SyncRejectedException] with the
/// server's message (`AUTH_027: ...` names the fields holding a phone
/// number); no answer throws the API client's error.
class WebProfileRepository extends BaseProfileRepository {
  WebProfileRepository({required super.api}) : _api = api;

  final ApiClient _api;

  static const _uuid = Uuid();

  @override
  Future<ProfileSaveOutcome> save({
    required String userId,
    required ProfileChanges changes,
  }) async {
    final now = DateTime.now().toUtc();
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'idempotencyKey': '$userId:$userProfileEntityType:$userId:update:'
                '${now.millisecondsSinceEpoch}',
            'entityType': userProfileEntityType,
            'entityId': userId,
            'action': 'update',
            'timestamp': now.toIso8601String(),
            'data': changes.toSyncData(),
          },
        ],
      },
    );
    ensureSyncSucceeded(response);
    return ProfileSaveOutcome.saved;
  }

  @override
  Future<bool> hasUnsentEdit(String userId) async => false;
}
