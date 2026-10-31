import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/presentation/widgets/pause_sheet.dart';

import 'live_trip_ui_fixtures.dart';

/// LT-09: the pause sheet. 15 min, 30 min, 1 hour, and overnight until 06:00
/// only while that fits in the 18 hours the engine accepts; times on the
/// viewer's clock.
void main() {
  // 22:00 in Ethiopia: 06:00 is 8 hours away.
  final evening = DateTime.utc(2026, 10, 28, 19);
  // 08:00 in Ethiopia: 06:00 is 22 hours away.
  final morning = DateTime.utc(2026, 10, 28, 5);

  group('PauseChoice', () {
    test('the next 06:00 in Ethiopia', () {
      expect(PauseChoice.nextMorning(evening), DateTime.utc(2026, 10, 29, 3));
      expect(PauseChoice.nextMorning(morning), DateTime.utc(2026, 10, 29, 3));
      // 05:00 in Ethiopia: the same morning.
      expect(
        PauseChoice.nextMorning(DateTime.utc(2026, 10, 28, 2)),
        DateTime.utc(2026, 10, 28, 3),
      );
    });

    test('overnight is offered from 12:00 in the day, never over 18 hours', () {
      // 12:00 in Ethiopia: exactly 18 hours.
      expect(
        PauseChoice.overnightOffered(DateTime.utc(2026, 10, 28, 9)),
        isTrue,
      );
      expect(
        PauseChoice.overnightOffered(DateTime.utc(2026, 10, 28, 8, 59)),
        isFalse,
      );
      expect(PauseChoice.overnight.untilFrom(morning), isNull);
      expect(PauseChoice.offeredAt(morning), hasLength(3));
      expect(PauseChoice.offeredAt(evening), PauseChoice.values);
    });

    test('durations count from the moment of the choice', () {
      expect(
        PauseChoice.minutes15.untilFrom(evening),
        evening.add(const Duration(minutes: 15)),
      );
      expect(
        PauseChoice.hour1.untilFrom(evening),
        evening.add(const Duration(hours: 1)),
      );
    });
  });

  group('sheet', () {
    Future<List<PauseChoice?>> open(
      WidgetTester tester,
      DateTime now, {
      String locale = 'en',
    }) async {
      final results = <PauseChoice?>[];
      await pumpApp(
        tester,
        Builder(
          builder: (context) => Center(
            child: TextButton(
              onPressed: () async =>
                  results.add(await showPauseSheet(context, now: now)),
              child: const Text('open'),
            ),
          ),
        ),
        locale: locale,
      );
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      return results;
    }

    testWidgets('in the evening all four choices, 30 minutes picked',
        (tester) async {
      final results = await open(tester, evening);
      final l10n = lookup('en');

      expect(find.text(l10n.pauseSheetTitle), findsOneWidget);
      // Each choice says on its own line when sharing comes back (the
      // board), the overnight one in its label.
      expect(find.textContaining(l10n.pauseSheet15), findsOneWidget);
      expect(
        find.text('${l10n.pauseSheet30} · ${l10n.pauseSheetBackAt('22:30')}'),
        findsOneWidget,
      );
      expect(find.textContaining(l10n.pauseSheet60), findsOneWidget);
      expect(find.text(l10n.pauseSheetOvernight('06:00')), findsOneWidget);

      await tester.tap(find.byKey(const Key('pause_confirm')));
      await tester.pumpAndSettle();
      expect(results, [PauseChoice.minutes30]);
    });

    testWidgets('in the morning overnight is not offered', (tester) async {
      await open(tester, morning);

      expect(find.byKey(const Key('pause_overnight')), findsNothing);
      expect(find.byKey(const Key('pause_hour1')), findsOneWidget);
    });

    testWidgets('a pick is returned; Keep sharing returns nothing',
        (tester) async {
      final results = await open(tester, evening);

      await tester.tap(find.byKey(const Key('pause_overnight')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('pause_confirm')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('pause_keep')));
      await tester.pumpAndSettle();

      expect(results, [PauseChoice.overnight, null]);
    });

    testWidgets('Amharic: the resume times on the Ethiopian clock',
        (tester) async {
      await open(tester, evening, locale: 'am');
      final l10n = lookup('am');

      expect(
        find.text(l10n.pauseSheetOvernight('ከጠዋቱ 12:00')),
        findsOneWidget,
      );
      expect(
        find.textContaining(l10n.pauseSheetBackAt('ከምሽቱ 4:30')),
        findsOneWidget,
      );
    });

    testWidgets('Afaan Oromo: the resume times on the Ethiopian clock',
        (tester) async {
      await open(tester, evening, locale: 'om');
      final l10n = lookup('om');

      expect(
        find.text(l10n.pauseSheetOvernight('12:00 ganama')),
        findsOneWidget,
      );
      expect(
        find.textContaining(l10n.pauseSheetBackAt('4:30 galgala')),
        findsOneWidget,
      );
    });
  });
}
