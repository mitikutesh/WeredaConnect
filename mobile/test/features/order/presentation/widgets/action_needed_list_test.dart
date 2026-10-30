import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/order/presentation/widgets/action_needed_list.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

Order _order(
  String id,
  OrderStatus status, {
  String? farmer,
  String? trucker,
  bool trip = true,
  DateTime? farmerDue,
  DateTime? truckerDue,
  DateTime? choiceDue,
  DateTime? createdAt,
}) =>
    Order()
      ..orderId = id
      ..listingId = 'listing-1'
      ..buyerId = 'buyer-1'
      ..farmerId = 'farmer-1'
      ..buyerName = 'Buyer'
      ..farmerName = 'Farmer'
      ..cropType = 'teff'
      ..quantity = 500
      ..pricePerUnit = 45
      ..totalPrice = 22500
      ..status = status
      ..createdAt = createdAt ?? DateTime.utc(2026, 10)
      ..version = 1
      ..tripId = trip ? 'trip-1' : null
      ..truckerId = trip ? 'trucker-1' : null
      ..fulfillment = trip ? 'trucker' : 'self_pickup'
      ..farmerResponse = farmer
      ..truckerResponse = trucker
      ..farmerDeadlineAt = farmerDue
      ..truckerDeadlineAt = truckerDue
      ..buyerChoiceDeadlineAt = choiceDue;

List<(String, ActionNeededKind)> _kinds(List<ActionNeededItem> items) =>
    [for (final i in items) (i.order.orderId, i.kind)];

/// ESC-15/21: the Home cards: each role sees only what waits for it.
void main() {
  final orders = [
    _order(
      'funded-new',
      OrderStatus.funded,
      farmer: 'pending',
      trucker: 'not_asked',
      farmerDue: DateTime.utc(2026, 10, 28),
    ),
    _order(
      'funded-asked',
      OrderStatus.funded,
      farmer: 'accepted',
      trucker: 'pending',
      truckerDue: DateTime.utc(2026, 10, 27),
    ),
    _order(
      'awaiting',
      OrderStatus.awaitingTransport,
      farmer: 'accepted',
      trucker: 'declined',
      choiceDue: DateTime.utc(2026, 10, 29),
    ),
    _order(
      'agreed',
      OrderStatus.accepted,
      farmer: 'accepted',
      trucker: 'accepted',
    ),
    _order('unpaid', OrderStatus.created),
    _order('failed', OrderStatus.paymentFailed),
    _order('arrived', OrderStatus.delivered),
    _order('settled', OrderStatus.settled),
  ];

  test('the farmer answers new orders and keeps the crops of a failed trip',
      () {
    expect(
      _kinds(
        actionNeededItems(
          orders: orders,
          userId: 'farmer-1',
          role: UserRole.farmer,
        ),
      ),
      [
        ('funded-new', ActionNeededKind.answerOrder),
        ('awaiting', ActionNeededKind.keepCrops),
      ],
    );
  });

  test('the trucker answers once asked and sets up agreed deliveries', () {
    expect(
      _kinds(
        actionNeededItems(
          orders: orders,
          userId: 'trucker-1',
          role: UserRole.trucker,
        ),
      ),
      [
        ('funded-asked', ActionNeededKind.answerTransport),
        ('agreed', ActionNeededKind.setUpDelivery),
      ],
    );
    // Not asked yet: nothing for the trucker on 'funded-new' (the sequential rule).
    expect(
      actionNeededItems(
        orders: orders,
        userId: 'trucker-1',
        role: UserRole.trucker,
        ordersWithDelivery: {'agreed'},
      ).map((i) => i.order.orderId),
      ['funded-asked'],
    );
  });

  test('the buyer pays, decides after a failed transport, checks a delivery',
      () {
    final items = actionNeededItems(
      orders: orders,
      userId: 'buyer-1',
      role: UserRole.buyer,
    );
    expect(
      _kinds(items).toSet(),
      {
        ('awaiting', ActionNeededKind.decideTransport),
        ('unpaid', ActionNeededKind.pay),
        ('failed', ActionNeededKind.payAgain),
        ('arrived', ActionNeededKind.checkDelivery),
      },
    );
    // A deadline sorts first.
    expect(items.first.order.orderId, 'awaiting');
    expect(items.first.dueAt, DateTime.utc(2026, 10, 29));
  });

  test('another user\'s orders never show', () {
    for (final role in UserRole.values) {
      expect(
        actionNeededItems(orders: orders, userId: 'someone-else', role: role),
        isEmpty,
        reason: role.name,
      );
      expect(
        actionNeededItems(orders: orders, userId: '', role: role),
        isEmpty,
      );
    }
  });

  test('the most urgent deadline comes first, then the newest order', () {
    final farmerOrders = [
      _order(
        'later',
        OrderStatus.funded,
        farmer: 'pending',
        farmerDue: DateTime.utc(2026, 10, 30),
      ),
      _order(
        'sooner',
        OrderStatus.funded,
        farmer: 'pending',
        farmerDue: DateTime.utc(2026, 10, 28),
      ),
      _order(
        'no-stamp-old',
        OrderStatus.funded,
        createdAt: DateTime.utc(2026, 9),
      ),
      _order(
        'no-stamp-new',
        OrderStatus.funded,
        createdAt: DateTime.utc(2026, 10, 2),
      ),
    ];
    expect(
      actionNeededItems(
        orders: farmerOrders,
        userId: 'farmer-1',
        role: UserRole.farmer,
      ).map((i) => i.order.orderId),
      ['sooner', 'later', 'no-stamp-new', 'no-stamp-old'],
    );
  });

  // Trucker home board: "Wheat · 1,500 kg · Thu 30 Oct trip"; the farmer
  // and buyer cards name the other party first.
  group('fact line', () {
    final en = lookupAppLocalizations(const Locale('en'));
    final order = _order('o', OrderStatus.funded)
      ..buyerName = 'Abebe Trading'
      ..quantity = 1500;

    test('the trucker reads the crop, the weight and the trip day', () {
      expect(
        actionNeededFacts(order, UserRole.trucker, en),
        'Teff · 1,500 kg',
      );
      final dated = _order('d', OrderStatus.funded)
        ..quantity = 1500
        ..pickupDate = DateTime.utc(2026, 10, 30, 3);
      expect(
        actionNeededFacts(dated, UserRole.trucker, en),
        'Teff · 1,500 kg · ${en.orderTripOn('Fri, Oct 30')}',
      );
    });

    test('the farmer reads the buyer first', () {
      expect(
        actionNeededFacts(order, UserRole.farmer, en),
        'Abebe Trading · Teff · 1,500 kg',
      );
    });
  });
}
