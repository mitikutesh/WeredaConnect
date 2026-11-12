import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/owner_reputation_wire.dart';
import 'package:wereda_connect/core/models/trip.dart';

/// ESC-31: the owner's public reputation copy on a server crop or trip row
/// (reputation-api.md section 7).
void main() {
  const full = <String, dynamic>{
    'ownerTier': 'low',
    'ownerEffectiveRating': 4.2,
    'ownerRatingCount': 12,
    'ownerReliabilityPct': 83,
    'ownerReputationAt': '2026-11-07T09:00:00.000Z',
  };

  test('reads all five fields', () {
    final fields = ownerReputationFromWire(full);
    expect(fields.tier, 'low');
    expect(fields.effectiveRating, 4.2);
    expect(fields.ratingCount, 12);
    expect(fields.reliabilityPct, 83);
    expect(fields.at, DateTime.utc(2026, 11, 7, 9));
  });

  test('a row without them (the flag is off) reads all null', () {
    final fields = ownerReputationFromWire(const {'cropId': 'crop-1'});
    expect(fields.tier, isNull);
    expect(fields.effectiveRating, isNull);
    expect(fields.ratingCount, isNull);
    expect(fields.reliabilityPct, isNull);
    expect(fields.at, isNull);
  });

  test('an owner without ratings has a tier and a count but no rating', () {
    final fields = ownerReputationFromWire(const {
      'ownerTier': 'good',
      'ownerRatingCount': 0,
      'ownerReputationAt': '2026-11-07T09:00:00.000Z',
    });
    expect(fields.tier, 'good');
    expect(fields.effectiveRating, isNull);
    expect(fields.ratingCount, 0);
    expect(fields.reliabilityPct, isNull);
  });

  test('a whole number rating and values of the wrong type', () {
    final fields = ownerReputationFromWire(const {
      'ownerTier': 7,
      'ownerEffectiveRating': 4,
      'ownerRatingCount': '12',
      'ownerReliabilityPct': 83.0,
      'ownerReputationAt': 'not a date',
    });
    expect(fields.tier, isNull);
    expect(fields.effectiveRating, 4.0);
    expect(fields.ratingCount, isNull);
    expect(fields.reliabilityPct, 83);
    expect(fields.at, isNull);
  });

  test('a crop takes the copy and loses it when the server sends none', () {
    final crop = CropListing()..readOwnerReputation(full);
    expect(crop.ownerTier, 'low');
    expect(crop.ownerEffectiveRating, 4.2);
    expect(crop.ownerRatingCount, 12);
    expect(crop.ownerReliabilityPct, 83);
    expect(crop.ownerReputationAt, DateTime.utc(2026, 11, 7, 9));

    crop.readOwnerReputation(const {});
    expect(crop.ownerTier, isNull);
    expect(crop.ownerEffectiveRating, isNull);
    expect(crop.ownerRatingCount, isNull);
    expect(crop.ownerReliabilityPct, isNull);
    expect(crop.ownerReputationAt, isNull);
  });

  test('a trip takes the copy and loses it when the server sends none', () {
    final trip = Trip()..readOwnerReputation(full);
    expect(trip.ownerTier, 'low');
    expect(trip.ownerEffectiveRating, 4.2);
    expect(trip.ownerRatingCount, 12);
    expect(trip.ownerReliabilityPct, 83);
    expect(trip.ownerReputationAt, DateTime.utc(2026, 11, 7, 9));

    trip.readOwnerReputation(const {});
    expect(trip.ownerTier, isNull);
    expect(trip.ownerReputationAt, isNull);
  });
}
