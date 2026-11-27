import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/widgets/listing_card.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/crop_glyph.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// The boards' crop tiles: Market (wheat on the orange tint, maize on the
/// green tint, teff on sand), each with the same sprout.
void main() {
  test('each crop gets its board colours', () {
    expect(cropTileColors('wheat').background, AppTheme.orangeTint);
    expect(cropTileColors('wheat').foreground, AppTheme.orangeInk);
    expect(cropTileColors('teff').background, AppTheme.sand);
    expect(cropTileColors('teff').foreground, AppTheme.inkSoft);
    expect(cropTileColors('chickpea').background, AppTheme.segmentTrack);
    expect(cropTileColors('chickpea').foreground, AppTheme.inkSoft);
    expect(cropTileColors('maize').background, AppTheme.statusSuccessContainer);
  });

  test('an unknown or missing crop is on the green tint', () {
    expect(
      cropTileColors(null).background,
      AppTheme.statusSuccessContainer,
    );
    expect(cropTileColors('quinoa').foreground, AppTheme.statusSuccess);
  });

  testWidgets('a listing card draws the crop glyph at 24 px in its tile',
      (tester) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: ListingCard(
            title: 'Teff · 12 quintals left',
            location: 'Koka',
            sellerName: 'Tolosa',
            price: '118',
            cropKey: 'teff',
          ),
        ),
      ),
    );
    final sprout = find.byType(CropGlyph);
    expect(sprout, findsOneWidget);
    expect(tester.widget<CropGlyph>(sprout).cropKey, 'teff');
    expect(tester.widget<CropGlyph>(sprout).color, AppTheme.inkSoft);
    expect(
      tester.getSize(
        find.descendant(of: sprout, matching: find.byType(CustomPaint)),
      ),
      const Size(24, 24),
    );
  });
}
