import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/crop_status_wire.dart';

/// FUP-149: a server crop row is deleted by the server's own rule
/// (crop-listing-api.md 1.10.0): a `deletedAt` stamp, or `status: deleted`.
void main() {
  group('isDeletedCropRow', () {
    Map<String, dynamic> row(Map<String, dynamic> extra) => {
          'cropId': 'crop-1',
          'farmerId': 'farmer-1',
          'status': 'available',
          ...extra,
        };

    test('a deleted row with its stamp', () {
      expect(
        isDeletedCropRow(
          row({'status': 'deleted', 'deletedAt': '2026-11-12T08:00:00Z'}),
        ),
        isTrue,
      );
    });

    test('a row deleted by an update before the stamp was written', () {
      expect(isDeletedCropRow(row({'status': 'deleted'})), isTrue);
      expect(isDeletedCropRow(row({'status': 'Deleted'})), isTrue);
    });

    test('a stamped row whatever its status (a deleted account tombstone)', () {
      expect(
        isDeletedCropRow(row({'deletedAt': '2026-11-12T08:00:00Z'})),
        isTrue,
      );
    });

    for (final status in ['available', 'reserved', 'sold', 'expired', null]) {
      test('a live "$status" row is not deleted', () {
        expect(
          isDeletedCropRow(row({'status': status, 'deletedAt': null})),
          isFalse,
        );
      });
    }

    test('an empty stamp is no stamp', () {
      expect(isDeletedCropRow(row({'deletedAt': ''})), isFalse);
    });
  });
}
