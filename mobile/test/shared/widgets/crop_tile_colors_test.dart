import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/widgets/listing_card.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// The boards' crop tiles: Market (wheat cream, maize green, teff sand) and
/// My Crops (chickpea on the olive tint), each with the same sprout.
void main() {
  test('each crop gets its board colours', () {
    expect(cropTileColors('wheat').background, const Color(0xFFF4EBCF));
    expect(cropTileColors('wheat').foreground, AppTheme.money);
    expect(cropTileColors('teff').background, const Color(0xFFEFE6D8));
    expect(cropTileColors('teff').foreground, AppTheme.money);
    expect(cropTileColors('chickpea').background, const Color(0xFFE6EFD9));
    expect(cropTileColors('chickpea').foreground, AppTheme.fieldGreen);
    expect(cropTileColors('maize').background, AppTheme.fieldGreenTint);
  });

  test('an unknown or missing crop is on field green tint', () {
    expect(cropTileColors(null).background, AppTheme.fieldGreenTint);
    expect(cropTileColors('quinoa').foreground, AppTheme.fieldGreen);
  });

  testWidgets('a listing card draws the sprout at 24 px in its tile',
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
    final sprout = find.byType(FwSprout);
    expect(sprout, findsOneWidget);
    expect(tester.widget<FwSprout>(sprout).color, AppTheme.money);
    expect(
      tester.getSize(
        find.descendant(of: sprout, matching: find.byType(CustomPaint)),
      ),
      const Size(24, 24),
    );
  });
}
