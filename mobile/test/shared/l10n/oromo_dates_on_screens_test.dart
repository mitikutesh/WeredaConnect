import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/payout_expected_text.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

/// BUG-L2: an Afaan Oromoo user saw "Nov 19, 2026" on the payout line and on
/// the order card. The trip departure day is covered in
/// trucker_hero_card_test.dart, the boards' days in board_day_test.dart.
void main() {
  tearDown(() => Intl.defaultLocale = null);

  Widget host(Locale locale, Widget child) => MaterialApp(
        // The app's delegates: Material has no Afaan Oromoo of its own.
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        locale: locale,
        home: Scaffold(body: child),
      );

  // Noon in Ethiopia on 19 November 2026.
  final day = DateTime.utc(2026, 11, 19, 9);

  testWidgets('the payout date is written in Afaan Oromoo', (tester) async {
    await tester.pumpWidget(
      host(const Locale('om'), PayoutExpectedText(payoutAt: day)),
    );
    await tester.pumpAndSettle();

    final l10n = lookupAppLocalizations(const Locale('om'));
    expect(
      find.text(l10n.escrowPayoutExpectedOn('Sad 19, 2026')),
      findsOneWidget,
    );
    expect(find.textContaining('Nov'), findsNothing);
  });

  testWidgets('the payout date stays English in English', (tester) async {
    await tester.pumpWidget(
      host(const Locale('en'), PayoutExpectedText(payoutAt: day)),
    );
    await tester.pumpAndSettle();

    expect(find.textContaining('Nov 19, 2026'), findsOneWidget);
  });

  testWidgets('the order card day is written in Afaan Oromoo', (tester) async {
    final order = Order()
      ..orderId = 'o1'
      ..listingId = 'l1'
      ..buyerId = 'b1'
      ..farmerId = 'f1'
      ..buyerName = 'Buyer'
      ..farmerName = 'Farmer'
      ..cropType = 'wheat'
      ..quantity = 100
      ..pricePerUnit = 50
      ..totalPrice = 5000
      ..status = OrderStatus.funded
      ..createdAt = day
      ..version = 1;

    await tester.pumpWidget(
      host(const Locale('om'), OrderCard(order: order)),
    );
    await tester.pumpAndSettle();

    expect(find.textContaining('Sad 19, 2026'), findsOneWidget);
    expect(find.textContaining('Nov'), findsNothing);
  });
}
