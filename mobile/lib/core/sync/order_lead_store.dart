import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// FUP-122: the last order lead the server reported on `GET /trips`
/// (`orderLeadHours`, trip-listing-api.md 2.3.0), kept for the phone's
/// offline match suggestions. The trip pull writes it and the Isar
/// `MatchingService` reads it, so a buyer offline is not offered a truck the
/// server's order create would refuse.
///
/// One app-wide value in SharedPreferences: a server setting, not the user's
/// data, so logout keeps it. Null means unknown (never pulled, a server from
/// before 2.3.0, or storage failing); matching then keeps the FUP-85 rule.
class OrderLeadStore {
  OrderLeadStore({Future<SharedPreferences> Function()? preferences})
      : _preferences = preferences ?? SharedPreferences.getInstance;

  final Future<SharedPreferences> Function() _preferences;

  static const String key = 'order_lead_hours_v1';

  /// The stored lead in hours, or null when unknown.
  Future<int?> read() async {
    try {
      final prefs = await _preferences();
      return prefs.getInt(key);
    } on Object catch (e) {
      debugPrint('[OrderLeadStore] read failed, lead unknown: $e');
      return null;
    }
  }

  /// Stores [hours]; null forgets the value (the server sent none). A failing
  /// store is logged and never fails the trip pull.
  Future<void> write(int? hours) async {
    try {
      final prefs = await _preferences();
      if (hours == null) {
        await prefs.remove(key);
      } else {
        await prefs.setInt(key, hours);
      }
    } on Object catch (e) {
      debugPrint('[OrderLeadStore] write failed: $e');
    }
  }
}
