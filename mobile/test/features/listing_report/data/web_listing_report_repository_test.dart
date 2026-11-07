import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/listing_report_rules.dart';
import 'package:wereda_connect/features/listing_report/data/web_listing_report_repository.dart';
import 'package:wereda_connect/features/listing_report/domain/base_listing_report_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// FUP-67: web has no queue, so a report goes straight to /sync.
void main() {
  late _MockApiClient api;
  late WebListingReportRepository repository;
  Map<String, dynamic>? sent;

  setUp(() {
    api = _MockApiClient();
    repository = WebListingReportRepository(api: api);
    sent = null;
  });

  void answerSync(Map<String, dynamic> result) {
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      sent = invocation.namedArguments[#data] as Map<String, dynamic>;
      return {
        'results': [result],
      };
    });
  }

  Future<ListingReportOutcome> report() => repository.report(
        reporterId: 'buyer-1',
        listingType: ReportedListingType.trip,
        listingId: 'trip-1',
        reason: ListingReportReason.misleading,
      );

  test('sends one listing_report create and reads success as accepted',
      () async {
    answerSync({'operationId': 'op', 'status': 'success', 'serverVersion': 1});

    expect(await report(), ListingReportOutcome.accepted);

    final op = (sent!['operations'] as List).single as Map<String, dynamic>;
    expect(op['entityType'], 'listing_report');
    expect(op['entityId'], 'trip#trip-1');
    expect(op['action'], 'create');
    expect(
      op['idempotencyKey'],
      startsWith('buyer-1:listing_report:trip#trip-1:create:'),
    );
    expect(op['data'], {
      'listingType': 'trip',
      'listingId': 'trip-1',
      'reason': 'misleading',
    });
  });

  test('a duplicate means the buyer already reported it', () async {
    answerSync({
      'status': 'duplicate',
      'message': 'This listing is already reported.',
    });

    expect(await report(), ListingReportOutcome.alreadyReported);
  });

  test('the daily limit throws and maps to its own code', () async {
    answerSync({
      'status': 'error',
      'message': 'SYNC_VALIDATION: report limit reached. A buyer can send at '
          'most 10 reports a day.',
    });

    await expectLater(
      report(),
      throwsA(
        isA<SyncRejectedException>().having(
          mapErrorToCode,
          'code',
          AppErrorCode.reportLimitReached,
        ),
      ),
    );
  });

  test('another refusal throws the server rejection', () async {
    answerSync({
      'status': 'error',
      'message': 'SYNC_VALIDATION: The reported listing does not exist.',
    });

    await expectLater(report(), throwsA(isA<SyncRejectedException>()));
  });
}
