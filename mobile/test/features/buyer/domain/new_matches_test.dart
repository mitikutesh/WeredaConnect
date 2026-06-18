import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/new_matches.dart';

/// Pure unit tests for the count-delta "new matches" logic.
void main() {
  BuyerIntent intent({int matchCount = 0, int lastSeenMatchCount = 0}) {
    return BuyerIntent()
      ..intentId = 'i'
      ..buyerId = 'b'
      ..cropType = 'wheat'
      ..quantityNeeded = 1
      ..maxPricePerKg = 1
      ..deliveryWoreda = 'Adama'
      ..status = BuyerIntentStatus.active
      ..createdAt = DateTime.utc(2026)
      ..updatedAt = DateTime.utc(2026)
      ..matchCount = matchCount
      ..lastSeenMatchCount = lastSeenMatchCount
      ..version = 1;
  }

  test('a never-viewed intent treats all of its matches as new', () {
    expect(newMatchCount(intent(matchCount: 3)), 3);
    expect(hasNewMatches(intent(matchCount: 3)), isTrue);
  });

  test('no new matches when the count equals what was last seen', () {
    final i = intent(matchCount: 5, lastSeenMatchCount: 5);
    expect(newMatchCount(i), 0);
    expect(hasNewMatches(i), isFalse);
  });

  test('the positive delta is the number of unseen matches', () {
    expect(newMatchCount(intent(matchCount: 8, lastSeenMatchCount: 5)), 3);
  });

  test('clamps to zero when the count drops below what was seen', () {
    final i = intent(matchCount: 3, lastSeenMatchCount: 5);
    expect(newMatchCount(i), 0);
    expect(hasNewMatches(i), isFalse);
  });

  test('zero matches is never new', () {
    expect(hasNewMatches(intent()), isFalse);
  });
}
