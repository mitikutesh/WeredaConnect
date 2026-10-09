import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/network/api_client.dart';

/// ESC-44: the order checkout measures its window against the server's
/// clock, read from the `Date` header.
void main() {
  group('parseHttpDate', () {
    test('parses an HTTP date as UTC', () {
      final date = parseHttpDate('Fri, 09 Oct 2026 10:15:30 GMT');

      expect(date, DateTime.utc(2026, 10, 9, 10, 15, 30));
      expect(date!.isUtc, isTrue);
    });

    test('null for a missing or malformed header', () {
      expect(parseHttpDate(null), isNull);
      expect(parseHttpDate(''), isNull);
      expect(parseHttpDate('2026-10-09T10:15:30Z'), isNull);
      expect(parseHttpDate('Fri, 09 Oct 2026 10:15:30 EAT'), isNull);
      expect(parseHttpDate('Fri, 99 Foo 2026 10:15:30 GMT'), isNull);
    });
  });
}
