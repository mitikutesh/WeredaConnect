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
/// the order card. FUP-545: Amharic and Afaan Oromoo now show the Ethiopian
/// date first (November 19, 2026 is Hidar 10, 2019) with the Gregorian month
/// and day in brackets; English is unchanged. The trip departure day is
/// covered in trucker_hero_card_test.dart, the boards' days in
/// board_day_test.dart.
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

  // The date each language shows for [day].
  const dates = {
    'om': 'Sadaasa 10, 2019 (Sad 19)',
    'am': 'ኅዳር 10፣ 2019 (ኖቬም 19)',
    'en': 'Nov 19, 2026',
  };

  for (final MapEntry(key: language, value: date) in dates.entries) {
    testWidgets('the payout date in $language', (tester) async {
      await tester.pumpWidget(
        host(Locale(language), PayoutExpectedText(payoutAt: day)),
      );
      await tester.pumpAndSettle();

      final l10n = lookupAppLocalizations(Locale(language));
      expect(find.text(l10n.escrowPayoutExpectedOn(date)), findsOneWidget);
      if (language != 'en') expect(find.textContaining('Nov'), findsNothing);
    });

    testWidgets('the order card day in $language', (tester) async {
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
        host(Locale(language), OrderCard(order: order)),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining(date), findsOneWidget);
      if (language != 'en') expect(find.textContaining('Nov'), findsNothing);
    });
  }

  testWidgets('a payout due at 23:00 UTC is the next day in Ethiopia',
      (tester) async {
    // 02:00 on November 20 in Ethiopia: Hidar 11.
    final late = DateTime.utc(2026, 11, 19, 23);
    await tester.pumpWidget(
      host(const Locale('am'), PayoutExpectedText(payoutAt: late)),
    );
    await tester.pumpAndSettle();

    final l10n = lookupAppLocalizations(const Locale('am'));
    expect(
      find.text(l10n.escrowPayoutExpectedOn('ኅዳር 11፣ 2019 (ኖቬም 20)')),
      findsOneWidget,
    );
  });
}
