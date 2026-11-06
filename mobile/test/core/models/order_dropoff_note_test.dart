import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_dropoff_note.dart';

/// LT-12: the client's mirror of the server's drop-off note rules
/// (server/src/Helpers/OrderDropoffNote.cs): the 80 character limit counted
/// in user-visible characters, the phone number screen and the edit window.
void main() {
  group('length, counted as the server counts it', () {
    test('80 characters pass, 81 are too long', () {
      expect(dropoffNoteProblem('a' * 80), isNull);
      expect(dropoffNoteProblem('a' * 81), DropoffNoteProblem.tooLong);
    });

    test('an Amharic syllable is one character', () {
      final eighty = 'ሰ' * 80;
      expect(dropoffNoteLength(eighty), 80);
      expect(dropoffNoteProblem(eighty), isNull);
      expect(dropoffNoteProblem('ሰ' * 81), DropoffNoteProblem.tooLong);
      expect(dropoffNoteLength('መርካቶ፣ አንዋር መስጊድ አጠገብ'), 20);
    });

    test('an emoji with a skin tone is one character', () {
      const thumbs = '\u{1F44D}\u{1F3FD}';
      expect(thumbs.length, 4);
      expect(dropoffNoteLength(thumbs), 1);
      expect(dropoffNoteProblem('${'a' * 79}$thumbs'), isNull);
    });

    test('surrounding spaces do not count', () {
      expect(dropoffNoteProblem('  ${'a' * 80}  '), isNull);
    });
  });

  group('the phone number screen', () {
    for (final note in [
      'Merkato, call 0911 234 567',
      '+251-911-234567 at the gate',
      'Kality ፱፩፩፪፫፬፭፮፯',
    ]) {
      test(note, () {
        expect(dropoffNoteProblem(note), DropoffNoteProblem.phoneNumber);
      });
    }

    test('a landmark with numbers that are not a phone', () {
      expect(dropoffNoteProblem('Gate 2, block 14, near km 3'), isNull);
    });
  });

  test('normalize trims and turns blank into none', () {
    expect(normalizeDropoffNote('  Merkato  '), 'Merkato');
    expect(normalizeDropoffNote('   '), isNull);
    expect(normalizeDropoffNote(null), isNull);
    expect(dropoffNoteProblem(null), isNull);
  });

  // The server refuses any control character and U+2028/U+2029 for good
  // (OrderDropoffNote.ValidationError), which would remove an order queued
  // offline: a pasted tab or line break becomes one space here instead.
  group('one line, as the server takes it', () {
    for (final (input, expected) in [
      ('Merkato\tgate 2', 'Merkato gate 2'),
      ('Merkato\r\nnear the mosque', 'Merkato near the mosque'),
      ('Merkato \n\n near the mosque', 'Merkato near the mosque'),
      ('Kality\u2028gate 2\u2029blue door', 'Kality gate 2 blue door'),
      ('Saris\u0085bus\u0000stop\u007Fhere', 'Saris bus stop here'),
      ('\t\r\n\u2028', null),
      ('Gate  2,   block 14', 'Gate 2, block 14'),
      ('መርካቶ፣\tአንዋር መስጊድ', 'መርካቶ፣ አንዋር መስጊድ'),
    ]) {
      test(input.codeUnits.join(','), () {
        expect(normalizeDropoffNote(input), expected);
      });
    }

    test('nothing the server refuses is ever sent', () {
      bool refusedByServer(int unit) =>
          unit <= 0x1F ||
          (unit >= 0x7F && unit <= 0x9F) ||
          unit == 0x2028 ||
          unit == 0x2029;
      final every = String.fromCharCodes([
        for (var unit = 0; unit <= 0xA0; unit++) unit,
        0x2028,
        0x2029,
        0x41,
      ]);
      final note = normalizeDropoffNote(every)!;
      expect(note.codeUnits.where(refusedByServer), isEmpty);
    });

    test('counted and checked after it is made one line', () {
      // 94 characters as pasted (two tabs between words), 79 on one line.
      final words = List.filled(16, 'abcd').join('\t\t');
      expect(normalizeDropoffNote(words)!.length, 79);
      expect(dropoffNoteProblem(words), isNull);
      expect(dropoffNoteLength('  a\tb  '), 3);
    });
  });

  group('who may edit it, and until when', () {
    Order order(OrderStatus status, {bool trip = true}) => Order()
      ..orderId = 'order-1'
      ..buyerId = 'buyer-1'
      ..farmerId = 'farmer-1'
      ..tripId = trip ? 'trip-1' : null
      ..truckerId = trip ? 'trucker-1' : null
      ..status = status;

    for (final status in OrderStatus.values) {
      final before = {
        OrderStatus.created,
        OrderStatus.pendingPayment,
        OrderStatus.paymentSubmitted,
        OrderStatus.paymentFailed,
        OrderStatus.funded,
        OrderStatus.awaitingTransport,
        OrderStatus.accepted,
      }.contains(status);
      test('the buyer, ${status.name}: ${before ? 'yes' : 'no'}', () {
        expect(canEditDropoffNote(order(status), 'buyer-1'), before);
      });
    }

    test('never the farmer, the trucker or anyone else', () {
      for (final caller in ['farmer-1', 'trucker-1', 'someone', '']) {
        expect(canEditDropoffNote(order(OrderStatus.created), caller), isFalse);
      }
    });

    test('never on a self-pickup order (no drop-off)', () {
      expect(
        canEditDropoffNote(order(OrderStatus.created, trip: false), 'buyer-1'),
        isFalse,
      );
    });
  });
}
