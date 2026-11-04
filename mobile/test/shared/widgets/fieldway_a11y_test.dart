import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_nav_bar.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/language_segmented_picker.dart';

/// Tap targets and large text on the shared Fieldway pieces (audit Fix
/// 10): 48 dp hit areas that keep the 44 or 40 dp look, labels that wrap
/// instead of shrinking, and a header and create button that make room at
/// 200% zoom.
void main() {
  Widget host(
    Widget child, {
    Size size = const Size(390, 844),
    double textScale = 1.0,
  }) {
    return MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: MediaQuery(
        data: MediaQueryData(
          size: size,
          textScaler: TextScaler.linear(textScale),
        ),
        child: Scaffold(body: child),
      ),
    );
  }

  void window(WidgetTester tester, Size size) {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  group('FwTapTarget', () {
    testWidgets('a tap on the margin reaches the control', (tester) async {
      var taps = 0;
      await tester.pumpWidget(
        host(
          Center(
            child: FwTapTarget(
              onTap: () => taps++,
              child: const SizedBox(width: 30, height: 30),
            ),
          ),
        ),
      );
      final box = tester.getRect(find.byType(FwTapTarget));
      expect(box.size, const Size(48, 48));

      // 2 px inside the 48 box, 7 px outside the 30 px child.
      await tester.tapAt(box.topLeft + const Offset(2, 2));
      expect(taps, 1);
    });
  });

  testWidgets('FwSquareButton: 44 to see, 48 to hit, one labelled button',
      (tester) async {
    final semantics = tester.ensureSemantics();
    var taps = 0;
    await tester.pumpWidget(
      host(
        Center(
          child: FwSquareButton(
            semanticLabel: 'Notifications',
            onTap: () => taps++,
            child: const Icon(Icons.notifications_outlined),
          ),
        ),
      ),
    );
    expect(
      tester.getSize(
        find.descendant(
          of: find.byType(FwSquareButton),
          matching: find.byType(Material),
        ),
      ),
      const Size(44, 44),
    );
    final node = tester.getSemantics(find.bySemanticsLabel('Notifications'));
    expect(node.rect.size, const Size(48, 48));
    expect(
      node,
      matchesSemantics(
        label: 'Notifications',
        isButton: true,
        isFocusable: true,
        hasTapAction: true,
        hasFocusAction: true,
      ),
    );

    await tester.tapAt(
      tester.getRect(find.byType(FwSquareButton)).bottomRight -
          const Offset(1, 1),
    );
    expect(taps, 1);
    semantics.dispose();
  });

  testWidgets('FwFilterPill: 40 tall pill in a 48 tall hit area',
      (tester) async {
    var taps = 0;
    await tester.pumpWidget(
      host(FwFilterPill(label: 'All', selected: false, onTap: () => taps++)),
    );
    expect(tester.getSize(find.byType(FwFilterPill)).height, 48);
    expect(
      tester
          .getSize(
            find.descendant(
              of: find.byType(FwFilterPill),
              matching: find.byType(Material),
            ),
          )
          .height,
      40,
    );
    await tester.tapAt(
      tester.getRect(find.byType(FwFilterPill)).topCenter + const Offset(0, 1),
    );
    expect(taps, 1);
  });

  testWidgets('FwSegmented: each segment hits 48 tall, long labels wrap',
      (tester) async {
    window(tester, const Size(390, 844));
    final semantics = tester.ensureSemantics();
    String? picked;
    await tester.pumpWidget(
      host(
        Padding(
          padding: const EdgeInsets.all(20),
          child: FwSegmented<String>(
            segments: const [
              FwSegment(value: 'a', label: 'Active'),
              FwSegment(value: 'b', label: 'Kan Gurguramuuf Qophaaʼe'),
            ],
            selected: 'a',
            onChanged: (value) => picked = value,
          ),
        ),
        textScale: 2.0,
      ),
    );
    expect(tester.takeException(), isNull);
    final node = tester.getSemantics(find.text('Active'));
    expect(node.rect.height, greaterThanOrEqualTo(48));
    final long = tester.widget<Text>(find.text('Kan Gurguramuuf Qophaaʼe'));
    expect(long.maxLines, 2);

    await tester.tap(find.text('Kan Gurguramuuf Qophaaʼe'));
    expect(picked, 'b');
    semantics.dispose();
  });

  testWidgets('FwSegmented: a label on two lines, the others keep its height',
      (tester) async {
    window(tester, const Size(390, 844));
    await tester.pumpWidget(
      host(
        // In a Column, as on the screens: no height to stretch into.
        Column(
          children: [
            Padding(
              padding: const EdgeInsets.all(20),
              child: FwSegmented<String>(
                segments: const [
                  FwSegment(value: 'a', label: 'Active'),
                  FwSegment(value: 'b', label: 'Sold Out'),
                ],
                selected: 'a',
                onChanged: (_) {},
              ),
            ),
          ],
        ),
        textScale: 2.0,
      ),
    );
    expect(tester.takeException(), isNull);
    // "Sold Out" wraps, "Active" does not: both segments still fill the
    // track's height (they were 74, 74 and 40 tall on Deliveries).
    final heights = [
      for (final label in ['Active', 'Sold Out'])
        tester
            .getSize(
              find
                  .ancestor(
                    of: find.text(label),
                    matching: find.byType(Material),
                  )
                  .first,
            )
            .height,
    ];
    expect(heights[1], greaterThan(48));
    expect(heights[0], heights[1]);
  });

  group('RoleNavBar labels', () {
    testWidgets('12 px, up to two lines, no FittedBox, a tap action',
        (tester) async {
      window(tester, const Size(360, 800));
      final semantics = tester.ensureSemantics();
      var selected = -1;
      await tester.pumpWidget(
        host(
          Align(
            alignment: Alignment.bottomCenter,
            child: RoleNavBar(
              role: UserRole.farmer,
              tabs: tabsForRole(UserRole.farmer),
              currentIndex: 0,
              onSelected: (i) => selected = i,
            ),
          ),
          textScale: 2.0,
        ),
      );
      expect(tester.takeException(), isNull);
      expect(
        find.descendant(
          of: find.byType(RoleNavBar),
          matching: find.byType(FittedBox),
        ),
        findsNothing,
      );
      final label = tester.widget<Text>(find.text('Deliveries'));
      expect(label.style!.fontSize, 12);
      expect(label.maxLines, 2);

      // Screen readers can open a tab (excludeSemantics had dropped the
      // InkWell's tap action).
      final node = tester.getSemantics(find.bySemanticsLabel('Deliveries'));
      expect(
        node.getSemanticsData().hasAction(SemanticsAction.tap),
        isTrue,
      );
      tester.semantics.tap(find.semantics.byLabel('Deliveries'));
      expect(selected, 3);
      semantics.dispose();
    });
  });

  group('LanguageSegmentedPicker', () {
    setUp(() => SharedPreferences.setMockInitialValues({}));

    testWidgets('48 tall targets that wrap at 200% and switch on tap',
        (tester) async {
      window(tester, const Size(320, 800));
      final semantics = tester.ensureSemantics();
      final language = LanguageProvider();
      await tester.pumpWidget(
        ChangeNotifierProvider<LanguageProvider>.value(
          value: language,
          child: host(
            const Padding(
              padding: EdgeInsets.all(16),
              child: LanguageSegmentedPicker(),
            ),
            size: const Size(320, 800),
            textScale: 2.0,
          ),
        ),
      );
      expect(tester.takeException(), isNull);
      expect(find.byType(FittedBox), findsNothing);

      final oromo = tester.getSemantics(find.bySemanticsLabel('Afaan Oromoo'));
      expect(oromo.rect.height, greaterThanOrEqualTo(48));
      expect(
        oromo.getSemanticsData().hasAction(SemanticsAction.tap),
        isTrue,
      );

      await tester.tap(find.text('Afaan Oromoo'));
      await tester.pump();
      expect(language.locale.languageCode, 'om');
      semantics.dispose();
    });
  });

  group('page header at 200% zoom', () {
    Widget header() => FwPageHeader(
          eyebrow: 'Meki · Oromia',
          title: 'Welcome back, Almaz',
          trailing: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              FwSquareButton(
                semanticLabel: 'Notifications',
                onTap: () {},
                child: const Icon(Icons.notifications_outlined),
              ),
            ],
          ),
        );

    testWidgets('a phone keeps the buttons beside the title', (tester) async {
      window(tester, const Size(390, 844));
      await tester.pumpWidget(host(header()));
      final title = tester.getRect(find.text('Welcome back, Almaz'));
      final bell = tester.getRect(find.byType(FwSquareButton));
      expect(bell.left, greaterThan(title.right));
      expect(bell.top, lessThan(title.bottom));
    });

    // A 390 px phone at 200% browser zoom is 195 px wide.
    for (final (name, size, scale) in [
      ('200% zoom', const Size(195, 422), 1.0),
      ('200% text', const Size(390, 844), 2.0),
    ]) {
      testWidgets('$name: the buttons go above, the title gets the row',
          (tester) async {
        window(tester, size);
        await tester.pumpWidget(host(header(), size: size, textScale: scale));
        expect(tester.takeException(), isNull);
        final title = tester.getRect(find.text('Welcome back, Almaz'));
        final bell = tester.getRect(find.byType(FwSquareButton));
        expect(bell.bottom, lessThanOrEqualTo(title.top));
        // Padding 20 on each side: the title spans the content width.
        expect(title.width, greaterThan(size.width - 40 - 1 - 20));
      });
    }

    // Stacked, the buttons are drawn above the title; a screen reader
    // still reads the title first, as on a phone.
    for (final (name, size, scale) in [
      ('a phone', const Size(390, 844), 1.0),
      ('200% zoom', const Size(195, 422), 1.0),
      ('200% text', const Size(390, 844), 2.0),
    ]) {
      testWidgets('$name: the title is read before the buttons',
          (tester) async {
        window(tester, size);
        final semantics = tester.ensureSemantics();
        await tester.pumpWidget(host(header(), size: size, textScale: scale));
        final labels = tester.semantics
            .simulatedAccessibilityTraversal()
            .map((node) => node.label)
            .toList();
        final title = labels.indexOf('Welcome back, Almaz');
        final bell = labels.indexOf('Notifications');
        expect(title, isNonNegative, reason: '$labels');
        expect(bell, greaterThan(title), reason: '$labels');
        semantics.dispose();
      });
    }
  });

  group('RoleCreateButton', () {
    for (final (name, size, scale, labelled) in [
      ('a phone', const Size(390, 844), 1.0, true),
      ('a small phone', const Size(320, 640), 1.0, true),
      ('200% zoom', const Size(195, 422), 1.0, false),
      ('200% text', const Size(390, 844), 2.0, false),
    ]) {
      testWidgets(
          '$name: ${labelled ? 'the labelled pill' : 'the plus and a tooltip'}',
          (tester) async {
        window(tester, size);
        final semantics = tester.ensureSemantics();
        await tester.pumpWidget(
          MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: MediaQuery(
              data: MediaQueryData(
                size: size,
                textScaler: TextScaler.linear(scale),
              ),
              child: Scaffold(
                floatingActionButton: RoleCreateButton(
                  role: UserRole.buyer,
                  onPressed: () {},
                ),
              ),
            ),
          ),
        );
        expect(
          find.text('New Buying Intent'),
          labelled ? findsOneWidget : findsNothing,
        );
        if (labelled) {
          expect(find.bySemanticsLabel('New Buying Intent'), findsOneWidget);
        } else {
          // The tooltip names the plus for screen readers and a long press.
          expect(find.byTooltip('New Buying Intent'), findsOneWidget);
          expect(
            tester.getSize(find.byType(FloatingActionButton)).width,
            lessThan(size.width / 3),
          );
        }
        semantics.dispose();
      });
    }
  });
}
