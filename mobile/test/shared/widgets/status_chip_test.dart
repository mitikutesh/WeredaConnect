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

  group('LoadErrorChip', () {
    const omMessage = 'Lakkoofsonni kun fe\'amuu hin dandeenye.';
    const omRetry = 'Irra Deebi\'ii Yaali';

    Future<void> pumpChip(
      WidgetTester tester, {
      required double textScale,
      String message = omMessage,
      String retryLabel = omRetry,
    }) async {
      tester.view.physicalSize = const Size(360, 640);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        MaterialApp(
          home: MediaQuery(
            data: MediaQueryData(
              size: const Size(360, 640),
              textScaler: TextScaler.linear(textScale),
            ),
            child: Scaffold(
              body: Padding(
                padding: const EdgeInsets.all(16),
                child: LoadErrorChip(
                  message: message,
                  retryLabel: retryLabel,
                  onRetry: () {},
                ),
              ),
            ),
          ),
        ),
      );
    }

    testWidgets('a short retry label keeps one line at 100%', (tester) async {
      await pumpChip(
        tester,
        textScale: 1,
        message: 'These numbers could not be loaded.',
        retryLabel: 'Retry',
      );

      expect(tester.takeException(), isNull);
      expect(tester.getSize(find.text('Retry')).height, lessThan(20));
    });

    testWidgets('Oromo at 200%: the retry label wraps, nothing overflows',
        (tester) async {
      await pumpChip(tester, textScale: 2);

      expect(tester.takeException(), isNull);
      final row = tester.getSize(find.byType(LoadErrorChip));
      final button = tester.getSize(find.byType(TextButton));
      final message = tester.getSize(
        find.text(omMessage),
      );
      expect(button.width, lessThanOrEqualTo(row.width / 2));
      expect(message.width, greaterThan(100));
      // The label wrapped rather than pushing the row wider.
      expect(tester.getSize(find.text(omRetry)).height, greaterThan(26));
    });
  });
}
