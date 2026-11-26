import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/screens/home_screen.dart';

/// Home's Pending Orders tile on web reads the server's order rows: it
/// counts the funded orders still waiting for the farmer's answer, the same
/// orders the Orders tab's "Waiting for you" shows.
void main() {
  test('funded rows the farmer has not answered', () {
    final rows = [
      {'status': 'funded', 'farmerResponse': 'pending'},
      {'status': 'funded'},
      {'status': 'funded', 'farmerResponse': ''},
      {'status': 'funded', 'farmerResponse': 'accepted'},
      {'status': 'funded', 'farmerResponse': 'declined'},
      {'status': 'pending_payment'},
      {'status': 'created'},
      {'status': 'accepted', 'farmerResponse': 'accepted'},
      'not a row',
    ];
    expect(farmerTurnOrderCount(rows), 3);
  });

  test('none', () {
    expect(farmerTurnOrderCount(const []), 0);
  });
}
