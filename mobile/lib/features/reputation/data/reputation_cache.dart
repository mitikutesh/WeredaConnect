import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// A stored answer and when it was fetched.
typedef CachedReputationBody = ({
  Map<String, dynamic> body,
  DateTime fetchedAt
});

/// ESC-31: the signed-in farmer's or trucker's own reputation, kept for
/// offline reading (reputation-api.md section 13): the last
/// `GET /reputation/me` answer, the first page of their events, and the
/// text of appeals they have not sent yet.
///
/// In the app's key-value store (SharedPreferences: app-private storage on
/// Android, localStorage on web), like the live trip snapshots, so the
/// phone and the web behave the same and no Isar schema is needed for a
/// read-only copy. Keys carry the user id, so two people on one phone never
/// see each other's; logout and account deletion remove the user's entries
/// ([forgetUser]). A failing store is logged and reads as empty: it never
/// fails a screen.
class ReputationCache {
  ReputationCache({Future<SharedPreferences> Function()? preferences})
      : _preferences = preferences ?? SharedPreferences.getInstance;

  final Future<SharedPreferences> Function() _preferences;

  static const String keyPrefix = 'reputation_v1:';

  /// An appeal text is at most 500 characters on the server.
  static const int maxDraftLength = 500;

  static String _userPrefix(String userId) => '$keyPrefix$userId:';
  static String _ownKey(String userId) => '${_userPrefix(userId)}me';
  static String _eventsKey(String userId) => '${_userPrefix(userId)}events';
  static String _ratingKey(String userId) => '${_userPrefix(userId)}rating';
  static String _draftKey(String userId, String eventId) =>
      '${_userPrefix(userId)}draft:$eventId';

  Future<CachedReputationBody?> readOwn(String userId) =>
      _read(_ownKey(userId));

  Future<void> writeOwn(
    String userId,
    Map<String, dynamic> body,
    DateTime fetchedAt,
  ) =>
      _write(_ownKey(userId), body, fetchedAt);

  /// The first page of the user's events.
  Future<CachedReputationBody?> readEvents(String userId) =>
      _read(_eventsKey(userId));

  Future<void> writeEvents(
    String userId,
    Map<String, dynamic> body,
    DateTime fetchedAt,
  ) =>
      _write(_eventsKey(userId), body, fetchedAt);

  /// The user's own rating aggregate (`{average, count}`), kept beside the
  /// reputation answer for the Profile tile.
  Future<CachedReputationBody?> readRating(String userId) =>
      _read(_ratingKey(userId));

  Future<void> writeRating(
    String userId,
    Map<String, dynamic> body,
    DateTime fetchedAt,
  ) =>
      _write(_ratingKey(userId), body, fetchedAt);

  /// The unsent appeal text for [eventId], or null.
  Future<String?> readDraft(String userId, String eventId) async {
    try {
      final prefs = await _preferences();
      final text = prefs.getString(_draftKey(userId, eventId));
      return text == null || text.isEmpty ? null : text;
    } on Object catch (e) {
      debugPrint('[ReputationCache] draft read failed: $e');
      return null;
    }
  }

  /// Keeps [text] as the unsent appeal for [eventId]; an empty text forgets
  /// it.
  Future<void> writeDraft(String userId, String eventId, String text) async {
    try {
      final prefs = await _preferences();
      final key = _draftKey(userId, eventId);
      if (text.trim().isEmpty) {
        await prefs.remove(key);
      } else {
        await prefs.setString(
          key,
          text.length > maxDraftLength
              ? text.substring(0, maxDraftLength)
              : text,
        );
      }
    } on Object catch (e) {
      debugPrint('[ReputationCache] draft write failed: $e');
    }
  }

  /// Removes everything stored for [userId] (logout, account deleted). A
  /// failing store is logged: it never fails the sign-out.
  Future<void> forgetUser(String userId) async {
    try {
      final prefs = await _preferences();
      final prefix = _userPrefix(userId);
      for (final key in prefs.getKeys().toList()) {
        if (key.startsWith(prefix)) await prefs.remove(key);
      }
    } on Object catch (e) {
      debugPrint('[ReputationCache] forget failed: $e');
    }
  }

  /// [forgetUser] on the app's own store: what the AuthBloc's sign-out
  /// forgetter calls.
  static Future<void> forgetStoredUser(String userId) =>
      ReputationCache().forgetUser(userId);

  Future<CachedReputationBody?> _read(String key) async {
    try {
      final prefs = await _preferences();
      final raw = prefs.getString(key);
      if (raw == null) return null;
      Object? decoded;
      try {
        decoded = jsonDecode(raw);
      } on FormatException {
        decoded = null;
      }
      if (decoded is Map<String, dynamic>) {
        final body = decoded['body'];
        final fetchedAt =
            DateTime.tryParse(decoded['fetchedAt'] as String? ?? '');
        if (body is Map<String, dynamic> && fetchedAt != null) {
          return (body: body, fetchedAt: fetchedAt.toUtc());
        }
      }
      // An entry that no longer parses is removed.
      await prefs.remove(key);
      return null;
    } on Object catch (e) {
      debugPrint('[ReputationCache] read failed: $e');
      return null;
    }
  }

  Future<void> _write(
    String key,
    Map<String, dynamic> body,
    DateTime fetchedAt,
  ) async {
    try {
      final prefs = await _preferences();
      await prefs.setString(
        key,
        jsonEncode({
          'fetchedAt': fetchedAt.toUtc().toIso8601String(),
          'body': body,
        }),
      );
    } on Object catch (e) {
      debugPrint('[ReputationCache] write failed: $e');
    }
  }
}
