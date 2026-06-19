import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

Order _order(OrderStatus status) => Order()
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
  ..status = status
  ..createdAt = DateTime(2026)
  ..version = 1;

Future<void> _pumpCard(
  WidgetTester tester, {
  required OrderStatus status,
  required bool isFarmerView,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(
        body: OrderCard(
          order: _order(status),
          isFarmerView: isFarmerView,
          onAccept: () {},
          onReject: () {},
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  AppLocalizations l10nOf(WidgetTester tester) =>
      AppLocalizations.of(tester.element(find.byType(OrderCard)))!;

  group('OrderCard farmer accept/reject visibility', () {
    // The farmer can only act once the order is funded (buyer paid into escrow):
    // this is the only valid transition (funded → accepted/rejected) across the
    // detail screen, both repos, and the server. The list card must agree.
    testWidgets('shows Accept/Reject for a farmer when the order is funded',
        (tester) async {
      await _pumpCard(tester, status: OrderStatus.funded, isFarmerView: true);
      final l10n = l10nOf(tester);
      expect(find.text(l10n.acceptOrder), findsOneWidget);
      expect(find.text(l10n.rejectOrder), findsOneWidget);
    });

    testWidgets(
        'hides Accept/Reject for a farmer when the order is only created '
        '(buyer has not funded yet)', (tester) async {
      await _pumpCard(tester, status: OrderStatus.created, isFarmerView: true);
      final l10n = l10nOf(tester);
      expect(find.text(l10n.acceptOrder), findsNothing);
      expect(find.text(l10n.rejectOrder), findsNothing);
    });

    testWidgets('never shows Accept/Reject for a non-farmer, even when funded',
        (tester) async {
      await _pumpCard(tester, status: OrderStatus.funded, isFarmerView: false);
      final l10n = l10nOf(tester);
      expect(find.text(l10n.acceptOrder), findsNothing);
      expect(find.text(l10n.rejectOrder), findsNothing);
    });
  });
}
