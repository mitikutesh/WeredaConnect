import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

Order _order(OrderStatus status) => Order()
  ..orderId = 'o1'
  ..listingId = 'l1'
  ..buyerId = 'b1'
  ..farmerId = 'f1'
  ..truckerId = 't1'
  ..buyerName = 'Buyer'
  ..farmerName = 'Farmer'
  ..cropType = 'wheat'
  ..quantity = 100
  ..pricePerUnit = 50
  ..totalPrice = 5000
  ..status = status
  ..createdAt = DateTime(2026)
  ..version = 1;

/// FRD-06: an order whose escrow is held shows "Under Review" in the order
/// list for every party; the farmer keeps Reject but loses Accept, which the
/// server refuses while the escrow is held.
void main() {
  Future<void> pump(
    WidgetTester tester, {
    required OrderStatus status,
    required bool isFarmerView,
    required bool underReview,
    Locale locale = const Locale('en'),
  }) async {
    await tester.pumpWidget(
      MaterialApp(
        locale: locale,
        // The app's delegates: Afan Oromo has no built-in Material strings.
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: OrderCard(
            order: _order(status),
            isFarmerView: isFarmerView,
            underReview: underReview,
            onAccept: isFarmerView ? () {} : null,
            onReject: isFarmerView ? () {} : null,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  final en = lookupAppLocalizations(const Locale('en'));

  testWidgets('farmer, funded and under review: Reject only', (tester) async {
    await pump(
      tester,
      status: OrderStatus.funded,
      isFarmerView: true,
      underReview: true,
    );

    expect(find.text(en.escrowStatusHeld), findsOneWidget);
    expect(find.text(en.rejectOrder), findsOneWidget);
    expect(find.text(en.acceptOrder), findsNothing);
  });

  testWidgets('farmer, funded, not under review: both actions (control)',
      (tester) async {
    await pump(
      tester,
      status: OrderStatus.funded,
      isFarmerView: true,
      underReview: false,
    );

    expect(find.text(en.escrowStatusHeld), findsNothing);
    expect(find.text(en.rejectOrder), findsOneWidget);
    expect(find.text(en.acceptOrder), findsOneWidget);
  });

  // The buyer and the trucker use the non-farmer card.
  for (final status in const [OrderStatus.delivered, OrderStatus.disputed]) {
    testWidgets('buyer or trucker view, ${status.name}: the chip and status',
        (tester) async {
      await pump(
        tester,
        status: status,
        isFarmerView: false,
        underReview: true,
      );

      expect(find.text(en.escrowStatusHeld), findsOneWidget);
      expect(find.text(orderStatusLabel(status, en)), findsOneWidget);
      expect(find.byType(FilledButton), findsNothing);
    });
  }

  for (final locale in const [Locale('am'), Locale('om')]) {
    testWidgets('the chip is localized (${locale.languageCode})',
        (tester) async {
      await pump(
        tester,
        status: OrderStatus.delivered,
        isFarmerView: false,
        underReview: true,
        locale: locale,
      );

      expect(
        find.text(lookupAppLocalizations(locale).escrowStatusHeld),
        findsOneWidget,
      );
    });
  }
}
