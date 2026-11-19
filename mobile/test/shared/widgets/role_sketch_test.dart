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

    testWidgets('no sketch unless a role is named', (tester) async {
      await tester.pumpWidget(app(const FwPageHeader(title: 'Today')));
      expect(find.byType(RoleSketch), findsNothing);
    });
  });
}
