import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

Order _order(
  OrderStatus status, {
  String? farmerResponse,
  double? cropCost,
  double? transportCost,
  double? orderTotal,
}) =>
    Order()
      ..farmerResponse = farmerResponse
      ..cropCost = cropCost
      ..transportCost = transportCost
      ..orderTotal = orderTotal
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
  bool isTruckerView = false,
  String? farmerResponse,
  Order? order,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(
        body: OrderCard(
          order: order ?? _order(status, farmerResponse: farmerResponse),
          isFarmerView: isFarmerView,
          isTruckerView: isTruckerView,
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

  group('OrderCard crop name (SIM-22)', () {
    testWidgets('title shows the localized crop name, not the stored key',
        (tester) async {
      await _pumpCard(tester, status: OrderStatus.funded, isFarmerView: true);
      final l10n = l10nOf(tester);
      expect(find.textContaining(l10n.cropTypeWheat), findsOneWidget);
      expect(find.textContaining('wheat'), findsNothing);
    });
  });

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

    // ESC-15: a trip order stays funded while the trucker is asked.
    testWidgets('hides Accept/Reject once the farmer accepted a funded order',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.funded,
        isFarmerView: true,
        farmerResponse: 'accepted',
      );
      final l10n = l10nOf(tester);
      expect(find.text(l10n.acceptOrder), findsNothing);
      expect(find.text(l10n.rejectOrder), findsNothing);
    });

    testWidgets('labels awaiting transport and an unknown status',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.awaitingTransport,
        isFarmerView: true,
      );
      expect(find.text(l10nOf(tester).statusAwaitingTransport), findsOneWidget);
      await _pumpCard(tester, status: OrderStatus.unknown, isFarmerView: true);
      expect(find.text(l10nOf(tester).orderStatusUpdating), findsOneWidget);
      expect(find.text(l10nOf(tester).acceptOrder), findsNothing);
    });

    testWidgets('never shows Accept/Reject for a non-farmer, even when funded',
        (tester) async {
      await _pumpCard(tester, status: OrderStatus.funded, isFarmerView: false);
      final l10n = l10nOf(tester);
      expect(find.text(l10n.acceptOrder), findsNothing);
      expect(find.text(l10n.rejectOrder), findsNothing);
    });
  });

  group('OrderCard money and quantity (Fieldway boards)', () {
    testWidgets('amount is grouped with the unit after, quantity in kg',
        (tester) async {
      await _pumpCard(tester, status: OrderStatus.funded, isFarmerView: false);
      final l10n = l10nOf(tester);

      expect(find.text('5,000 ETB'), findsOneWidget);
      expect(find.text('${l10n.cropTypeWheat} · 100 kg'), findsOneWidget);
      // The other party stands alone on its line (the date moved under the
      // amount), so a deleted party still reads "Farmer: Deleted user".
      expect(find.text('${l10n.farmer}: Farmer'), findsOneWidget);
      expect(find.text(l10n.statusFunded), findsOneWidget);
    });
  });

  // F-1: each party reads its own money, never the buyer's total next to
  // the farmer's Accept.
  group('OrderCard amount per role (F-1)', () {
    // 100 kg x 50 ETB = 5,000 crop, 400 transport, 250 commission.
    Order tripOrder() => _order(
          OrderStatus.funded,
          cropCost: 5000,
          transportCost: 400,
          orderTotal: 5650,
        );

    testWidgets('farmer: the crop money as You receive, no transport line',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.funded,
        isFarmerView: true,
        order: tripOrder(),
      );
      final l10n = l10nOf(tester);

      expect(find.text(l10n.orderYouReceive), findsOneWidget);
      expect(find.text('5,000 ETB'), findsOneWidget);
      expect(find.text('5,650 ETB'), findsNothing);
      expect(find.textContaining(l10n.transportCost), findsNothing);
      // The Accept the amount sits next to is still there.
      expect(find.text(l10n.acceptOrder), findsOneWidget);
    });

    testWidgets('farmer, legacy order without cropCost: weight times price',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.funded,
        isFarmerView: true,
        order: _order(OrderStatus.funded, orderTotal: 5650)..totalPrice = 5650,
      );

      expect(find.text('5,000 ETB'), findsOneWidget);
      expect(find.text('5,650 ETB'), findsNothing);
    });

    testWidgets('trucker: the transport pay, no transport line',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.funded,
        isFarmerView: false,
        isTruckerView: true,
        order: tripOrder(),
      );
      final l10n = l10nOf(tester);

      expect(find.text(l10n.orderYourTransportPay), findsOneWidget);
      expect(find.text('400 ETB'), findsOneWidget);
      expect(find.text('5,650 ETB'), findsNothing);
      expect(find.textContaining(l10n.transportCost), findsNothing);
      expect(find.text(l10n.acceptOrder), findsNothing);
    });

    testWidgets('buyer: the order total with the transport line, unchanged',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.funded,
        isFarmerView: false,
        order: tripOrder(),
      );
      final l10n = l10nOf(tester);

      expect(find.text('5,650 ETB'), findsOneWidget);
      expect(
        find.text('${l10n.transportCost}: 400 ETB'),
        findsOneWidget,
      );
      expect(find.text(l10n.orderYouReceive), findsNothing);
      expect(find.text(l10n.orderYourTransportPay), findsNothing);
    });
  });
}
