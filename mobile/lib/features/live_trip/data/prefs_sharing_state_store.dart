import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';

/// Keeps each trucker's sharing state in shared preferences (localStorage on
/// web), one small JSON entry per account. It holds trip ids, answers and
/// pause ends, never a position.
class PrefsSharingStateStore implements SharingStateStore {
  static const _keyPrefix = 'live_trip_sharing_';

  @override
  Future<SharingState> load(String userId) async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final raw = prefs.getString('$_keyPrefix$userId');
      if (raw == null) return const SharingState();
      return SharingState.fromJson(jsonDecode(raw));
    } on Object catch (e) {
      debugPrint('[LiveTrip] sharing state unreadable: $e');
      return const SharingState();
    }
  }

  @override
  Future<void> save(String userId, SharingState state) async {
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setString('$_keyPrefix$userId', jsonEncode(state.toJson()));
    } on Object catch (e) {
      debugPrint('[LiveTrip] sharing state not saved: $e');
    }
  }

  @override
  Future<void> forget(String userId) async {
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.remove('$_keyPrefix$userId');
    } on Object catch (e) {
      debugPrint('[LiveTrip] sharing state not removed: $e');
    }
  }
}
