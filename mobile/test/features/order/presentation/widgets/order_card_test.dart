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

    testWidgets('trucker, no transport cost on the order: no amount, not 0',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.funded,
        isFarmerView: false,
        isTruckerView: true,
        order: _order(OrderStatus.funded, cropCost: 5000, orderTotal: 5250),
      );
      final l10n = l10nOf(tester);

      expect(find.text('0 ETB'), findsNothing);
      expect(find.text(l10n.orderYourTransportPay), findsNothing);
      expect(find.text('5,250 ETB'), findsNothing);
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

  group('OrderCard impossible values (SIM-33)', () {
    testWidgets('a negative total reads as a dash, never as birr',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.created,
        isFarmerView: false,
        order: _order(OrderStatus.created, orderTotal: -470.6),
      );
      expect(find.text('-'), findsOneWidget);
      expect(find.textContaining('-470'), findsNothing);
      expect(find.textContaining('-471'), findsNothing);
    });

    testWidgets('a NaN amount reads as a dash and does not throw',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.created,
        isFarmerView: false,
        order: _order(OrderStatus.created, orderTotal: double.nan),
      );
      expect(tester.takeException(), isNull);
      expect(find.text('-'), findsOneWidget);
      expect(find.textContaining('NaN'), findsNothing);
    });

    testWidgets('a quantity of 0 kg or past the server cap is a dash',
        (tester) async {
      for (final kg in [0.0, -5.0, 2000000.0]) {
        await _pumpCard(
          tester,
          status: OrderStatus.created,
          isFarmerView: false,
          order: _order(OrderStatus.created)..quantity = kg,
        );
        final l10n = l10nOf(tester);
        expect(
          find.text('${l10n.cropTypeWheat} · -'),
          findsOneWidget,
          reason: '$kg kg',
        );
        expect(find.textContaining(' kg'), findsNothing, reason: '$kg kg');
      }
    });

    testWidgets('part of a kilo keeps its decimals, never "0 kg"',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.created,
        isFarmerView: false,
        order: _order(OrderStatus.created)..quantity = 0.5,
      );
      final l10n = l10nOf(tester);
      expect(find.text('${l10n.cropTypeWheat} · 0.5 kg'), findsOneWidget);
    });

    testWidgets('a user id where the name belongs reads as a dash',
        (tester) async {
      await _pumpCard(
        tester,
        status: OrderStatus.created,
        isFarmerView: false,
        order: _order(OrderStatus.created)
          ..farmerName = '3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b',
      );
      final l10n = l10nOf(tester);
      expect(find.text('${l10n.farmer}: -'), findsOneWidget);
      expect(find.textContaining('3f2b8c1e'), findsNothing);
    });
  });
}
