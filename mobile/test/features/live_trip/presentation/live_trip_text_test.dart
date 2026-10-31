import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/presentation/live_trip_text.dart';

/// The boards write long distances in whole km ("47 km to go", "134 km
/// driven") and short ones with one decimal ("3.2 km away").
void main() {
  group('kmText', () {
    test('whole km from 10 km', () {
      expect(kmText(166.3), '166');
      expect(kmText(47.2), '47');
      expect(kmText(96.5), '97');
      expect(kmText(10), '10');
      expect(kmText(1234.4), '1,234');
    });

    test('one decimal below 10 km, none when it is whole', () {
      expect(kmText(3.2), '3.2');
      expect(kmText(0.8), '0.8');
      expect(kmText(5), '5');
      expect(kmText(9.96), '10');
      expect(kmText(0), '0');
    });
  });
}
