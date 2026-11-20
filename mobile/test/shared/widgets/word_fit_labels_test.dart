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
    manrope.addFont(file('BricolageGrotesque-$weight.ttf'));
  }
  final ethiopic = FontLoader(AppTheme.ethiopicFontFallback.first);
  for (final weight in ['Regular', 'SemiBold', 'Bold']) {
    ethiopic.addFont(file('NotoSansEthiopic-$weight.ttf'));
  }
  await Future.wait([manrope.load(), ethiopic.load()]);
}

/// How many lines [boxes] sit on. Boxes of one line overlap in height;
/// their tops alone differ by a pixel or two where a line mixes fonts (an
/// Ethiopic word, then a space from the Latin face).
int _lineCount(List<TextBox> boxes) {
  final sorted = [...boxes]..sort((a, b) => a.top.compareTo(b.top));
  var lines = 0;
  var lineBottom = double.negativeInfinity;
  for (final box in sorted) {
    final middle = (box.top + box.bottom) / 2;
    if (middle > lineBottom) {
      lines++;
      lineBottom = box.bottom;
    } else if (box.bottom > lineBottom) {
      lineBottom = box.bottom;
    }
  }
  return lines;
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
    final lines = _lineCount(
      paragraph.getBoxesForSelection(
        TextSelection(baseOffset: 0, extentOffset: text.length),
      ),
    );
    final words = RegExp(r'\S+').allMatches(text).toList();
    expect(lines, lessThanOrEqualTo(words.length), reason: text);
    for (final word in words) {
      final wordLines = _lineCount(
        paragraph.getBoxesForSelection(
          TextSelection(baseOffset: word.start, extentOffset: word.end),
        ),
      );
      expect(wordLines, 1, reason: '"${word.group(0)}" in "$text"');
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
    // The create button shows its word here and takes more of the bar.
    (width: 390.0, locale: Locale('om'), scale: 1.0),
    (width: 390.0, locale: Locale('am'), scale: 1.3),
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
                // As on the phone: the create button takes its share.
                onCreate: () {},
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

  // Canvas page M, with the fonts the phone draws: the create button
  // shows its word only where every tab keeps its label at full size.
  // (With the word always shown at 390 dp, the Afaan Oromoo trucker's
  // "Imala Galmeessi" squeezed "Geejjibaalee" to 70% of its 12 px.)
  group('the bar\'s create word and its tabs, real fonts', () {
    Future<void> pumpBar(
      WidgetTester tester,
      UserRole role, {
      required double width,
      required Locale locale,
      double scale = 1.0,
    }) async {
      window(tester, width);
      await tester.pumpWidget(
        host(
          Align(
            alignment: Alignment.bottomCenter,
            child: RoleNavBar(
              role: role,
              tabs: tabsForRole(role),
              currentIndex: 0,
              onSelected: (_) {},
              onCreate: () {},
            ),
          ),
          locale: locale,
          scale: scale,
        ),
      );
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
    }

    bool wordShown(WidgetTester tester) =>
        tester
            .getSize(
              find.descendant(
                of: find.byType(RoleCreateButton),
                matching: find.byType(ClipRect),
              ),
            )
            .width >
        0;

    /// Every tab is a 48 px target and no label was scaled down to fit.
    void expectTabsWhole(
      WidgetTester tester, {
      bool unshrunk = true,
      double minWidth = 48,
    }) {
      for (final item in find.byType(RoleNavItem).evaluate()) {
        final finder = find.byWidget(item.widget);
        final size = tester.getSize(finder);
        expect(size.width, greaterThanOrEqualTo(minWidth));
        expect(size.height, greaterThanOrEqualTo(48));
        if (!unshrunk) continue;
        final label = find.descendant(of: finder, matching: find.byType(Text));
        final fit = find.ancestor(
          of: label,
          matching: find.byWidgetPredicate(
            (w) => w is SingleChildRenderObjectWidget && w is! Semantics,
          ),
        );
        // The word-fit box is as wide as its text: a shrunk label is
        // laid out wider than the box that shows it.
        expect(
          tester.getSize(label).width,
          lessThanOrEqualTo(tester.getSize(fit.first).width + 0.01),
          reason: tester.widget<Text>(label).data,
        );
      }
    }

    // The boards' phone: the word shows for every role in English and
    // Amharic, and nothing is squeezed.
    for (final width in const [390.0, 412.0]) {
      for (final locale in const [Locale('en'), Locale('am')]) {
        for (final role in UserRole.values) {
          testWidgets(
              '${width.toInt()} dp, ${role.name} in ${locale.languageCode}: '
              'the word shows, the labels keep their size', (tester) async {
            await pumpBar(tester, role, width: width, locale: locale);
            expect(wordShown(tester), isTrue);
            expectTabsWhole(tester);
          });
        }
      }
    }

    // The commonest small phone still names the farmer's and the buyer's
    // verb: an icon-only plus assumes app-convention literacy.
    for (final role in const [UserRole.farmer, UserRole.buyer]) {
      testWidgets('360 dp, ${role.name} in en: the word shows', (tester) async {
        await pumpBar(tester, role, width: 360, locale: const Locale('en'));
        expect(wordShown(tester), isTrue);
        expectTabsWhole(tester);
      });
    }

    testWidgets(
        '390 dp, trucker in om: "Imala Galmeessi" gives way, '
        '"Geejjibaalee" keeps its size', (tester) async {
      await pumpBar(
        tester,
        UserRole.trucker,
        width: 390,
        locale: const Locale('om'),
      );
      expect(wordShown(tester), isFalse);
      expectTabsWhole(tester);
      // The plus still has its name.
      final om = lookupAppLocalizations(const Locale('om'));
      expect(find.byTooltip(om.postTrip), findsOneWidget);
    });

    // Large text and 200% zoom: the plus alone, no overflow. Three tabs
    // and the plus at 48 px each need 246 px of window: a 390 px window
    // at 200% zoom is 195 px wide, where the tabs keep 48 px of height
    // and WCAG's 24 px of width.
    for (final (name, width, scale, minTab) in [
      ('200% text', 390.0, 2.0, 48.0),
      ('200% text on a small phone', 360.0, 2.0, 48.0),
      ('the narrowest phone', 320.0, 1.0, 48.0),
      ('200% zoom', 195.0, 1.0, 24.0),
    ]) {
      for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
        for (final role in UserRole.values) {
          testWidgets(
              '$name, ${role.name} in ${locale.languageCode}: tabs and '
              'the create button keep their targets', (tester) async {
            await pumpBar(
              tester,
              role,
              width: width,
              locale: locale,
              scale: scale,
            );
            // Large text or zoom: the plus alone. (At 320 px the word
            // still shows where it fits beside whole labels.)
            if (width != 320) expect(wordShown(tester), isFalse);
            // A tab narrower than its longest word shrinks that label,
            // the only case, and never breaks it.
            expectTabsWhole(tester, unshrunk: false, minWidth: minTab);
            final create = tester.getSize(find.byType(RoleCreateButton));
            expect(create.width, greaterThanOrEqualTo(48));
            expect(create.height, greaterThanOrEqualTo(48));
          });
        }
      }
    }
  });

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
            onCreate: () {},
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
