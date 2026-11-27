import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// MarketM at 390 px with the app's own fonts and its real title in each
/// language: the buyer sketch shows under the bell and the account button
/// when "Find Crops" leaves its line room, and hides when the title is
/// long. Whether it fits depends on how wide the words really are, so the
/// fonts are loaded.
Future<void> _loadFonts() async {
  Future<ByteData> file(String name) async =>
      ByteData.sublistView(File('assets/fonts/$name').readAsBytesSync());
  final latin = FontLoader(AppTheme.fontFamily);
  for (final weight in ['Medium', 'Bold', 'ExtraBold']) {
    latin.addFont(file('BricolageGrotesque-$weight.ttf'));
  }
  final ethiopic = FontLoader(AppTheme.ethiopicFontFallback.first);
  for (final weight in ['Regular', 'Bold']) {
    ethiopic.addFont(file('NotoSansEthiopic-$weight.ttf'));
  }
  await Future.wait([latin.load(), ethiopic.load()]);
}

void main() {
  setUpAll(_loadFonts);

  const buttons = Key('buttons');

  Future<String> pump(
    WidgetTester tester,
    String language, {
    bool below = true,
  }) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final l10n = lookupAppLocalizations(Locale(language));
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(role: UserRole.buyer),
        home: Scaffold(
          body: FwPageHeader(
            eyebrow: '${l10n.deliverTo} Merkato',
            title: l10n.findCrops,
            sketchRole: UserRole.buyer,
            sketchBelowTrailing: below,
            // The bell and the account button: two 44 px circles.
            trailing: const Row(
              key: buttons,
              mainAxisSize: MainAxisSize.min,
              children: [
                SizedBox(width: 44, height: 44),
                SizedBox(width: 8),
                SizedBox(width: 44, height: 44),
              ],
            ),
          ),
        ),
      ),
    );
    expect(tester.takeException(), isNull);
    return l10n.findCrops;
  }

  Finder drawing() => find.descendant(
        of: find.byType(RoleSketch),
        matching: find.byType(CustomPaint),
      );

  testWidgets('English: beside the title alone the sketch has no room',
      (tester) async {
    await pump(tester, 'en', below: false);
    expect(drawing(), findsNothing);
  });

  testWidgets('English: the sketch shows under the two buttons',
      (tester) async {
    final title = await pump(tester, 'en');
    expect(drawing(), findsOneWidget);
    final sketch = tester.getRect(drawing());
    final text = tester.getRect(find.text(title));
    final trailing = tester.getRect(find.byKey(buttons));
    expect(sketch.top, greaterThanOrEqualTo(trailing.bottom));
    expect(sketch.left, greaterThan(text.right));
    expect(sketch.left, greaterThanOrEqualTo(20 + 350 * 0.65));
    expect(trailing.right, 370);
    // One line of the 42 px title.
    expect(text.height, lessThan(60));
  });

  testWidgets('Afaan Oromo: the long title drops the sketch', (tester) async {
    final title = await pump(tester, 'om');
    expect(drawing(), findsNothing);
    final text = tester.getRect(find.text(title));
    final trailing = tester.getRect(find.byKey(buttons));
    expect(text.right, lessThanOrEqualTo(trailing.left));
  });

  testWidgets('Amharic: the sketch never sits under or over the title',
      (tester) async {
    final title = await pump(tester, 'am');
    final text = tester.getRect(find.text(title));
    if (drawing().evaluate().isNotEmpty) {
      expect(tester.getRect(drawing()).left, greaterThan(text.right));
    }
    expect(text.height, lessThan(70));
  });
}
