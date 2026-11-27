import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

final _asciiLetter = RegExp('[A-Za-z]');

void main() {
  group('status parsing from API (SIM-58)', () {
    test('order statuses arrive in snake_case and map to the enum', () {
      expect(
        orderStatusFromApi('payment_submitted'),
        OrderStatus.paymentSubmitted,
      );
      expect(orderStatusFromApi('in_transit'), OrderStatus.inTransit);
      expect(
        orderStatusFromApi('pickup_confirmed'),
        OrderStatus.pickupConfirmed,
      );
    });

    test('every order status round-trips through its wire token', () {
      for (final s in OrderStatus.values) {
        expect(
          orderStatusFromApi(orderStatusToWire(s.name)),
          s,
          reason: 'wire token ${orderStatusToWire(s.name)}',
        );
      }
    });

    test('missing or unknown order status reads as unknown (Updating)', () {
      expect(orderStatusFromApi(null), OrderStatus.unknown);
      expect(orderStatusFromApi('bogus'), OrderStatus.unknown);
      expect(
        orderStatusFromApi('awaiting_transport'),
        OrderStatus.awaitingTransport,
      );
    });

    test('delivery statuses are camelCase with a pending fallback', () {
      expect(deliveryStatusFromApi('pickedUp'), DeliveryStatus.pickedUp);
      expect(deliveryStatusFromApi('delivered'), DeliveryStatus.delivered);
      expect(deliveryStatusFromApi(null), DeliveryStatus.pending);
      expect(deliveryStatusFromApi(''), DeliveryStatus.pending);
    });

    test('trip statuses are camelCase with an open fallback', () {
      expect(tripStatusFromApi('inTransit'), TripStatus.inTransit);
      expect(tripStatusFromApi('delivered'), TripStatus.delivered);
      expect(tripStatusFromApi(null), TripStatus.open);
      expect(tripStatusFromApi('bogus'), TripStatus.open);
    });
  });

  // SIM-33: the web At a glance tiles read one page; "20 of 33" read as 20.
  group('At a glance counts over pages (SIM-33)', () {
    test('hasMore wins when the server sends it', () {
      expect(apiPageHasMore({'hasMore': true}), isTrue);
      expect(apiPageHasMore({'hasMore': false, 'nextToken': 'abc'}), isFalse);
    });

    test('else a non-empty nextToken means another page', () {
      expect(apiPageHasMore({'nextToken': 'abc'}), isTrue);
      expect(apiPageHasMore({'nextToken': ''}), isFalse);
      expect(apiPageHasMore({'nextToken': null}), isFalse);
      expect(apiPageHasMore(<String, dynamic>{}), isFalse);
    });

    test('a count of one page of more reads "50+"', () {
      expect(glanceCountText(50, more: true), '50+');
      expect(glanceCountText(33, more: false), '33');
      expect(glanceCountText(0, more: false), '0');
    });
  });

  // FUP-126: the farmer's active and reserved counts (and the trucker's
  // matched count) came from the first page of 50 only.
  group('fetchApiPages: own lists over every page (FUP-126)', () {
    late List<Map<String, dynamic>?> sent;

    /// A fake GET answering [pages] in order, recording each query.
    Future<Map<String, dynamic>> Function(
      String path, {
      Map<String, dynamic>? queryParameters,
    }) server(List<Map<String, dynamic>> pages) {
      var next = 0;
      return (path, {queryParameters}) async {
        sent.add(queryParameters);
        return next < pages.length ? pages[next++] : <String, dynamic>{};
      };
    }

    Map<String, dynamic> crop(String id, String status) =>
        {'cropId': id, 'status': status};

    setUp(() => sent = []);

    test('follows nextToken to the last page and counts every row', () async {
      final get = server([
        {
          'crops': [crop('c1', 'available'), crop('c2', 'reserved')],
          'nextToken': 't1',
          'hasMore': true,
        },
        {
          'crops': [crop('c3', 'reserved')],
          'hasMore': false,
        },
      ]);

      final pages = await fetchApiPages(
        get,
        '/crops',
        listKey: 'crops',
        params: {'farmerId': 'farmer-1'},
        maxPages: 10,
      );

      expect(pages.items.map((c) => (c as Map)['cropId']), ['c1', 'c2', 'c3']);
      expect(
        pages.items.where((c) => (c as Map)['status'] == 'reserved').length,
        2,
      );
      expect(pages.more, isFalse);
      // The first read is the Recent line's (SIM-21 folding); the second
      // carries the token. The farmer id is never dropped or widened.
      expect(sent, [
        {'farmerId': 'farmer-1', 'limit': '50'},
        {'farmerId': 'farmer-1', 'limit': '50', 'nextToken': 't1'},
      ]);
    });

    test('stops at maxPages and reports more', () async {
      final get = server([
        for (var i = 0; i < 5; i++)
          {
            'trips': [
              {'tripId': 'trip-$i', 'status': 'matched'},
            ],
            'nextToken': 'token-$i',
          },
      ]);

      final pages = await fetchApiPages(
        get,
        '/trips',
        listKey: 'trips',
        params: {'truckerId': 'trucker-1'},
        maxPages: 3,
      );

      expect(pages.items, hasLength(3));
      expect(pages.more, isTrue);
      expect(sent, hasLength(3));
      expect(sent.every((q) => q!['truckerId'] == 'trucker-1'), isTrue);
    });

    test('an empty token ends the read', () async {
      final get = server([
        {
          'crops': [crop('c1', 'available')],
          'nextToken': '',
        },
      ]);

      final pages = await fetchApiPages(
        get,
        '/crops',
        listKey: 'crops',
        params: {'farmerId': 'farmer-1'},
        maxPages: 10,
      );

      expect(pages.items, hasLength(1));
      expect(pages.more, isFalse);
      expect(sent, hasLength(1));
    });

    test('a failed page (an empty map) ends the read', () async {
      final get = server([
        {
          'crops': [crop('c1', 'available')],
          'nextToken': 't1',
        },
        <String, dynamic>{},
      ]);

      final pages = await fetchApiPages(
        get,
        '/crops',
        listKey: 'crops',
        params: {'farmerId': 'farmer-1'},
        maxPages: 10,
      );

      expect(pages.items, hasLength(1));
      expect(pages.more, isFalse);
      expect(sent, hasLength(2));
    });

    test('reads the items envelope too', () async {
      final get = server([
        {
          'items': [
            {'orderId': 'o1'},
          ],
        },
      ]);

      final pages = await fetchApiPages(
        get,
        '/orders',
        listKey: 'orders',
        params: {'farmerId': 'farmer-1'},
        maxPages: 5,
      );

      expect(pages.items, hasLength(1));
    });
  });

  group('delivered activity mark (ESC-09)', () {
    test('delivered, disbursing and settled orders show the success mark', () {
      expect(
        OrderStatus.values.where(isDeliveredOrderActivity).toSet(),
        {OrderStatus.delivered, OrderStatus.disbursing, OrderStatus.settled},
      );
    });
  });

  group('activity descriptions (SIM-58)', () {
    final en = lookupAppLocalizations(const Locale('en'));
    final am = lookupAppLocalizations(const Locale('am'));
    final om = lookupAppLocalizations(const Locale('om'));

    test('en lines show the readable status label', () {
      expect(
        orderActivityDescription(en, 'Teff', OrderStatus.paymentSubmitted),
        'Orders: Teff (Payment Submitted)',
      );
      expect(
        tripActivityDescription(en, 'Adama', 'Hawassa', TripStatus.inTransit),
        'My Trips: Adama → Hawassa (In Transit)',
      );
      expect(
        en.deliveryStatusActivity(
          deliveryStatusLabel(DeliveryStatus.pickedUp, en),
        ),
        'Delivery Picked Up',
      );
    });

    test('am order lines are fully Amharic for every status', () {
      for (final s in OrderStatus.values) {
        final line = orderActivityDescription(am, 'ጤፍ', s);
        expect(line, contains(am.orders));
        expect(line, contains(orderStatusLabel(s, am)));
        expect(
          _asciiLetter.hasMatch(line),
          isFalse,
          reason: 'am order line leaks a token: "$line"',
        );
      }
    });

    test('am trip lines are fully Amharic for every status', () {
      for (final s in TripStatus.values) {
        final line = tripActivityDescription(am, 'አዳማ', 'ሀዋሳ', s);
        expect(line, contains(am.myTrips));
        expect(line, contains(tripStatusLabel(s, am)));
        expect(
          _asciiLetter.hasMatch(line),
          isFalse,
          reason: 'am trip line leaks a token: "$line"',
        );
      }
    });

    test('am delivery lines are fully Amharic for every status', () {
      for (final s in DeliveryStatus.values) {
        final label = deliveryStatusLabel(s, am);
        final line = am.deliveryStatusActivity(label);
        expect(line, contains(label));
        expect(
          _asciiLetter.hasMatch(line),
          isFalse,
          reason: 'am delivery line leaks a token: "$line"',
        );
      }
    });

    test('om order lines use the om label, never the enum or wire token', () {
      for (final s in OrderStatus.values) {
        final line = orderActivityDescription(om, 'Xaafii', s);
        expect(line, contains(orderStatusLabel(s, om)));
        _expectNoToken(line, [s.name, orderStatusToWire(s.name)]);
      }
    });

    test('om trip lines use the om label, never the enum name', () {
      for (final s in TripStatus.values) {
        final line = tripActivityDescription(om, 'Adaamaa', 'Hawaasaa', s);
        expect(line, contains(tripStatusLabel(s, om)));
        _expectNoToken(line, [s.name]);
      }
    });

    test('om delivery lines use the om label, never the enum name', () {
      for (final s in DeliveryStatus.values) {
        final label = deliveryStatusLabel(s, om);
        final line = om.deliveryStatusActivity(label);
        expect(line, contains(label));
        _expectNoToken(line, [s.name]);
      }
    });
  });
}

void _expectNoToken(String line, List<String> tokens) {
  for (final token in tokens) {
    expect(
      line.toLowerCase(),
      isNot(contains(token.toLowerCase())),
      reason: 'om line "$line" leaks the raw token "$token"',
    );
  }
}
