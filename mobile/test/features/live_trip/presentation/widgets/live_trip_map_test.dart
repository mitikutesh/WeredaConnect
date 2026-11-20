import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/presentation/map/track_map_painter.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

import '../../live_trip_fixtures.dart';

/// Buyer live trip board: the check-in chip sits beside the truck, inside
/// the map and clear of the controls floating over its top; at the top left
/// when it has no room there.
void main() {
  const map = Size(390, 440);
  const chip = Size(150, 42);

  CheckInChipPlacement placement(
    Offset? truck, {
    double top = 76,
    TextDirection direction = TextDirection.ltr,
  }) =>
      CheckInChipPlacement(truck: truck, top: top, textDirection: direction);

  group('where the chip goes', () {
    test('after the truck, level with it', () {
      const truck = Offset(120, 220);
      final at = placement(truck).place(map, chip);
      // The truck's half side (15) and the board's gap (8).
      expect(at, const Offset(120 + 15 + 8, 220 - 21));
    });

    test('before the truck when the map ends after it', () {
      const truck = Offset(330, 220);
      final at = placement(truck).place(map, chip);
      expect(at, const Offset(330 - 15 - 8 - 150, 220 - 21));
    });

    test('before the truck first in a right-to-left layout', () {
      const truck = Offset(200, 220);
      final at =
          placement(truck, direction: TextDirection.rtl).place(map, chip);
      expect(at.dx + chip.width, lessThan(truck.dx));
    });

    test('under the truck when neither side has room', () {
      const narrow = Size(200, 440);
      const truck = Offset(100, 200);
      final at = placement(truck).place(narrow, chip);
      expect(at, const Offset(100 - 75, 200 + 15 + 8));
    });

    test('never above the free area: a truck under the controls', () {
      // Level with the truck the chip would reach over the top row.
      const truck = Offset(120, 80);
      final at = placement(truck).place(map, chip);
      expect(at.dy, greaterThanOrEqualTo(76));
      expect(
        (at & chip).overlaps(Rect.fromCircle(center: truck, radius: 15)),
        isFalse,
      );
    });

    test('the top left when it fits nowhere beside the truck', () {
      const tiny = Size(180, 150);
      const truck = Offset(90, 100);
      expect(placement(truck).place(tiny, chip), const Offset(12, 76));
    });

    test('the top left without a truck', () {
      expect(placement(null).place(map, chip), const Offset(12, 76));
      expect(
        placement(null, direction: TextDirection.rtl).place(map, chip),
        const Offset(390 - 12 - 150, 76),
      );
    });

    test('whole inside the map wherever the truck is', () {
      for (var x = 0.0; x <= map.width; x += 30) {
        for (var y = 0.0; y <= map.height; y += 40) {
          final at = placement(Offset(x, y)).place(map, chip);
          final rect = at & chip;
          expect(rect.left, greaterThanOrEqualTo(12));
          expect(rect.top, greaterThanOrEqualTo(76));
          expect(rect.right, lessThanOrEqualTo(map.width - 12));
          expect(rect.bottom, lessThanOrEqualTo(map.height - 12));
        }
      }
    });

    test('a new truck place lays the chip out again', () {
      expect(
        placement(const Offset(1, 2)).shouldRelayout(
          placement(const Offset(1, 2)),
        ),
        isFalse,
      );
      expect(
        placement(const Offset(1, 2)).shouldRelayout(
          placement(const Offset(3, 2)),
        ),
        isTrue,
      );
    });
  });

  group('on the map', () {
    Future<void> pumpMap(
      WidgetTester tester, {
      bool besideTruck = true,
      TrackMapFocus focus = TrackMapFocus.wholeRoute,
      double width = 390,
    }) async {
      tester.view.physicalSize = Size(width, 600);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light(),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: Align(
              alignment: Alignment.topCenter,
              child: LiveTripMap(
                track: corridorTrack(),
                now: at(9, 0),
                places: corridorPlaces,
                height: 440,
                borderRadius: 0,
                chipTop: 76,
                chipBesideTruck: besideTruck,
                focus: focus,
              ),
            ),
          ),
        ),
      );
      await tester.pump();
    }

    Offset truckOf(WidgetTester tester, TrackMapFocus focus) =>
        TrackMapFrame.of(
          track: corridorTrack(),
          now: at(9, 0),
          size: tester.getSize(find.byType(LiveTripMap)),
          focus: focus,
        )!
            .truck!;

    for (final focus in TrackMapFocus.values) {
      testWidgets('${focus.name}: the chip touches the truck, not the corner',
          (tester) async {
        await pumpMap(tester, focus: focus);
        final chip = tester.getRect(find.byType(CheckInChip));
        final truck = truckOf(tester, focus);
        final square = Rect.fromCircle(center: truck, radius: 15);
        expect(chip.overlaps(square), isFalse);
        // 8 px from the truck's square, on one of its sides.
        expect(chip.inflate(8.5).overlaps(square), isTrue);
        expect(chip.top, greaterThanOrEqualTo(76));
        expect(chip.left, greaterThanOrEqualTo(12));
        expect(chip.right, lessThanOrEqualTo(390 - 12));
        expect(chip.bottom, lessThanOrEqualTo(440 - 12));
      });
    }

    testWidgets('the web board keeps the chip at the top left', (tester) async {
      await pumpMap(tester, besideTruck: false);
      expect(
        tester.getTopLeft(find.byType(CheckInChip)),
        const Offset(12, 76),
      );
    });

    testWidgets('a narrow map still shows the chip whole', (tester) async {
      await pumpMap(tester, width: 320);
      final chip = tester.getRect(find.byType(CheckInChip));
      expect(chip.left, greaterThanOrEqualTo(12));
      expect(chip.right, lessThanOrEqualTo(320 - 12));
      expect(tester.takeException(), isNull);
    });
  });
}
