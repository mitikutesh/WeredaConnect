import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';
import 'package:wereda_connect/features/reputation/domain/models/own_reputation.dart';
import 'package:wereda_connect/features/reputation/domain/models/reputation_event.dart';
import 'package:wereda_connect/features/reputation/presentation/screens/my_reputation_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

import '../reputation_fixtures.dart';

/// ESC-31: My reputation, the farmer's or trucker's own standing, rating
/// and history, with the appeal. Only the signed-in user's own data.
void main() {
  late MockReputationRepository repository;

  setUp(() {
    repository = MockReputationRepository();
    when(() => repository.readAppealDraft(any(), any()))
        .thenAnswer((_) async => null);
    when(() => repository.saveAppealDraft(any(), any(), any()))
        .thenAnswer((_) async {});
  });

  void answer(
    OwnReputation reputation, {
    List<ReputationEvent> events = const [],
    String? nextToken,
    bool stale = false,
  }) {
    when(() => repository.loadOwn('f-1', force: any(named: 'force')))
        .thenAnswer((_) async => snapshot(reputation, stale: stale));
    when(() => repository.loadEvents('f-1')).thenAnswer(
      (_) async => eventsResult(events, nextToken: nextToken, stale: stale),
    );
  }

  Future<void> open(
    WidgetTester tester, {
    Locale locale = const Locale('en'),
    Future<Uint8List?> Function()? pickPhoto,
  }) async {
    tester.view.physicalSize = const Size(420, 2400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        // As the app does: Afaan Oromoo has no Material strings of its own.
        localizationsDelegates: const [
          ...AppLocalizations.localizationsDelegates,
          FallbackMaterialLocalizationsDelegate(),
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        locale: locale,
        home: MyReputationScreen(
          userId: 'f-1',
          repository: repository,
          pickPhoto: pickPhoto,
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  group('standing', () {
    testWidgets('good: nothing to do, and where the label starts',
        (tester) async {
      answer(ownReputation());
      await open(tester);

      expect(find.text('My reputation'), findsOneWidget);
      expect(find.text('Good standing'), findsOneWidget);
      expect(find.text('No penalty points'), findsOneWidget);
      expect(
        find.text('Nothing to do. Keep completing your orders.'),
        findsOneWidget,
      );
      expect(
        find.text('At 6 points, buyers will see a Low reputation label.'),
        findsOneWidget,
      );
      expect(find.textContaining('paused'), findsNothing);
    });

    testWidgets('warning: private, with both thresholds and the expiry',
        (tester) async {
      answer(
        ownReputation(
          tier: 'warning',
          points: 4,
          nextPointExpiryAt: '2027-01-20T09:00:00.000Z',
        ),
      );
      await open(tester);

      expect(find.text('Please take care'), findsOneWidget);
      expect(find.text('4 penalty points'), findsOneWidget);
      expect(
        find.text('At 6 points, buyers will see a Low reputation label.'),
        findsOneWidget,
      );
      expect(
        find.text('At 10 points, new orders are paused for a while.'),
        findsOneWidget,
      );
      expect(
        find.text('Your oldest points expire on Jan 20, 2027.'),
        findsOneWidget,
      );
      expect(
        find.text(
          'Points expire with time, and a completed order can remove one.',
        ),
        findsOneWidget,
      );
    });

    testWidgets('low: what buyers see', (tester) async {
      answer(ownReputation(tier: 'low', points: 7));
      await open(tester);

      expect(find.text('Low reputation'), findsOneWidget);
      expect(find.text('7 penalty points'), findsOneWidget);
      expect(
        find.textContaining('Buyers see a Low reputation label'),
        findsOneWidget,
      );
      expect(
        find.text('At 10 points, new orders are paused for a while.'),
        findsOneWidget,
      );
    });

    testWidgets('restricted: until when, and current orders continue',
        (tester) async {
      answer(
        ownReputation(
          tier: 'restricted',
          points: 11,
          restriction: {
            'since': '2026-11-07T09:00:00.000Z',
            'until': '2026-11-21T09:00:00.000Z',
            'cause': 'points',
          },
        ),
      );
      await open(tester);

      expect(find.text('New orders paused'), findsOneWidget);
      expect(
        find.text(
          "You can't take new orders until Nov 21, 2026. Your current "
          'orders continue.',
        ),
        findsOneWidget,
      );
      // Nothing left to warn about.
      expect(find.textContaining('At 10 points'), findsNothing);
    });

    testWidgets('restricted while a review is open has no date',
        (tester) async {
      answer(
        ownReputation(
          tier: 'restricted',
          points: 5,
          restriction: {
            'since': '2026-11-07T09:00:00.000Z',
            'cause': 'delivery_overdue',
          },
        ),
      );
      await open(tester);

      expect(
        find.textContaining('while our team reviews a late delivery'),
        findsOneWidget,
      );
    });

    testWidgets("an admin tag is said plainly with the admin's reason",
        (tester) async {
      answer(
        ownReputation(
          tier: 'low',
          points: 1,
          override: {
            'tag': 'low',
            'until': '2026-12-01T00:00:00.000Z',
            'reason': 'Reported by three buyers.',
          },
        ),
      );
      await open(tester);

      expect(
        find.text('Our team added the Low reputation label until Dec 1, 2026.'),
        findsOneWidget,
      );
      expect(find.text('Reported by three buyers.'), findsOneWidget);
    });
  });

  group('rating', () {
    testWidgets('the raw average with what was deducted and what buyers see',
        (tester) async {
      answer(ownReputation(tier: 'warning', points: 4, deduction: 0.4));
      await open(tester);

      expect(find.text('4.8 · 12 ratings'), findsOneWidget);
      expect(
        find.text('4.8 from buyers, 0.4 deducted for cancellations'),
        findsOneWidget,
      );
      expect(find.text('Buyers see 4.4'), findsOneWidget);
      expect(
        find.text('10 of 12 orders completed in the last 90 days'),
        findsOneWidget,
      );
    });

    testWidgets('nothing deducted: just the average', (tester) async {
      answer(ownReputation());
      await open(tester);

      expect(find.text('4.8 from buyers'), findsOneWidget);
      expect(find.textContaining('deducted'), findsNothing);
      expect(find.textContaining('Buyers see'), findsNothing);
    });

    testWidgets('no ratings yet, no orders yet', (tester) async {
      answer(
        ownReputation(rated: false, completed: 0, faultCancels: 0, noShows: 0),
      );
      await open(tester);

      expect(find.text('No ratings yet'), findsOneWidget);
      expect(find.textContaining('from buyers'), findsNothing);
      expect(find.textContaining('orders completed'), findsNothing);
    });
  });

  testWidgets(
      'penalties switched off: the rating only, no points, no history, no '
      'events call', (tester) async {
    answer(ownReputation(penaltiesEnabled: false));
    await open(tester);

    expect(find.byKey(const Key('reputation_rating')), findsOneWidget);
    expect(find.byKey(const Key('reputation_standing')), findsNothing);
    expect(find.byKey(const Key('reputation_points')), findsNothing);
    expect(find.text('HISTORY'), findsNothing);
    verifyNever(
      () => repository.loadEvents(any(), nextToken: any(named: 'nextToken')),
    );
  });

  group('history', () {
    testWidgets('empty', (tester) async {
      answer(ownReputation());
      await open(tester);

      expect(find.text('Nothing recorded.'), findsOneWidget);
    });

    testWidgets('an active penalty: what, when, points and expiry',
        (tester) async {
      answer(
        ownReputation(tier: 'warning', points: 4),
        events: [reputationEvent('CANCEL#ord-1')],
      );
      await open(tester);

      expect(
        find.text('Cancelled less than a day before pickup'),
        findsOneWidget,
      );
      expect(find.text('Nov 1, 2026'), findsOneWidget);
      expect(find.text('4 points'), findsOneWidget);
      expect(find.text('Expires Jan 30, 2027'), findsOneWidget);
      // Not appealable: no button.
      expect(find.text('Appeal'), findsNothing);
    });

    testWidgets('every code has its own plain line', (tester) async {
      answer(
        ownReputation(),
        events: [
          reputationEvent('a', code: 'DECLINED_REQUEST', points: 1),
          reputationEvent('b', code: 'RESPONSE_EXPIRED', points: 0),
          reputationEvent('c', code: 'CANCELLED_AFTER_ACCEPT', points: 3),
          reputationEvent('d', code: 'TRIP_CANCELLED_WITH_BOOKINGS'),
          reputationEvent('e', code: 'NO_SHOW', points: 5),
          reputationEvent('f', code: 'DELIVERY_OVERDUE', points: 5),
          reputationEvent('g', code: 'CANCELLED_TRANSPORT_FAILED', points: 0),
          reputationEvent('h', code: 'ORDER_COMPLETED', points: 0),
          reputationEvent('i', code: 'SOMETHING_NEW', points: 0),
        ],
      );
      await open(tester);

      for (final text in [
        'Declined a paid order',
        "Didn't respond in time",
        'Cancelled after accepting',
        'Cancelled a trip with booked orders',
        'Missed the pickup',
        'Delivery overdue',
        'Cancelled after the transport failed',
        'Completed an order, one point removed',
        'Order event',
      ]) {
        expect(find.text(text), findsOneWidget, reason: text);
      }
      expect(find.text('1 point'), findsOneWidget);
      // Three rows with no points; the completed order shows no pill.
      expect(find.text('No points counted'), findsNWidgets(3));
    });

    testWidgets('earned back, expired and excused', (tester) async {
      answer(
        ownReputation(),
        events: [
          reputationEvent('a', earnedBack: 1),
          reputationEvent('b', status: 'expired'),
          reputationEvent('c', status: 'excused', appealStatus: 'excused'),
        ],
      );
      await open(tester);

      expect(find.text('3 still counted'), findsOneWidget);
      expect(find.text('Expired'), findsOneWidget);
      expect(find.text('Excused'), findsOneWidget);
      expect(
        find.text('Appeal accepted. The points were removed.'),
        findsOneWidget,
      );
      // Only the active one still expires.
      expect(find.text('Expires Jan 30, 2027'), findsOneWidget);
    });

    testWidgets('appeal states: pending, review, upheld with the note',
        (tester) async {
      answer(
        ownReputation(),
        events: [
          reputationEvent('a', appealStatus: 'pending'),
          reputationEvent('b', appealStatus: 'review'),
          reputationEvent(
            'c',
            appealStatus: 'upheld',
            note: 'The buyer confirmed the cancel.',
          ),
        ],
      );
      await open(tester);

      expect(
        find.text('Appeal sent. Our team will review it.'),
        findsOneWidget,
      );
      expect(find.text('Our team is reviewing this.'), findsOneWidget);
      expect(find.text('Appeal reviewed. The points stay.'), findsOneWidget);
      expect(
        find.text('Note from our team: The buyer confirmed the cancel.'),
        findsOneWidget,
      );
      expect(find.text('Appeal'), findsNothing);
    });

    testWidgets('an event decided without the party may still be appealed',
        (tester) async {
      // ESC-29: a decision with no party text leaves the one appeal open;
      // the server says so with canAppeal.
      answer(
        ownReputation(),
        events: [reputationEvent('a', appealStatus: 'upheld', canAppeal: true)],
      );
      await open(tester);

      expect(find.text('Appeal reviewed. The points stay.'), findsOneWidget);
      expect(find.widgetWithText(OutlinedButton, 'Appeal'), findsOneWidget);
    });

    testWidgets('show more loads the next page', (tester) async {
      answer(
        ownReputation(),
        events: [reputationEvent('a')],
        nextToken: 'tok-2',
      );
      when(() => repository.loadEvents('f-1', nextToken: 'tok-2')).thenAnswer(
        (_) async => eventsResult([reputationEvent('b', code: 'NO_SHOW')]),
      );
      await open(tester);

      expect(find.text('Missed the pickup'), findsNothing);
      await tester.tap(find.byKey(const Key('reputation_load_more')));
      await tester.pumpAndSettle();

      expect(find.text('Missed the pickup'), findsOneWidget);
      expect(find.byKey(const Key('reputation_load_more')), findsNothing);
    });

    testWidgets('a next page that fails says so and can be tried again',
        (tester) async {
      answer(
        ownReputation(),
        events: [reputationEvent('a')],
        nextToken: 'tok-2',
      );
      when(() => repository.loadEvents('f-1', nextToken: 'tok-2'))
          .thenThrow(const NetworkException());
      await open(tester);

      await tester.tap(find.byKey(const Key('reputation_load_more')));
      await tester.pumpAndSettle();

      expect(find.text('No internet connection'), findsOneWidget);
      expect(find.byKey(const Key('reputation_load_more')), findsOneWidget);
    });
  });

  group('offline', () {
    testWidgets('the saved copy is shown with its date', (tester) async {
      answer(
        ownReputation(tier: 'warning', points: 4),
        events: [reputationEvent('a')],
        stale: true,
      );
      await open(tester);

      expect(
        find.text("You're offline. Showing what was saved on Nov 7, 2026."),
        findsOneWidget,
      );
      expect(find.text('4 penalty points'), findsOneWidget);
      expect(
        find.text('Cancelled less than a day before pickup'),
        findsOneWidget,
      );
    });

    testWidgets('nothing saved: a clear message and Retry', (tester) async {
      when(() => repository.loadOwn('f-1', force: any(named: 'force')))
          .thenThrow(const NetworkException());
      await open(tester);

      expect(
        find.text('Connect to the internet to see your reputation.'),
        findsOneWidget,
      );
      expect(find.byKey(const Key('reputation_standing')), findsNothing);

      answer(ownReputation());
      await tester.tap(find.text('Retry'));
      await tester.pumpAndSettle();

      expect(find.text('Good standing'), findsOneWidget);
      verify(() => repository.loadOwn('f-1', force: true)).called(1);
    });

    testWidgets('the history alone unavailable keeps the standing',
        (tester) async {
      when(() => repository.loadOwn('f-1', force: any(named: 'force')))
          .thenAnswer((_) async => snapshot(ownReputation()));
      when(() => repository.loadEvents('f-1'))
          .thenThrow(const NetworkException());
      await open(tester);

      expect(find.text('Good standing'), findsOneWidget);
      expect(
        find.text('Connect to the internet to see your history.'),
        findsOneWidget,
      );
    });
  });

  group('appeal', () {
    final appealable = reputationEvent('CANCEL#ord-1', canAppeal: true);

    Future<void> openAppeal(WidgetTester tester) async {
      await tester.tap(find.widgetWithText(OutlinedButton, 'Appeal'));
      await tester.pumpAndSettle();
    }

    testWidgets('an appealable event offers the appeal with its deadline',
        (tester) async {
      answer(ownReputation(tier: 'warning', points: 4), events: [appealable]);
      await open(tester);

      expect(find.text('You can appeal until Nov 8, 2026.'), findsOneWidget);
      await openAppeal(tester);

      expect(find.byKey(const Key('reputation_appeal_dialog')), findsOneWidget);
      expect(
        find.textContaining('You can appeal each record once.'),
        findsOneWidget,
      );
      // Sending needs a connection, and the form says so.
      expect(
        find.textContaining('Sending an appeal needs a connection.'),
        findsOneWidget,
      );
      // Nothing to send yet.
      expect(
        tester
            .widget<FilledButton>(
              find.byKey(const Key('reputation_appeal_send')),
            )
            .onPressed,
        isNull,
      );
    });

    testWidgets('sent: the event shows pending and cannot be appealed again',
        (tester) async {
      answer(ownReputation(tier: 'warning', points: 4), events: [appealable]);
      when(
        () => repository.submitAppeal(
          userId: 'f-1',
          eventId: 'CANCEL#ord-1',
          text: any(named: 'text'),
          photo: any(named: 'photo'),
          photoContentType: any(named: 'photoContentType'),
        ),
      ).thenAnswer((_) async => ReputationAppeal.sent);
      await open(tester);
      await openAppeal(tester);

      await tester.enterText(
        find.byKey(const Key('reputation_appeal_text')),
        'The road to Kombolcha was closed by the flood.',
      );
      await tester.pump();
      await tester.tap(find.byKey(const Key('reputation_appeal_send')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('reputation_appeal_dialog')), findsNothing);
      expect(find.text('Appeal sent.'), findsOneWidget);
      expect(
        find.text('Appeal sent. Our team will review it.'),
        findsOneWidget,
      );
      expect(find.widgetWithText(OutlinedButton, 'Appeal'), findsNothing);
      verify(
        () => repository.submitAppeal(
          userId: 'f-1',
          eventId: 'CANCEL#ord-1',
          text: 'The road to Kombolcha was closed by the flood.',
        ),
      ).called(1);
      // A sent appeal leaves no draft behind.
      verifyNever(() => repository.saveAppealDraft(any(), any(), any()));
    });

    testWidgets('offline: said plainly, the form stays, the text is kept',
        (tester) async {
      answer(ownReputation(tier: 'warning', points: 4), events: [appealable]);
      when(
        () => repository.submitAppeal(
          userId: any(named: 'userId'),
          eventId: any(named: 'eventId'),
          text: any(named: 'text'),
          photo: any(named: 'photo'),
          photoContentType: any(named: 'photoContentType'),
        ),
      ).thenThrow(const AppealException(AppealFailure.notSent));
      await open(tester);
      await openAppeal(tester);

      await tester.enterText(
        find.byKey(const Key('reputation_appeal_text')),
        'The road was closed.',
      );
      await tester.pump();
      await tester.tap(find.byKey(const Key('reputation_appeal_send')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('reputation_appeal_dialog')), findsOneWidget);
      expect(
        find.text(
          'The appeal was not sent. Check your connection and try again.',
        ),
        findsOneWidget,
      );

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      verify(
        () => repository.saveAppealDraft(
          'f-1',
          'CANCEL#ord-1',
          'The road was closed.',
        ),
      ).called(1);
      // Still appealable.
      expect(find.widgetWithText(OutlinedButton, 'Appeal'), findsOneWidget);
    });

    testWidgets('a kept draft comes back', (tester) async {
      answer(ownReputation(tier: 'warning', points: 4), events: [appealable]);
      when(() => repository.readAppealDraft('f-1', 'CANCEL#ord-1'))
          .thenAnswer((_) async => 'The road was closed.');
      await open(tester);
      await openAppeal(tester);

      expect(find.text('The road was closed.'), findsOneWidget);
      expect(
        tester
            .widget<FilledButton>(
              find.byKey(const Key('reputation_appeal_send')),
            )
            .onPressed,
        isNotNull,
      );
    });

    for (final (failure, text) in [
      (AppealFailure.closed, 'This can no longer be appealed.'),
      (AppealFailure.exists, 'An appeal was already sent for this.'),
      (AppealFailure.unavailable, 'Appeals are not available right now.'),
    ]) {
      testWidgets('${failure.name}: "$text"', (tester) async {
        answer(ownReputation(tier: 'warning', points: 4), events: [appealable]);
        when(
          () => repository.submitAppeal(
            userId: any(named: 'userId'),
            eventId: any(named: 'eventId'),
            text: any(named: 'text'),
            photo: any(named: 'photo'),
            photoContentType: any(named: 'photoContentType'),
          ),
        ).thenThrow(AppealException(failure));
        await open(tester);
        await openAppeal(tester);
        await tester.enterText(
          find.byKey(const Key('reputation_appeal_text')),
          'x',
        );
        await tester.pump();
        await tester.tap(find.byKey(const Key('reputation_appeal_send')));
        await tester.pumpAndSettle();

        expect(find.text(text), findsOneWidget);
      });
    }

    testWidgets('a photo is optional: added, removed, sent with the appeal',
        (tester) async {
      final jpeg = Uint8List.fromList([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
      answer(ownReputation(tier: 'warning', points: 4), events: [appealable]);
      when(
        () => repository.submitAppeal(
          userId: any(named: 'userId'),
          eventId: any(named: 'eventId'),
          text: any(named: 'text'),
          photo: any(named: 'photo'),
          photoContentType: any(named: 'photoContentType'),
        ),
      ).thenAnswer((_) async => ReputationAppeal.sent);
      await open(tester, pickPhoto: () async => jpeg);
      await openAppeal(tester);

      await tester.tap(find.byKey(const Key('reputation_appeal_add_photo')));
      await tester.pumpAndSettle();
      expect(find.text('Photo added'), findsOneWidget);

      await tester.tap(find.byKey(const Key('reputation_appeal_remove_photo')));
      await tester.pumpAndSettle();
      expect(find.text('Photo added'), findsNothing);

      await tester.tap(find.byKey(const Key('reputation_appeal_add_photo')));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byKey(const Key('reputation_appeal_text')),
        'See the photo.',
      );
      await tester.pump();
      await tester.tap(find.byKey(const Key('reputation_appeal_send')));
      await tester.pumpAndSettle();

      verify(
        () => repository.submitAppeal(
          userId: 'f-1',
          eventId: 'CANCEL#ord-1',
          text: 'See the photo.',
          photo: jpeg,
        ),
      ).called(1);
    });

    testWidgets('a photo that cannot be used is refused, the appeal can go on',
        (tester) async {
      answer(ownReputation(tier: 'warning', points: 4), events: [appealable]);
      // A PNG: the server takes JPEG and WebP only.
      await open(
        tester,
        pickPhoto: () async =>
            Uint8List.fromList([0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4]),
      );
      await openAppeal(tester);

      await tester.tap(find.byKey(const Key('reputation_appeal_add_photo')));
      await tester.pumpAndSettle();

      expect(find.text('Photo added'), findsNothing);
      expect(
        find.textContaining("This photo is too large or can't be used."),
        findsOneWidget,
      );
      expect(
        find.byKey(const Key('reputation_appeal_add_photo')),
        findsOneWidget,
      );
    });

    testWidgets('no photo picked changes nothing', (tester) async {
      answer(ownReputation(tier: 'warning', points: 4), events: [appealable]);
      await open(tester, pickPhoto: () async => null);
      await openAppeal(tester);

      await tester.tap(find.byKey(const Key('reputation_appeal_add_photo')));
      await tester.pumpAndSettle();

      expect(find.text('Photo added'), findsNothing);
      expect(find.byKey(const Key('reputation_appeal_error')), findsNothing);
    });
  });

  for (final locale in const [Locale('am'), Locale('om')]) {
    testWidgets('${locale.languageCode}: the screen renders without overflow',
        (tester) async {
      answer(
        ownReputation(
          tier: 'restricted',
          points: 11,
          deduction: 1,
          restriction: {
            'since': '2026-11-07T09:00:00.000Z',
            'until': '2026-11-21T09:00:00.000Z',
            'cause': 'points',
          },
          nextPointExpiryAt: '2027-01-20T09:00:00.000Z',
        ),
        events: [
          reputationEvent('a', canAppeal: true, earnedBack: 1),
          reputationEvent('b', code: 'RESPONSE_EXPIRED', points: 0),
          reputationEvent('c', appealStatus: 'upheld', note: 'x'),
        ],
        nextToken: 'tok-2',
      );
      await open(tester, locale: locale);

      expect(tester.takeException(), isNull);
      expect(find.byKey(const Key('reputation_standing')), findsOneWidget);
      // No English left on the screen's own lines.
      expect(find.text('My reputation'), findsNothing);
      expect(find.text('New orders paused'), findsNothing);
    });
  }
}
