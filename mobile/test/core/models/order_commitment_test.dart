import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';

Order _order(
  OrderStatus status, {
  bool trip = true,
  bool commitments = true,
  String? farmer,
  String? trucker,
}) =>
    Order()
      ..orderId = 'order-1'
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
      ..createdAt = DateTime.utc(2026, 10)
      ..version = 1
      ..tripId = trip ? 'trip-1' : null
      ..truckerId = trip ? 'trucker-1' : null
      ..fulfillment = commitments ? (trip ? 'trucker' : 'self_pickup') : null
      ..farmerResponse = farmer
      ..truckerResponse = trucker;

/// ESC-15: the client's reading of the sequential commitments, mirroring the
/// server's OrderCommitment (order-lifecycle.md 2.0.0).
void main() {
  final now = DateTime.utc(2026, 10, 27, 8);

  group('responses of a row the server never stamped', () {
    test('the farmer decides while funded or awaiting, else accepted', () {
      expect(farmerResponseOf(_order(OrderStatus.funded)), 'pending');
      expect(
        farmerResponseOf(_order(OrderStatus.awaitingTransport)),
        'pending',
      );
      expect(farmerResponseOf(_order(OrderStatus.accepted)), 'accepted');
    });

    test('the trucker is not asked yet, or accepted (K2), or there is none',
        () {
      expect(truckerResponseOf(_order(OrderStatus.funded)), 'not_asked');
      expect(truckerResponseOf(_order(OrderStatus.accepted)), 'accepted');
      expect(
        truckerResponseOf(_order(OrderStatus.funded, trip: false)),
        'none',
      );
    });

    test('a stored answer wins', () {
      expect(
        truckerResponseOf(_order(OrderStatus.funded, trucker: 'pending')),
        'pending',
      );
      expect(
        farmerResponseOf(_order(OrderStatus.funded, farmer: 'accepted')),
        'accepted',
      );
    });
  });

  group('who may answer now (the sequential rule)', () {
    test('the farmer first, only while pending', () {
      final order = _order(OrderStatus.funded, farmer: 'pending');
      expect(canFarmerRespond(order, 'farmer-1'), isTrue);
      expect(canFarmerRespond(order, 'buyer-1'), isFalse);
      expect(
        canFarmerRespond(order..farmerResponse = 'accepted', 'farmer-1'),
        isFalse,
      );
    });

    test('the trucker never before the farmer accepted', () {
      final notAsked = _order(
        OrderStatus.funded,
        farmer: 'pending',
        trucker: 'not_asked',
      );
      expect(canTruckerRespond(notAsked, 'trucker-1'), isFalse);
      final asked = _order(
        OrderStatus.funded,
        farmer: 'accepted',
        trucker: 'pending',
      );
      expect(canTruckerRespond(asked, 'trucker-1'), isTrue);
      expect(canTruckerRespond(asked, 'farmer-1'), isFalse);
      expect(canTruckerRespond(asked, ''), isFalse);
      expect(
        canTruckerRespond(asked..truckerResponse = 'declined', 'trucker-1'),
        isFalse,
      );
    });

    test('the committed trucker may drop out before pickup', () {
      final accepted = _order(
        OrderStatus.accepted,
        farmer: 'accepted',
        trucker: 'accepted',
      );
      expect(canTruckerDropOut(accepted, 'trucker-1'), isTrue);
      expect(
        canTruckerDropOut(
          _order(OrderStatus.pickupConfirmed, trucker: 'accepted'),
          'trucker-1',
        ),
        isFalse,
      );
      // A server without commitments has no drop-out command.
      expect(
        canTruckerDropOut(
          _order(OrderStatus.accepted, commitments: false),
          'trucker-1',
        ),
        isFalse,
      );
    });

    test('the deadline the viewer owes', () {
      final farmerDue = DateTime.utc(2026, 10, 28);
      final truckerDue = DateTime.utc(2026, 10, 29);
      final choiceDue = DateTime.utc(2026, 10, 30);
      final order = _order(OrderStatus.funded, farmer: 'pending')
        ..farmerDeadlineAt = farmerDue
        ..truckerDeadlineAt = truckerDue
        ..buyerChoiceDeadlineAt = choiceDue;
      expect(commitmentDeadlineFor(order, 'farmer-1'), farmerDue);
      expect(commitmentDeadlineFor(order, 'trucker-1'), isNull);
      order
        ..farmerResponse = 'accepted'
        ..truckerResponse = 'pending';
      expect(commitmentDeadlineFor(order, 'trucker-1'), truckerDue);
      expect(commitmentDeadlineFor(order, 'buyer-1'), isNull);
      order.status = OrderStatus.awaitingTransport;
      expect(commitmentDeadlineFor(order, 'buyer-1'), choiceDue);
    });
  });

  group('the farmer\'s accept, as the server decides it', () {
    test('self-pickup is agreed at once', () {
      expect(
        farmerAcceptStatus(_order(OrderStatus.funded, trip: false)),
        OrderStatus.accepted,
      );
    });

    test('a trip order waits for the trucker', () {
      expect(
        farmerAcceptStatus(_order(OrderStatus.funded, trucker: 'not_asked')),
        OrderStatus.funded,
      );
    });

    test('a trip already full or cancelled lands with the buyer', () {
      for (final trucker in ['failed_capacity', 'cancelled']) {
        expect(
          farmerAcceptStatus(_order(OrderStatus.funded, trucker: trucker)),
          OrderStatus.awaitingTransport,
          reason: trucker,
        );
      }
    });

    test('without commitments the accept is final', () {
      expect(
        farmerAcceptStatus(_order(OrderStatus.funded, commitments: false)),
        OrderStatus.accepted,
      );
    });
  });

  group('who may cancel', () {
    test('the buyer: before paying and after the transport fell through', () {
      for (final status in [
        OrderStatus.created,
        OrderStatus.pendingPayment,
        OrderStatus.paymentFailed,
        OrderStatus.awaitingTransport,
      ]) {
        expect(
          canCancelOrder(_order(status), 'buyer-1', now),
          isTrue,
          reason: status.name,
        );
      }
      for (final status in [
        OrderStatus.paymentSubmitted,
        OrderStatus.funded,
        OrderStatus.accepted,
        OrderStatus.delivered,
        OrderStatus.unknown,
      ]) {
        expect(
          canCancelOrder(_order(status), 'buyer-1', now),
          isFalse,
          reason: status.name,
        );
      }
    });

    test('the buyer: a funded order only on a server without commitments', () {
      expect(
        canCancelOrder(
          _order(OrderStatus.funded, commitments: false),
          'buyer-1',
          now,
        ),
        isTrue,
      );
    });

    test('the buyer: an accepted order after its pickup deadline (ESC-06)', () {
      final order = _order(OrderStatus.accepted)
        ..pickupDeadlineAt = now.subtract(const Duration(minutes: 1));
      expect(canCancelOrder(order, 'buyer-1', now), isTrue);
    });

    test('the farmer: awaiting transport, or accepted with commitments', () {
      expect(
        canCancelOrder(_order(OrderStatus.awaitingTransport), 'farmer-1', now),
        isTrue,
      );
      expect(
        canCancelOrder(_order(OrderStatus.accepted), 'farmer-1', now),
        isTrue,
      );
      expect(
        canCancelOrder(
          _order(OrderStatus.accepted, commitments: false),
          'farmer-1',
          now,
        ),
        isFalse,
      );
      expect(
        canCancelOrder(_order(OrderStatus.funded), 'farmer-1', now),
        isFalse,
      );
    });

    test('nobody else', () {
      for (final userId in ['trucker-1', 'someone', '']) {
        expect(
          canCancelOrder(_order(OrderStatus.created), userId, now),
          isFalse,
          reason: userId,
        );
      }
    });
  });

  test('the commitment fields are read from a server row', () {
    final order = _order(OrderStatus.funded, commitments: false);
    applyOrderCommitmentFields(order, {
      'fulfillment': 'trucker',
      'farmerResponse': 'accepted',
      'truckerResponse': 'pending',
      'farmerDeadlineAt': '2026-10-28T08:00:00.0000000Z',
      'truckerDeadlineAt': '2026-10-29T08:00:00Z',
      'buyerChoiceDeadlineAt': null,
      'transportFailure': '',
      'declineReason': 'price',
      'cancelledBy': 'system',
    });
    expect(order.fulfillment, 'trucker');
    expect(order.farmerResponse, 'accepted');
    expect(order.truckerResponse, 'pending');
    expect(order.farmerDeadlineAt, DateTime.utc(2026, 10, 28, 8));
    expect(order.farmerDeadlineAt!.isUtc, isTrue);
    expect(order.truckerDeadlineAt, DateTime.utc(2026, 10, 29, 8));
    expect(order.buyerChoiceDeadlineAt, isNull);
    expect(order.transportFailure, isNull);
    expect(order.declineReason, 'price');
    expect(order.cancelledBy, 'system');
    expect(orderUsesCommitments(order), isTrue);
  });

  test('an older server sends none of them', () {
    final order = _order(OrderStatus.funded, commitments: false);
    applyOrderCommitmentFields(order, const {});
    expect(order.fulfillment, isNull);
    expect(order.farmerResponse, isNull);
    expect(orderUsesCommitments(order), isFalse);
  });
}
