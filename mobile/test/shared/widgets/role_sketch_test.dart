import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// The canvas's sketch rules (specs/design-canvas/canvas.json): decoration
/// only, one per screen beside a short title, hidden with large text and
/// reduced effects.
void main() {
  Widget app(Widget child, {MediaQueryData? media}) {
    return MaterialApp(
      theme: AppTheme.light(role: UserRole.farmer),
      home: Builder(
        builder: (context) => MediaQuery(
          data: media ?? MediaQuery.of(context),
          child: Scaffold(body: child),
        ),
      ),
    );
  }

  Finder drawing() => find.descendant(
        of: find.byType(RoleSketch),
        matching: find.byType(CustomPaint),
      );

  group('RoleSketch', () {
    testWidgets('draws each role at its own proportions', (tester) async {
      for (final role in UserRole.values) {
        await tester.pumpWidget(app(Center(child: RoleSketch(role: role))));
        final size = tester.getSize(drawing());
        expect(size.width, 84, reason: role.name);
        expect(
          size.width / size.height,
          closeTo(RoleSketch.aspectRatio(role), 0.01),
          reason: role.name,
        );
      }
    });

    testWidgets('is decoration: no semantics, no hit target', (tester) async {
      final handle = tester.ensureSemantics();
      var taps = 0;
      await tester.pumpWidget(
        app(
          GestureDetector(
            onTap: () => taps++,
            behavior: HitTestBehavior.opaque,
            child: const Center(child: RoleSketch(role: UserRole.trucker)),
          ),
        ),
      );
      // A tap on the drawing reaches what is under it.
      await tester.tapAt(tester.getCenter(find.byType(RoleSketch)));
      expect(taps, 1);
      final node = tester.getSemantics(find.byType(RoleSketch));
      expect(node.label, isEmpty);
      handle.dispose();
    });

    testWidgets('hidden with large text and with reduced effects',
        (tester) async {
      const base = MediaQueryData(size: Size(390, 844));
      for (final media in [
        base.copyWith(textScaler: const TextScaler.linear(1.5)),
        base.copyWith(disableAnimations: true),
        base.copyWith(highContrast: true),
        base.copyWith(boldText: true),
      ]) {
        await tester.pumpWidget(
          app(
            const Center(child: RoleSketch(role: UserRole.buyer)),
            media: media,
          ),
        );
        expect(drawing(), findsNothing);
      }
      await tester.pumpWidget(
        app(const Center(child: RoleSketch(role: UserRole.buyer)), media: base),
      );
      expect(drawing(), findsOneWidget);
    });

    test('reads the path words the drawings use', () {
      final bounds = sketchPath('M58 12 H 120 V 46 H 58 Z').getBounds();
      expect(bounds, const Rect.fromLTRB(58, 12, 120, 46));
      final curve = sketchPath('M4 62 C 46 58, 96 60, 138 56').getBounds();
      expect(curve.left, 4);
      expect(curve.right, 138);
    });
  });

  group('FwPageHeader sketch', () {
    testWidgets('beside a short title, never under it', (tester) async {
      await tester.pumpWidget(
        app(
          const FwPageHeader(title: 'Today', sketchRole: UserRole.trucker),
        ),
      );
      expect(drawing(), findsOneWidget);
      final title = tester.getRect(find.text('Today'));
      final sketch = tester.getRect(drawing());
      expect(sketch.left, greaterThanOrEqualTo(title.right));
    });

    testWidgets('left out when the title needs the room', (tester) async {
      await tester.pumpWidget(
        app(
          const FwPageHeader(
            title: 'Geejjibaalee keessan hunda',
            sketchRole: UserRole.trucker,
          ),
        ),
      );
      expect(drawing(), findsNothing);
      expect(find.text('Geejjibaalee keessan hunda'), findsOneWidget);
    });

    // MarketM with the app's two header buttons: no room on the title's
    // line beside them, so the sketch goes under them.
    group('under the trailing buttons', () {
      const buttons = Row(
        key: Key('buttons'),
        mainAxisSize: MainAxisSize.min,
        children: [
          SizedBox(width: 44, height: 44),
          SizedBox(width: 8),
          SizedBox(width: 44, height: 44),
        ],
      );

      Future<void> pump(
        WidgetTester tester, {
        required String title,
        bool below = true,
        MediaQueryData? media,
      }) async {
        tester.view.physicalSize = const Size(390, 844);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        await tester.pumpWidget(
          app(
            FwPageHeader(
              eyebrow: 'Deliver to Merkato',
              title: title,
              sketchRole: UserRole.buyer,
              sketchBelowTrailing: below,
              trailing: buttons,
            ),
            media: media,
          ),
        );
      }

      testWidgets('the usual header has no room for it at 390 px',
          (tester) async {
        await pump(tester, title: 'Market', below: false);
        expect(drawing(), findsNothing);
      });

      testWidgets('on the title line, under the buttons, in the right 35%',
          (tester) async {
        await pump(tester, title: 'Market');
        expect(drawing(), findsOneWidget);
        final sketch = tester.getRect(drawing());
        final title = tester.getRect(find.text('Market'));
        final trailing = tester.getRect(find.byKey(const Key('buttons')));
        final eyebrow = tester.getRect(find.text('Deliver to Merkato'));

        expect(sketch.width, 58);
        expect(sketch.top, greaterThanOrEqualTo(trailing.bottom));
        expect(sketch.left, greaterThanOrEqualTo(title.right));
        expect(sketch.right, 370);
        // The header's content is 350 wide; its right 35% starts at 247.5.
        expect(sketch.left, greaterThanOrEqualTo(20 + 350 * 0.65));
        // On the title's line, not under it.
        expect(sketch.bottom, lessThanOrEqualTo(title.bottom));
        expect(sketch.top, greaterThanOrEqualTo(title.top));
        // The buttons keep the top right; the eyebrow is beside them and
        // the title on one line.
        expect(trailing.right, 370);
        expect(trailing.size, const Size(96, 44));
        expect(eyebrow.right, lessThanOrEqualTo(trailing.left));
        expect(title.height, lessThan(42 * 1.1 * 1.5));
      });

      testWidgets('a long title drops it and keeps the usual header',
          (tester) async {
        await pump(tester, title: 'Midhaan Barbaadi');
        expect(drawing(), findsNothing);
        final title = tester.getRect(find.text('Midhaan Barbaadi'));
        final trailing = tester.getRect(find.byKey(const Key('buttons')));
        // The title wraps beside the buttons, never under them.
        expect(title.right, lessThanOrEqualTo(trailing.left));
        expect(tester.takeException(), isNull);
      });

      testWidgets('large text drops it', (tester) async {
        await pump(
          tester,
          title: 'Market',
          media: const MediaQueryData(
            size: Size(390, 844),
            textScaler: TextScaler.linear(2),
          ),
        );
        expect(drawing(), findsNothing);
        expect(tester.takeException(), isNull);
      });

      testWidgets('read in the usual order: eyebrow, title, buttons',
          (tester) async {
        final handle = tester.ensureSemantics();
        tester.view.physicalSize = const Size(390, 844);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.reset);
        await tester.pumpWidget(
          app(
            FwPageHeader(
              eyebrow: 'Deliver to Merkato',
              title: 'Market',
              sketchRole: UserRole.buyer,
              sketchBelowTrailing: true,
              trailing: IconButton(
                onPressed: () {},
                tooltip: 'Notifications',
                icon: const Icon(Icons.notifications_none),
              ),
            ),
          ),
        );
        expect(drawing(), findsOneWidget);
        final order = tester.semantics
            .simulatedAccessibilityTraversal()
            .map((node) => node.label.isEmpty ? node.tooltip : node.label)
            .toList();
        expect(order, ['Deliver to Merkato', 'Market', 'Notifications']);
        handle.dispose();
      });
    });

    testWidgets('no sketch unless a role is named', (tester) async {
      await tester.pumpWidget(app(const FwPageHeader(title: 'Today')));
      expect(find.byType(RoleSketch), findsNothing);
    });
  });
}
