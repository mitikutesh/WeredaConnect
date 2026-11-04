import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart' show Intl;
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_nav_bar.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/language_segmented_picker.dart';

/// Short labels wrap at spaces and never inside a word (review of the a11y
/// pass): with the bundled fonts, "Geejjibaalee" (Deliveries in Afaan
/// Oromoo) broke into "Geejjibaale|e" at 360 dp and 1.15x, and "Deliver|
/// ies" at 2.0x in English. The test fonts would hide this, so the real
/// ones are loaded.
Future<void> _loadFonts() async {
  Future<ByteData> file(String name) async =>
      ByteData.sublistView(File('assets/fonts/$name').readAsBytesSync());
  final manrope = FontLoader(AppTheme.fontFamily);
  for (final weight in [
    'Regular',
    'Medium',
    'SemiBold',
    'Bold',
    'ExtraBold',
  ]) {
    manrope.addFont(file('Manrope-$weight.ttf'));
  }
  final ethiopic = FontLoader(AppTheme.ethiopicFontFallback.first);
  for (final weight in ['Regular', 'SemiBold', 'Bold']) {
    ethiopic.addFont(file('NotoSansEthiopic-$weight.ttf'));
  }
  await Future.wait([manrope.load(), ethiopic.load()]);
}

/// Every word of every label sits on one line, and no label takes more
/// lines than it has words.
void _expectNoWordBroken(WidgetTester tester, Finder labels) {
  final paragraphs = tester
      .renderObjectList<RenderParagraph>(labels)
      .where((p) => p.text.toPlainText().trim().isNotEmpty)
      .toList();
  expect(paragraphs, isNotEmpty);
  for (final paragraph in paragraphs) {
    final text = paragraph.text.toPlainText();
    final lineTops = paragraph
        .getBoxesForSelection(
          TextSelection(baseOffset: 0, extentOffset: text.length),
        )
        .map((box) => box.top.round())
        .toSet();
    final words = RegExp(r'\S+').allMatches(text).toList();
    expect(lineTops.length, lessThanOrEqualTo(words.length), reason: text);
    for (final word in words) {
      final tops = paragraph
          .getBoxesForSelection(
            TextSelection(baseOffset: word.start, extentOffset: word.end),
          )
          .map((box) => box.top.round())
          .toSet();
      expect(tops, hasLength(1), reason: '"${word.group(0)}" in "$text"');
    }
    expect(paragraph.didExceedMaxLines, isFalse, reason: text);
  }
}

void main() {
  setUpAll(_loadFonts);
  tearDown(() => Intl.defaultLocale = null);

  Widget host(
    Widget child, {
    required Locale locale,
    required double scale,
  }) {
    return MaterialApp(
      theme: AppTheme.light(),
      locale: locale,
      localizationsDelegates: const [
        AppLocalizations.delegate,
        FallbackMaterialLocalizationsDelegate(),
        GlobalWidgetsLocalizations.delegate,
        FallbackCupertinoLocalizationsDelegate(),
      ],
      supportedLocales: AppLocalizations.supportedLocales,
      home: Builder(
        builder: (context) => MediaQuery(
          data: MediaQuery.of(context).copyWith(
            textScaler: TextScaler.linear(scale),
          ),
          child: Scaffold(body: child),
        ),
      ),
    );
  }

  void window(WidgetTester tester, double width) {
    tester.view.physicalSize = Size(width, 800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  const cases = [
    (width: 360.0, locale: Locale('om'), scale: 1.15),
    (width: 360.0, locale: Locale('om'), scale: 1.3),
    (width: 360.0, locale: Locale('en'), scale: 2.0),
    (width: 360.0, locale: Locale('am'), scale: 2.0),
    (width: 320.0, locale: Locale('om'), scale: 1.0),
  ];

  for (final c in cases) {
    final variant =
        '${c.width.toInt()} dp, ${c.locale.languageCode} at ${c.scale}x';

    for (final role in UserRole.values) {
      testWidgets('nav labels, ${role.name}, $variant', (tester) async {
        window(tester, c.width);
        await tester.pumpWidget(
          host(
            Align(
              alignment: Alignment.bottomCenter,
              child: RoleNavBar(
                role: role,
                tabs: tabsForRole(role),
                currentIndex: 0,
                onSelected: (_) {},
              ),
            ),
            locale: c.locale,
            scale: c.scale,
          ),
        );
        await tester.pumpAndSettle();
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        _expectNoWordBroken(
          tester,
          find.descendant(
            of: find.byType(RoleNavBar),
            matching: find.byType(RichText),
          ),
        );
      });
    }

    testWidgets('FwSegmented labels, $variant', (tester) async {
      window(tester, c.width);
      final l10n = await AppLocalizations.delegate.load(c.locale);
      await tester.pumpWidget(
        host(
          Padding(
            padding: const EdgeInsets.all(20),
            child: FwSegmented<int>(
              segments: [
                FwSegment(value: 0, label: l10n.delivSegmentActive, count: 3),
                FwSegment(value: 1, label: l10n.deliveryFilterDelivered),
                FwSegment(value: 2, label: l10n.deliveryFilterAll),
              ],
              selected: 0,
              onChanged: (_) {},
            ),
          ),
          locale: c.locale,
          scale: c.scale,
        ),
      );
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      _expectNoWordBroken(
        tester,
        find.descendant(
          of: find.byType(FwSegmented<int>),
          matching: find.byType(RichText),
        ),
      );
    });

    testWidgets('language picker, $variant', (tester) async {
      SharedPreferences.setMockInitialValues({});
      window(tester, c.width);
      await tester.pumpWidget(
        ChangeNotifierProvider<LanguageProvider>(
          create: (_) => LanguageProvider(),
          child: host(
            const Padding(
              padding: EdgeInsets.all(40),
              child: LanguageSegmentedPicker(),
            ),
            locale: c.locale,
            scale: c.scale,
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      for (final name in ['English', 'Afaan Oromoo']) {
        _expectNoWordBroken(tester, find.text(name));
      }
    });
  }

  testWidgets('a word that fits keeps its size: no shrinking needed',
      (tester) async {
    window(tester, 390);
    await tester.pumpWidget(
      host(
        Align(
          alignment: Alignment.bottomCenter,
          child: RoleNavBar(
            role: UserRole.farmer,
            tabs: tabsForRole(UserRole.farmer),
            currentIndex: 0,
            onSelected: (_) {},
          ),
        ),
        locale: const Locale('en'),
        scale: 1.0,
      ),
    );
    await tester.pumpAndSettle();
    final home = tester.getSize(find.text('Home'));
    final painter = TextPainter(
      text: TextSpan(
        text: 'Home',
        style: AppTheme.light().textTheme.bodyMedium!.merge(
              const TextStyle(fontSize: 12, fontWeight: FontWeight.w700),
            ),
      ),
      textDirection: TextDirection.ltr,
    )..layout();
    expect(home.width, closeTo(painter.width, 1));
    painter.dispose();
  });
}
