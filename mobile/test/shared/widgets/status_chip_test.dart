import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

Future<void> _pump(WidgetTester tester, Widget child) {
  return tester.pumpWidget(
    MaterialApp(home: Scaffold(body: Center(child: child))),
  );
}

void main() {
  group('StatusKind visuals', () {
    test('every kind resolves to its WCAG-checked container pair', () {
      expect(
        StatusKind.neutral.background,
        AppTheme.statusNeutralContainer,
      );
      expect(
        StatusKind.neutral.foreground,
        AppTheme.onStatusNeutralContainer,
      );
      expect(
        StatusKind.success.background,
        AppTheme.statusSuccessContainer,
      );
      expect(
        StatusKind.success.foreground,
        AppTheme.onStatusSuccessContainer,
      );
      expect(
        StatusKind.warning.background,
        AppTheme.statusWarningContainer,
      );
      expect(
        StatusKind.warning.foreground,
        AppTheme.onStatusWarningContainer,
      );
      expect(StatusKind.error.background, AppTheme.statusErrorContainer);
      expect(StatusKind.error.foreground, AppTheme.onStatusErrorContainer);
      expect(StatusKind.info.background, AppTheme.statusInfoContainer);
      expect(StatusKind.info.foreground, AppTheme.onStatusInfoContainer);
      expect(
        StatusKind.transit.background,
        AppTheme.statusTransitContainer,
      );
      expect(
        StatusKind.transit.foreground,
        AppTheme.onStatusTransitContainer,
      );
    });
  });

  group('StatusChip', () {
    testWidgets('renders label, default icon, and pair colors', (tester) async {
      await _pump(
        tester,
        const StatusChip(label: 'In transit', kind: StatusKind.transit),
      );

      expect(find.text('In transit'), findsOneWidget);

      final icon = tester.widget<Icon>(find.byType(Icon));
      expect(icon.icon, Icons.local_shipping_outlined);
      expect(icon.color, AppTheme.onStatusTransitContainer);

      final box = tester.widget<Container>(
        find.ancestor(
          of: find.text('In transit'),
          matching: find.byType(Container),
        ),
      );
      final decoration = box.decoration! as BoxDecoration;
      expect(decoration.color, AppTheme.statusTransitContainer);
    });

    testWidgets('custom icon overrides the kind default', (tester) async {
      await _pump(
        tester,
        const StatusChip(
          label: 'In escrow',
          kind: StatusKind.warning,
          icon: Icons.shield,
        ),
      );

      final icon = tester.widget<Icon>(find.byType(Icon));
      expect(icon.icon, Icons.shield);
      expect(icon.color, AppTheme.onStatusWarningContainer);
    });

    testWidgets('large variant scales icon and text', (tester) async {
      await _pump(
        tester,
        const StatusChip(
          label: 'Delivered',
          kind: StatusKind.success,
          large: true,
        ),
      );

      final icon = tester.widget<Icon>(find.byType(Icon));
      expect(icon.size, 20);

      final text = tester.widget<Text>(find.text('Delivered'));
      expect(text.style?.fontSize, 16);
      expect(text.style?.fontWeight, FontWeight.w700);
    });

    testWidgets('every kind renders without error', (tester) async {
      for (final kind in StatusKind.values) {
        await _pump(tester, StatusChip(label: kind.name, kind: kind));
        expect(find.text(kind.name), findsOneWidget);
      }
    });
  });
}
