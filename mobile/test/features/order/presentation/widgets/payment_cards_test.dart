import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/order/presentation/widgets/payment_cards.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// The pieces of the buyer's payment boards (PaymentM, PaymentFailedM,
/// PaymentOfflineM).
void main() {
  Future<void> pump(
    WidgetTester tester,
    Widget Function(AppLocalizations l10n) build, {
    String locale = 'en',
    Size size = const Size(390, 844),
    double textScale = 1,
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    tester.platformDispatcher.textScaleFactorTestValue = textScale;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(role: UserRole.buyer),
        locale: Locale(locale),
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        home: Builder(
          builder: (context) => Scaffold(
            body: SafeArea(child: build(AppLocalizations.of(context)!)),
          ),
        ),
      ),
    );
    await tester.pump();
  }

  group('PaymentStateView', () {
    testWidgets('failed: sketch, pill, title, explanation, two actions',
        (tester) async {
      var tried = 0;
      var later = 0;
      await pump(
        tester,
        (l10n) => PaymentStateView.failed(
          l10n: l10n,
          onTryAgain: () => tried++,
          onPayLater: () => later++,
        ),
      );

      // The buyer's large drawing, once.
      final sketch = tester.widget<RoleSketch>(find.byType(RoleSketch));
      expect(sketch.role, UserRole.buyer);
      expect(find.text('Not paid'), findsOneWidget);
      // Not colour alone: the pill carries a glyph.
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
      expect(find.text('Payment did not go through'), findsOneWidget);

      await tester.tap(find.byKey(const Key('payment_state_primary')));
      expect(tried, 1);
      await tester.tap(find.byKey(const Key('payment_state_secondary')));
      expect(later, 1);
      // Close leaves the payment like the second action.
      await tester.tap(find.byKey(const Key('payment_close')));
      expect(later, 2);
    });

    testWidgets(
        'offline: the order is saved on the phone; Retry, Back to order',
        (tester) async {
      var back = 0;
      await pump(
        tester,
        (l10n) => PaymentStateView.offline(
          l10n: l10n,
          onTryAgain: null,
          onBackToOrder: () => back++,
        ),
      );

      expect(find.text('No network'), findsOneWidget);
      expect(find.byIcon(Icons.wifi_off), findsOneWidget);
      expect(find.text('No internet connection'), findsOneWidget);
      // A request in flight: the orange action waits.
      expect(
        tester
            .widget<FilledButton>(
              find.byKey(const Key('payment_state_primary')),
            )
            .onPressed,
        isNull,
      );
      await tester.tap(find.text('Back to order'));
      expect(back, 1);
    });

    testWidgets('the title is a heading; close and both actions are 48 tall',
        (tester) async {
      final semantics = tester.ensureSemantics();
      await pump(
        tester,
        (l10n) => PaymentStateView.failed(
          l10n: l10n,
          onTryAgain: () {},
          onPayLater: () {},
        ),
      );

      expect(
        tester
            .getSemantics(find.text('Payment did not go through'))
            .getSemanticsData()
            .flagsCollection
            .isHeader,
        isTrue,
      );
      expect(find.bySemanticsLabel('Close'), findsOneWidget);
      for (final key in const [
        Key('payment_close'),
        Key('payment_state_primary'),
        Key('payment_state_secondary'),
      ]) {
        final size = tester.getSize(find.byKey(key));
        expect(size.height, greaterThanOrEqualTo(48), reason: '$key');
        expect(size.width, greaterThanOrEqualTo(48), reason: '$key');
      }
      semantics.dispose();
    });

    for (final locale in ['en', 'am', 'om']) {
      testWidgets('$locale: 320 px at 2x text scrolls, nothing overflows',
          (tester) async {
        await pump(
          tester,
          (l10n) => PaymentStateView.offline(
            l10n: l10n,
            onTryAgain: () {},
            onBackToOrder: () {},
          ),
          locale: locale,
          size: const Size(320, 568),
          textScale: 2,
        );

        expect(tester.takeException(), isNull);
        // No drawing with large text (the sketch rules).
        expect(
          tester.getSize(find.byType(RoleSketch)),
          Size.zero,
        );
        // The actions are reachable by scrolling.
        await tester.ensureVisible(
          find.byKey(const Key('payment_state_secondary')),
        );
        await tester.pump();
        expect(tester.takeException(), isNull);
      });
    }
  });

  group('PaymentHeader', () {
    testWidgets('a long title keeps the sketch out and wraps', (tester) async {
      await pump(
        tester,
        (l10n) => PaymentHeader(
          title: 'A payment title that needs two lines',
          onClose: () {},
        ),
      );

      expect(find.byType(RoleSketch), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('a short title leaves room for the buyer sketch',
        (tester) async {
      await pump(
        tester,
        (l10n) => PaymentHeader(title: 'Pay', onClose: () {}),
      );

      expect(
        tester.widget<RoleSketch>(find.byType(RoleSketch)).role,
        UserRole.buyer,
      );
    });
  });

  group('cards', () {
    testWidgets('the amount is in the buyer colour on the raised card',
        (tester) async {
      await pump(
        tester,
        (l10n) => const PaymentAmountCard(amount: 68400),
      );

      expect(
        tester.widget<Text>(find.text('68,400 ETB')).style!.color,
        RoleColors.buyer.primary,
      );
      expect(find.text('AMOUNT'), findsOneWidget);
    });

    testWidgets('Confirm it is you names the method as a pill', (tester) async {
      await pump(
        tester,
        (l10n) => const ConfirmItsYouCard(method: 'Confirmed with Fayda'),
      );

      expect(find.text("Confirm it's you"), findsOneWidget);
      expect(find.text('Confirmed with Fayda'), findsOneWidget);
    });
  });
}
