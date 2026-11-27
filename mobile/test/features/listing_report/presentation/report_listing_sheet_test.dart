import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/listing_report_rules.dart';
import 'package:wereda_connect/features/listing_report/domain/base_listing_report_repository.dart';
import 'package:wereda_connect/features/listing_report/presentation/report_listing_sheet.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

/// Records each report; answers [outcome] or throws [error].
class _FakeReportRepository extends BaseListingReportRepository {
  final calls = <(String, ReportedListingType, String, ListingReportReason)>[];
  ListingReportOutcome outcome = ListingReportOutcome.accepted;
  Object? error;

  @override
  Future<ListingReportOutcome> report({
    required String reporterId,
    required ReportedListingType listingType,
    required String listingId,
    required ListingReportReason reason,
  }) async {
    calls.add((reporterId, listingType, listingId, reason));
    final failure = error;
    if (failure != null) throw failure;
    return outcome;
  }
}

/// FUP-67: the buyer picks a reason and sends; the sheet closes with a thank
/// you, or keeps the reason and says why it could not send.
void main() {
  late _FakeReportRepository repository;

  setUp(() => repository = _FakeReportRepository());

  Widget app({Locale locale = const Locale('en')}) =>
      RepositoryProvider<BaseListingReportRepository>.value(
        value: repository,
        child: MaterialApp(
          // The app's delegates: om has no Material localizations of its own.
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          locale: locale,
          home: const Scaffold(
            body: ReportListingButton(
              listingType: ReportedListingType.crop,
              listingId: 'crop-1',
              reporterId: 'buyer-1',
            ),
          ),
        ),
      );

  Future<void> openSheet(WidgetTester tester) async {
    tester.view.physicalSize = const Size(800, 1600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(app());
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('report_listing_button')));
    await tester.pumpAndSettle();
  }

  FilledButton sendButton(WidgetTester tester) =>
      tester.widget<FilledButton>(find.byKey(const Key('report_listing_send')));

  testWidgets('the sheet lists every reason and says the owner will not know',
      (tester) async {
    await openSheet(tester);

    expect(find.text('What is wrong with this listing?'), findsOneWidget);
    expect(
      find.text(
        'The owner will not see who reported it. Our team reviews every '
        'report.',
      ),
      findsOneWidget,
    );
    for (final reason in ListingReportReason.values) {
      expect(find.byKey(Key('report_reason_${reason.wire}')), findsOneWidget);
    }
    expect(find.text('Shares a phone number or other contact'), findsOneWidget);
  });

  testWidgets('Send stays off until a reason is picked', (tester) async {
    await openSheet(tester);

    expect(sendButton(tester).onPressed, isNull);

    await tester.tap(find.byKey(const Key('report_reason_scam')));
    await tester.pump();

    expect(sendButton(tester).onPressed, isNotNull);
    expect(repository.calls, isEmpty);
  });

  testWidgets('a sent report closes the sheet with a thank you',
      (tester) async {
    await openSheet(tester);

    await tester.tap(find.byKey(const Key('report_reason_contact_info')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('report_listing_send')));
    await tester.pumpAndSettle();

    expect(repository.calls, [
      (
        'buyer-1',
        ReportedListingType.crop,
        'crop-1',
        ListingReportReason.contactInfo,
      ),
    ]);
    expect(find.byType(ReportListingSheet), findsNothing);
    expect(
      find.text('Thank you. Our team will review this listing.'),
      findsOneWidget,
    );
  });

  testWidgets('a report the server already holds says so', (tester) async {
    repository.outcome = ListingReportOutcome.alreadyReported;
    await openSheet(tester);

    await tester.tap(find.byKey(const Key('report_reason_other')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('report_listing_send')));
    await tester.pumpAndSettle();

    expect(find.text('You already reported this listing.'), findsOneWidget);
  });

  testWidgets('the daily limit is explained in the sheet, which stays open',
      (tester) async {
    repository.error = const SyncRejectedException(
      'SYNC_VALIDATION: report limit reached. A buyer can send at most 10 '
      'reports a day.',
    );
    await openSheet(tester);

    await tester.tap(find.byKey(const Key('report_reason_scam')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('report_listing_send')));
    await tester.pumpAndSettle();

    expect(find.byType(ReportListingSheet), findsOneWidget);
    expect(
      find.text('You can send up to 10 reports a day. Try again tomorrow.'),
      findsOneWidget,
    );
    // The reason is kept, so the buyer can try again.
    expect(sendButton(tester).onPressed, isNotNull);
  });

  testWidgets('another refusal says the report was not sent', (tester) async {
    repository.error = const SyncRejectedException(
      'SYNC_VALIDATION: The reported listing does not exist.',
    );
    await openSheet(tester);

    await tester.tap(find.byKey(const Key('report_reason_misleading')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('report_listing_send')));
    await tester.pumpAndSettle();

    expect(find.text('Your report could not be sent.'), findsOneWidget);
  });

  testWidgets('no connection on web is said as such', (tester) async {
    repository.error = const NetworkException('offline');
    await openSheet(tester);

    await tester.tap(find.byKey(const Key('report_reason_offensive')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('report_listing_send')));
    await tester.pumpAndSettle();

    expect(
      find.byKey(const Key('report_listing_error')),
      findsOneWidget,
    );
    expect(find.text('Your report could not be sent.'), findsNothing);
  });

  testWidgets('Cancel closes without a report', (tester) async {
    await openSheet(tester);

    await tester.tap(find.byKey(const Key('report_listing_cancel')));
    await tester.pumpAndSettle();

    expect(find.byType(ReportListingSheet), findsNothing);
    expect(repository.calls, isEmpty);
  });

  for (final locale in const [Locale('am'), Locale('om')]) {
    testWidgets('the sheet renders in ${locale.languageCode}', (tester) async {
      tester.view.physicalSize = const Size(800, 1600);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(app(locale: locale));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('report_listing_button')));
      await tester.pumpAndSettle();

      expect(find.byType(ReportListingSheet), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  }
}
