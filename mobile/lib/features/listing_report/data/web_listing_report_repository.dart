import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/listing_report_rules.dart';
import 'package:wereda_connect/features/listing_report/domain/base_listing_report_repository.dart';

/// Web listing reports: no queue on web, so the `listing_report` create goes
/// straight to /sync. A refusal throws [SyncRejectedException] (the daily
/// limit is told apart by `mapErrorToCode`).
class WebListingReportRepository extends BaseListingReportRepository {
  WebListingReportRepository({required ApiClient api}) : _api = api;

  final ApiClient _api;

  static const _uuid = Uuid();

  @override
  Future<ListingReportOutcome> report({
    required String reporterId,
    required ReportedListingType listingType,
    required String listingId,
    required ListingReportReason reason,
  }) async {
    final now = DateTime.now().toUtc();
    final entityId = listingReportEntityId(listingType, listingId);
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'idempotencyKey':
                '$reporterId:$listingReportEntityType:$entityId:create:'
                    '${now.millisecondsSinceEpoch}',
            'entityType': listingReportEntityType,
            'entityId': entityId,
            'action': 'create',
            'timestamp': now.toIso8601String(),
            'data': listingReportData(
              listingType: listingType,
              listingId: listingId,
              reason: reason,
            ),
          },
        ],
      },
    );
    ensureSyncSucceeded(response);

    final results = response['results'];
    final first = results is List && results.isNotEmpty ? results.first : null;
    return first is Map && first['status'] == 'duplicate'
        ? ListingReportOutcome.alreadyReported
        : ListingReportOutcome.accepted;
  }
}
