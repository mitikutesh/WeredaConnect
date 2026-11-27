import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
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
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/language_segmented_picker.dart';

/// Tap targets and large text on the shared Fieldway pieces (audit Fix
/// 10), as canvas page M draws them: 48 dp hit areas around the 44 dp
/// pills and round buttons and the bar's 46 dp create pill, labels that
/// keep their size (a pill row scrolls, a bar label wraps), and a header
/// and create button that make room at 200% zoom.
/// WCAG contrast ratio between two opaque colours.
double _contrast(Color a, Color b) {
  final la = a.computeLuminance();
  final lb = b.computeLuminance();
  final hi = la > lb ? la : lb;
  final lo = la > lb ? lb : la;
  return (hi + 0.05) / (lo + 0.05);
}

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

  testWidgets(
      'FwSquareButton: a 44 circle on paper to see, 48 to hit, one labelled '
      'button', (tester) async {
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
    final circle = find.descendant(
      of: find.byType(FwSquareButton),
      matching: find.byType(Material),
    );
    expect(tester.getSize(circle), const Size(44, 44));
    expect(tester.widget<Material>(circle).shape, const CircleBorder());
    expect(tester.widget<Material>(circle).color, AppTheme.paper);
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

  testWidgets('FwFilterPill: 44 tall pill in a 48 tall hit area',
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
      44,
    );
    await tester.tapAt(
      tester.getRect(find.byType(FwFilterPill)).topCenter + const Offset(0, 1),
    );
    expect(taps, 1);
  });

  // Canvas page M: the selected pill is black with white ink, the others
  // outlined on the ground. Both pass 4.5:1.
  testWidgets('FwFilterPill: black when selected, outlined otherwise',
      (tester) async {
    await tester.pumpWidget(
      host(
        Row(
          children: [
            FwFilterPill(label: 'On', selected: true, onTap: () {}),
            FwFilterPill(label: 'Off', selected: false, onTap: () {}),
          ],
        ),
      ),
    );
    Material pill(String label) => tester.widget<Material>(
          find
              .ancestor(
                of: find.text(label),
                matching: find.byType(Material),
              )
              .first,
        );
    Color ink(String label) =>
        tester.widget<Text>(find.text(label)).style!.color!;

    expect(pill('On').color, AppTheme.ink);
    expect(ink('On'), Colors.white);
    expect(_contrast(ink('On'), AppTheme.ink), greaterThanOrEqualTo(4.5));

    expect(pill('Off').color, Colors.transparent);
    expect(
      (pill('Off').shape! as StadiumBorder).side.color,
      AppTheme.fieldBorder,
    );
    expect(_contrast(ink('Off'), AppTheme.ground), greaterThanOrEqualTo(4.5));
    // The edge is the only thing that marks an unselected pill: 3:1.
    expect(
      _contrast(AppTheme.fieldBorder, AppTheme.ground),
      greaterThanOrEqualTo(3),
    );
  });

  // Canvas page M: the switch is a row of pills. A long label or large
  // text keeps its size on one line and the row scrolls sideways instead
  // (the labels used to wrap inside an equal-width track).
  testWidgets(
      'FwSegmented: each pill hits 48 tall, long labels stay whole and the '
      'row scrolls', (tester) async {
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
    expect(find.byType(FittedBox), findsNothing);
    final node = tester.getSemantics(find.text('Active'));
    expect(node.rect.height, greaterThanOrEqualTo(48));
    expect(
      node,
      matchesSemantics(
        label: 'Active',
        isButton: true,
        isFocusable: true,
        hasSelectedState: true,
        isSelected: true,
        hasTapAction: true,
        hasFocusAction: true,
      ),
    );

    // One line, never cut: wider than the phone, so the row scrolls.
    final long = find.text('Kan Gurguramuuf Qophaaʼe');
    expect(tester.widget<Text>(long).maxLines, 1);
    expect(tester.widget<Text>(long).overflow, isNull);
    final paragraph = tester.renderObject<RenderParagraph>(long);
    expect(paragraph.didExceedMaxLines, isFalse);
    expect(
      paragraph.size.width,
      greaterThanOrEqualTo(
        paragraph.getMaxIntrinsicWidth(double.infinity) - 0.5,
      ),
    );
    final scroller = tester.state<ScrollableState>(
      find.descendant(
        of: find.byType(FwSegmented<String>),
        matching: find.byType(Scrollable),
      ),
    );
    expect(scroller.position.axis, Axis.horizontal);
    expect(scroller.position.maxScrollExtent, greaterThan(0));

    await tester.ensureVisible(long);
    await tester.pump();
    await tester.tap(long);
    expect(picked, 'b');
    semantics.dispose();
  });

  testWidgets(
      'FwSegmented: pills of one height; black when selected, outlined '
      'otherwise', (tester) async {
    window(tester, const Size(390, 844));
    for (final scale in [1.0, 2.0]) {
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
                    FwSegment(value: 'b', label: 'Sold Out', count: 2),
                  ],
                  selected: 'a',
                  onChanged: (_) {},
                ),
              ),
            ],
          ),
          textScale: scale,
        ),
      );
      expect(tester.takeException(), isNull);
      Finder pill(String label) => find
          .ancestor(of: find.text(label), matching: find.byType(Material))
          .first;
      final heights = [
        for (final label in ['Active', 'Sold Out · 2'])
          tester.getSize(pill(label)).height,
      ];
      // 44 on the boards; taller with large text, never shorter.
      expect(heights[0], scale == 1.0 ? 44 : greaterThanOrEqualTo(44));
      expect(heights[1], heights[0], reason: 'at ${scale}x');

      final on = tester.widget<Material>(pill('Active'));
      final off = tester.widget<Material>(pill('Sold Out · 2'));
      expect(on.color, AppTheme.ink);
      expect(on.shape, isA<StadiumBorder>());
      expect(
        tester.widget<Text>(find.text('Active')).style!.color,
        Colors.white,
      );
      expect(off.color, Colors.transparent);
      expect(
        (off.shape! as StadiumBorder).side.color,
        AppTheme.fieldBorder,
      );
      expect(
        _contrast(
          tester.widget<Text>(find.text('Sold Out · 2')).style!.color!,
          AppTheme.ground,
        ),
        greaterThanOrEqualTo(4.5),
      );
    }
  });

  group('RoleNavBar', () {
    Widget bar(
      UserRole role, {
      int? currentIndex = 0,
      ValueChanged<int>? onSelected,
      VoidCallback? onCreate,
      Size size = const Size(390, 844),
      double textScale = 1.0,
      Locale? locale,
    }) {
      return MaterialApp(
        theme: AppTheme.light(role: role),
        locale: locale,
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        home: MediaQuery(
          data: MediaQueryData(
            size: size,
            textScaler: TextScaler.linear(textScale),
          ),
          child: Scaffold(
            body: const SizedBox.expand(),
            bottomNavigationBar: RoleNavBar(
              role: role,
              tabs: tabsForRole(role),
              currentIndex: currentIndex,
              onSelected: onSelected ?? (_) {},
              onCreate: onCreate,
            ),
          ),
        ),
      );
    }

    // The app's delegates load before the bar shows.
    Future<void> pumpBar(WidgetTester tester, Widget bar) async {
      await tester.pumpWidget(bar);
      await tester.pumpAndSettle();
    }

    testWidgets('12 px, up to two lines, no FittedBox, a tap action',
        (tester) async {
      window(tester, const Size(360, 800));
      final semantics = tester.ensureSemantics();
      var selected = -1;
      await pumpBar(
        tester,
        bar(
          UserRole.farmer,
          onSelected: (i) => selected = i,
          onCreate: () {},
          size: const Size(360, 800),
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
      final label = tester.widget<Text>(find.text('Orders'));
      expect(label.style!.fontSize, 12);
      expect(label.maxLines, 2);

      // Screen readers can open a tab (excludeSemantics had dropped the
      // InkWell's tap action).
      final node = tester.getSemantics(find.bySemanticsLabel('Orders'));
      expect(
        node.getSemanticsData().hasAction(SemanticsAction.tap),
        isTrue,
      );
      tester.semantics.tap(find.semantics.byLabel('Orders'));
      expect(selected, 2);
      semantics.dispose();
    });

    // Canvas page M: three tabs, then the role's create verb.
    for (final (role, tabs, short, full) in [
      (
        UserRole.farmer,
        ['Home', 'My Crops', 'Orders'],
        'Sell',
        'Sell Crops',
      ),
      (
        UserRole.trucker,
        ['Home', 'My Trips', 'Deliveries'],
        'Post Trip',
        'Post Trip',
      ),
      (
        UserRole.buyer,
        ['Deliveries', 'Market', 'Orders'],
        'Buy',
        'New Buying Intent',
      ),
    ]) {
      testWidgets(
          '${role.name}: three tabs, then the create button, left to right',
          (tester) async {
        window(tester, const Size(390, 844));
        final semantics = tester.ensureSemantics();
        await pumpBar(tester, bar(role, onCreate: () {}));
        expect(tester.takeException(), isNull);

        expect(find.byType(RoleNavItem), findsNWidgets(3));
        final lefts = [
          for (final label in tabs)
            tester
                .getRect(
                  find.ancestor(
                    of: find.text(label),
                    matching: find.byType(RoleNavItem),
                  ),
                )
                .left,
        ];
        expect(lefts, orderedEquals([...lefts]..sort()));
        final items = tester
            .widgetList<RoleNavItem>(find.byType(RoleNavItem))
            .map((item) => item.label)
            .toList();
        expect(items, tabs);

        // The create button ends the bar, inside it: the short word to
        // see, the full name for a screen reader and the tooltip.
        final create = find.byType(RoleCreateButton);
        expect(create, findsOneWidget);
        expect(
          find.descendant(of: find.byType(RoleNavBar), matching: create),
          findsOneWidget,
        );
        expect(
          find.descendant(of: create, matching: find.text(short)),
          findsOneWidget,
        );
        expect(find.bySemanticsLabel(full), findsOneWidget);
        expect(find.byTooltip(full), findsOneWidget);
        final createRect = tester.getRect(create);
        final lastTab = tester.getRect(find.byType(RoleNavItem).last);
        expect(createRect.left, greaterThanOrEqualTo(lastTab.right - 0.01));
        expect(find.byType(FloatingActionButton), findsNothing);
        semantics.dispose();
      });

      testWidgets('${role.name}: tapping the create button calls onCreate',
          (tester) async {
        window(tester, const Size(390, 844));
        var created = 0;
        final selected = <int>[];
        await pumpBar(
          tester,
          bar(role, onCreate: () => created++, onSelected: selected.add),
        );
        await tester.tap(find.byType(RoleCreateButton));
        expect(created, 1);
        expect(selected, isEmpty);

        // And each tab its own index, never the create verb.
        for (var i = 0; i < tabs.length; i++) {
          await tester.tap(find.text(tabs[i]));
        }
        expect(selected, [0, 1, 2]);
        expect(created, 1);
      });
    }

    testWidgets('a screen reader can press the create button', (tester) async {
      window(tester, const Size(390, 844));
      final semantics = tester.ensureSemantics();
      var created = 0;
      await pumpBar(
        tester,
        bar(UserRole.buyer, onCreate: () => created++),
      );
      final node = tester.getSemantics(
        find.bySemanticsLabel('New Buying Intent'),
      );
      expect(
        node,
        matchesSemantics(
          label: 'New Buying Intent',
          isButton: true,
          hasTapAction: true,
        ),
      );
      tester.semantics.tap(find.semantics.byLabel('New Buying Intent'));
      expect(created, 1);
      // Heard once: the short word under the name is not a second node.
      expect(find.bySemanticsLabel('Buy'), findsNothing);
      semantics.dispose();
    });

    testWidgets('no onCreate (a page outside the tabs): three tabs alone',
        (tester) async {
      window(tester, const Size(390, 844));
      await pumpBar(tester, bar(UserRole.trucker, currentIndex: null));
      expect(find.byType(RoleNavItem), findsNWidgets(3));
      expect(find.byType(RoleCreateButton), findsNothing);
      // Nothing selected: no dot, no bold label.
      for (final item
          in tester.widgetList<RoleNavItem>(find.byType(RoleNavItem))) {
        expect(item.selected, isFalse);
      }
    });

    // The board: a black pill at least 62 tall floating over the white
    // ground, white icons and labels, an orange create pill at least 46
    // tall with black ink, and under the selected tab a 4 px dot in the
    // role's tint.
    for (final role in UserRole.values) {
      testWidgets('${role.name}: the black pill, the orange button, the dot',
          (tester) async {
        window(tester, const Size(390, 844));
        await pumpBar(tester, bar(role, currentIndex: 1, onCreate: () {}));

        final pill = find.descendant(
          of: find.byType(RoleNavBar),
          matching: find.byWidgetPredicate(
            (w) => w is DecoratedBox && w.decoration is ShapeDecoration,
          ),
        );
        final decoration =
            tester.widget<DecoratedBox>(pill).decoration as ShapeDecoration;
        expect(decoration.color, AppTheme.ink);
        expect(decoration.shape, isA<StadiumBorder>());
        final pillRect = tester.getRect(pill);
        expect(pillRect.height, greaterThanOrEqualTo(62));
        // Floating: clear of both edges and the bottom of the screen.
        expect(pillRect.left, greaterThan(0));
        expect(pillRect.right, lessThan(390));
        expect(pillRect.bottom, lessThan(844));

        for (final item in find.byType(RoleNavItem).evaluate()) {
          final finder = find.byWidget(item.widget);
          final icon = tester.widget<Icon>(
            find.descendant(of: finder, matching: find.byType(Icon)),
          );
          final label = tester.widget<Text>(
            find.descendant(of: finder, matching: find.byType(Text)),
          );
          expect(icon.color, Colors.white);
          expect(label.style!.color, Colors.white);
          expect(label.style!.fontSize, 12);
          expect(
            _contrast(label.style!.color!, AppTheme.ink),
            greaterThanOrEqualTo(4.5),
          );
        }

        // One dot, under the selected tab only, in the role's tint.
        Iterable<BoxDecoration> dots(Finder item) => tester
            .widgetList<DecoratedBox>(
              find.descendant(of: item, matching: find.byType(DecoratedBox)),
            )
            .map((box) => box.decoration)
            .whereType<BoxDecoration>()
            .where((d) => d.shape == BoxShape.circle);
        final tint = RoleColors.forRole(role).tint;
        for (var i = 0; i < 3; i++) {
          final item = find.byType(RoleNavItem).at(i);
          expect(
            dots(item).single.color,
            i == 1 ? tint : Colors.transparent,
            reason: 'tab $i',
          );
          expect(
            tester.widget<RoleNavItem>(item).selected,
            i == 1,
            reason: 'tab $i',
          );
        }
        final dot = find.descendant(
          of: find.byType(RoleNavItem).at(1),
          matching: find.byWidgetPredicate(
            (w) =>
                w is DecoratedBox &&
                w.decoration is BoxDecoration &&
                (w.decoration as BoxDecoration).shape == BoxShape.circle,
          ),
        );
        expect(tester.getSize(dot), const Size(4, 4));
        expect(
          tester.getRect(dot).top,
          greaterThanOrEqualTo(
            tester
                .getRect(
                  find.descendant(
                    of: find.byType(RoleNavItem).at(1),
                    matching: find.byType(Text),
                  ),
                )
                .bottom,
          ),
        );
        // The tint is the only colour on the bar besides the orange: it
        // must be told apart from the black under it.
        expect(_contrast(tint, AppTheme.ink), greaterThanOrEqualTo(3));

        final create = find.descendant(
          of: find.byType(RoleCreateButton),
          matching: find.byType(Material),
        );
        final material = tester.widget<Material>(create);
        expect(material.color, AppTheme.orange);
        expect(material.shape, isA<StadiumBorder>());
        expect(tester.getSize(create).height, greaterThanOrEqualTo(46));
        final word = tester.widget<Text>(
          find.descendant(of: create, matching: find.byType(Text)),
        );
        expect(word.style!.color, AppTheme.ink);
        expect(
          _contrast(AppTheme.ink, AppTheme.orange),
          greaterThanOrEqualTo(4.5),
        );
        // Inside the black pill, not floating over the page.
        expect(pillRect.contains(tester.getRect(create).topLeft), isTrue);
        expect(pillRect.contains(tester.getRect(create).bottomRight), isTrue);
      });
    }

    // Every target on the bar is at least 48 by 48, on the narrowest
    // phone, at 200% text and in the three languages, with no overflow.
    for (final (name, size, scale) in [
      ('a phone', const Size(390, 844), 1.0),
      ('a small phone', const Size(360, 640), 1.0),
      ('320 px', const Size(320, 640), 1.0),
      ('200% text', const Size(360, 800), 2.0),
      ('320 px at 200% text', const Size(320, 640), 2.0),
    ]) {
      for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
        for (final role in UserRole.values) {
          testWidgets(
              '${role.name} in ${locale.languageCode}, $name: 48 px targets, '
              'nothing overflows', (tester) async {
            window(tester, size);
            final semantics = tester.ensureSemantics();
            await pumpBar(
              tester,
              bar(
                role,
                onCreate: () {},
                size: size,
                textScale: scale,
                locale: locale,
              ),
            );
            expect(tester.takeException(), isNull);

            final l10n = lookupAppLocalizations(locale);
            final pill = tester.getRect(
              find.descendant(
                of: find.byType(RoleNavBar),
                matching: find.byWidgetPredicate(
                  (w) => w is DecoratedBox && w.decoration is ShapeDecoration,
                ),
              ),
            );
            expect(pill.height, greaterThanOrEqualTo(62));
            expect(pill.left, greaterThanOrEqualTo(0));
            expect(pill.right, lessThanOrEqualTo(size.width));

            final rects = <Rect>[];
            for (final tab in tabsForRole(role)) {
              final label = tab.label(l10n);
              final node = tester.getSemantics(find.bySemanticsLabel(label));
              final rect = tester.getRect(
                find.ancestor(
                  of: find.text(label),
                  matching: find.byType(RoleNavItem),
                ),
              );
              expect(rect.height, greaterThanOrEqualTo(48), reason: label);
              expect(rect.width, greaterThanOrEqualTo(48), reason: label);
              expect(node.rect.height, greaterThanOrEqualTo(48));
              rects.add(rect);
            }
            final name = RoleCreateButton.labelFor(l10n, role);
            final create = tester.getSemantics(find.bySemanticsLabel(name));
            expect(create.rect.height, greaterThanOrEqualTo(48), reason: name);
            expect(create.rect.width, greaterThanOrEqualTo(48), reason: name);
            rects.add(tester.getRect(find.byType(RoleCreateButton)));

            // Side by side inside the pill, none over another.
            for (var i = 0; i < rects.length; i++) {
              expect(rects[i].left, greaterThanOrEqualTo(pill.left));
              expect(rects[i].right, lessThanOrEqualTo(pill.right));
              expect(rects[i].top, greaterThanOrEqualTo(pill.top));
              expect(rects[i].bottom, lessThanOrEqualTo(pill.bottom));
              if (i > 0) {
                expect(
                  rects[i].left,
                  greaterThanOrEqualTo(rects[i - 1].right - 0.01),
                  reason: 'slot $i',
                );
              }
            }
            semantics.dispose();
          });
        }
      }
    }
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

    // As on the screens, the header scrolls with its page: the 42 px
    // title of canvas page M, in the test font's one em per letter, is
    // taller than a 422 px window on its own.
    Widget scrolling(Widget child) => SingleChildScrollView(child: child);

    testWidgets('the title is the boards\' 42 px', (tester) async {
      window(tester, const Size(390, 844));
      await tester.pumpWidget(host(header()));
      final title = tester.widget<Text>(find.text('Welcome back, Almaz'));
      expect(title.style!.fontSize, 42);
      expect(title.style!.color, AppTheme.ink);
      expect(FwText.pageTitle.fontSize, 42);
    });

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
        await tester.pumpWidget(
          host(scrolling(header()), size: size, textScale: scale),
        );
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
        await tester.pumpWidget(
          host(scrolling(header()), size: size, textScale: scale),
        );
        expect(tester.takeException(), isNull);
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

  // The bar's create pill: the short word where the bar leaves it room,
  // the plus alone where it does not (the bar measures what its tabs
  // need). The full name is always its semantics label and its tooltip.
  group('RoleCreateButton', () {
    Widget button(
      Size size,
      double scale, {
      UserRole role = UserRole.buyer,
      bool floating = false,
      bool expanded = false,
      // The room the bar leaves the button; unbounded when null.
      double? room,
      VoidCallback? onPressed,
    }) {
      return MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: MediaQuery(
          data: MediaQueryData(
            size: size,
            textScaler: TextScaler.linear(scale),
          ),
          child: Scaffold(
            body: Center(
              child: ConstrainedBox(
                constraints: BoxConstraints(
                  minWidth: expanded ? 220 : 0,
                  maxWidth: expanded ? 220 : room ?? double.infinity,
                ),
                child: RoleCreateButton(
                  role: role,
                  floating: floating,
                  expanded: expanded,
                  onPressed: onPressed ?? () {},
                ),
              ),
            ),
          ),
        ),
      );
    }

    final pill = find.descendant(
      of: find.byType(RoleCreateButton),
      matching: find.byType(Material),
    );

    /// How wide the pill is with its word, given all the room it wants.
    Future<double> naturalWidth(WidgetTester tester, double scale) async {
      await tester.pumpWidget(button(const Size(390, 844), scale));
      await tester.pumpAndSettle();
      return tester.getSize(pill).width.ceilToDouble();
    }

    for (final (name, scale, room, labelled) in <(
      String,
      double,
      double Function(double at1x, double at2x),
      bool,
    )>[
      ('room for the word', 1.0, (at1x, _) => at1x, true),
      ('one pixel short', 1.0, (at1x, _) => at1x - 1, false),
      ('no room beside the tabs', 1.0, (_, __) => 48, false),
      // The word is twice as wide: the same room no longer holds it.
      ('200% text', 2.0, (at1x, _) => at1x, false),
      ('200% text, room for the larger word', 2.0, (_, at2x) => at2x, true),
    ]) {
      testWidgets(
          '$name: ${labelled ? 'the labelled pill' : 'the plus and a tooltip'}',
          (tester) async {
        const size = Size(390, 844);
        window(tester, size);
        final semantics = tester.ensureSemantics();
        final at1x = await naturalWidth(tester, 1.0);
        final at2x = await naturalWidth(tester, 2.0);
        expect(at2x, greaterThan(at1x));
        final given = room(at1x, at2x);
        var presses = 0;
        await tester.pumpWidget(
          button(size, scale, room: given, onPressed: () => presses++),
        );
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);

        expect(find.byIcon(Icons.add), findsOneWidget);
        // Never the long name on the pill: the bar has room for one word.
        expect(find.text('New Buying Intent'), findsNothing);
        // The name, for a screen reader and a long press, both ways.
        expect(find.bySemanticsLabel('New Buying Intent'), findsOneWidget);
        expect(find.byTooltip('New Buying Intent'), findsOneWidget);
        final node = tester.getSemantics(
          find.bySemanticsLabel('New Buying Intent'),
        );
        expect(
          node,
          matchesSemantics(
            label: 'New Buying Intent',
            isButton: true,
            hasTapAction: true,
          ),
        );
        // 46 tall to see, 48 by 48 to hit, never wider than its room.
        expect(tester.getSize(pill).height, greaterThanOrEqualTo(46));
        expect(node.rect.height, greaterThanOrEqualTo(48));
        expect(node.rect.width, greaterThanOrEqualTo(48));
        expect(tester.getSize(pill).width, lessThanOrEqualTo(given));

        final word = tester.getSize(
          find.ancestor(of: find.text('Buy'), matching: find.byType(ClipRect)),
        );
        if (labelled) {
          // The whole word, at its size: not clipped, not shrunk.
          expect(word.width, greaterThan(3 * 14.0 * scale));
          expect(
            tester.getSize(pill).width.ceilToDouble(),
            scale == 1.0 ? at1x : at2x,
          );
          expect(
            tester.widget<Text>(find.text('Buy')).style!.fontSize,
            14,
          );
        } else {
          // Folded shut: the plus alone, a small round button.
          expect(word.width, 0);
          expect(tester.getSize(pill).width, moreOrLessEquals(46));
        }

        // A tap on the hit area's edge, outside the 46 px pill, presses it.
        final hit = tester.getRect(find.byType(RoleCreateButton));
        await tester.tapAt(hit.topCenter + const Offset(0, 0.5));
        expect(presses, 1);
        semantics.dispose();
      });
    }

    // The tablet rail's button has no bar to measure: it keeps the rule
    // of 26 label-sized letters of window width.
    for (final (name, size, scale, labelled) in [
      ('a tablet', const Size(800, 900), 1.0, true),
      ('200% zoom', const Size(195, 422), 1.0, false),
      ('200% text on a phone', const Size(390, 844), 2.0, false),
    ]) {
      testWidgets(
          'floating, $name: '
          '${labelled ? 'the labelled pill' : 'the plus and a tooltip'}',
          (tester) async {
        window(tester, size);
        final semantics = tester.ensureSemantics();
        await tester.pumpWidget(button(size, scale, floating: true));
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        final pill = find.descendant(
          of: find.byType(RoleCreateButton),
          matching: find.byType(Material),
        );
        final word = tester.getSize(
          find.ancestor(of: find.text('Buy'), matching: find.byType(ClipRect)),
        );
        expect(find.bySemanticsLabel('New Buying Intent'), findsOneWidget);
        expect(find.byTooltip('New Buying Intent'), findsOneWidget);
        if (labelled) {
          expect(word.width, greaterThan(0));
        } else {
          expect(word.width, 0);
          expect(tester.getSize(pill).width, moreOrLessEquals(54));
          expect(tester.getSize(pill).width, lessThan(size.width / 3));
        }
        semantics.dispose();
      });
    }

    testWidgets('as tall as its label: it never fills the height it is given',
        (tester) async {
      window(tester, const Size(390, 844));
      await tester.pumpWidget(button(const Size(390, 844), 1.0));
      await tester.pumpAndSettle();
      final pill = find.descendant(
        of: find.byType(RoleCreateButton),
        matching: find.byType(Material),
      );
      expect(tester.getSize(pill).height, 46);
      expect(tester.getSize(find.byType(RoleCreateButton)).height, 48);
    });

    testWidgets('floating (the tablet rail): the same pill, 54 tall, lifted',
        (tester) async {
      window(tester, const Size(800, 900));
      await tester.pumpWidget(
        button(const Size(800, 900), 1.0, floating: true),
      );
      await tester.pumpAndSettle();
      final pill = find.descendant(
        of: find.byType(RoleCreateButton),
        matching: find.byType(Material),
      );
      final material = tester.widget<Material>(pill);
      expect(tester.getSize(pill).height, 54);
      expect(material.color, AppTheme.orange);
      expect(material.elevation, greaterThan(0));
      expect(find.text('Buy'), findsOneWidget);
    });

    for (final (role, full) in [
      (UserRole.farmer, 'Sell Crops'),
      (UserRole.trucker, 'Post Trip'),
      (UserRole.buyer, 'New Buying Intent'),
    ]) {
      testWidgets(
          '${role.name}, expanded (the desktop sidebar): the full name on '
          'a black button (the page keeps the one orange action)',
          (tester) async {
        window(tester, const Size(1300, 900));
        var presses = 0;
        await tester.pumpWidget(
          button(
            const Size(1300, 900),
            1.0,
            role: role,
            expanded: true,
            onPressed: () => presses++,
          ),
        );
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        expect(find.text(full), findsOneWidget);
        final filled = find.byWidgetPredicate((w) => w is FilledButton);
        expect(tester.getSize(filled), const Size(220, 44));
        final style = tester.widget<FilledButton>(filled).style!;
        expect(style.backgroundColor!.resolve({}), AppTheme.ink);
        expect(style.foregroundColor!.resolve({}), Colors.white);
        expect(
          style.shape!.resolve({}),
          RoundedRectangleBorder(borderRadius: BorderRadius.circular(14)),
        );
        await tester.tap(find.text(full));
        expect(presses, 1);
      });
    }
  });
}
