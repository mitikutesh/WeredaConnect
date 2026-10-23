import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Tracks cumulative bytes sent and received through the app's Dio clients,
/// separately for each signed-in account on this device.
///
/// Counts are persisted to SharedPreferences (localStorage on web) under
/// keys suffixed with a hash of the user id, so a new account starts at zero
/// and two people sharing a phone see only their own usage, without leaving
/// raw account ids behind after logout. Traffic while nobody is signed in
/// (login, sign-up, session restore) is not attributed and is dropped.
///
/// Use [DataUsageInterceptor] to feed data into this tracker automatically,
/// and [bindDataUsageToAuth] to switch accounts on login/logout.
class DataUsageTracker extends ChangeNotifier {
  static const String _downloadedKey = 'data_downloaded_bytes';
  static const String _uploadedKey = 'data_uploaded_bytes';
  static const String _sinceKey = 'data_tracking_since';

  /// Clock seam for tests.
  final DateTime Function() _now;

  DataUsageTracker({DateTime Function()? now}) : _now = now ?? DateTime.now;

  SharedPreferences? _prefs;
  String? _userId;

  /// Storage key suffix for [_userId]; null while signed out.
  String? _keySuffix;
  int _bytesDownloaded = 0;
  int _bytesUploaded = 0;
  DateTime? _trackingSince;

  int get bytesDownloaded => _bytesDownloaded;
  int get bytesUploaded => _bytesUploaded;
  int get totalBytes => _bytesDownloaded + _bytesUploaded;
  DateTime? get trackingSince => _trackingSince;
  String? get userId => _userId;

  /// Load SharedPreferences once on startup. Every later read is synchronous
  /// so an account switch never races an in-flight counter update.
  Future<void> initialize() async {
    final prefs = await SharedPreferences.getInstance();
    _prefs = prefs;
    // Earlier builds stored one device-wide counter that cannot be
    // attributed to any account, so drop it.
    await prefs.remove(_downloadedKey);
    await prefs.remove(_uploadedKey);
    await prefs.remove(_sinceKey);
    _load();
  }

  /// Switch the counters to [userId] (null = signed out). A user seen on
  /// this device for the first time starts at zero, tracking from now.
  void setUser(String? userId) {
    if (userId == _userId) return;
    _userId = userId;
    _keySuffix = userId == null ? null : _suffixFor(userId);
    _load();
  }

  static String _suffixFor(String userId) =>
      sha256.convert(utf8.encode(userId)).toString().substring(0, 16);

  /// Remove the stored counters of [userId], an account that is not the one
  /// being counted (it was deleted after it signed out).
  static Future<void> forgetStoredUser(String userId) async {
    final prefs = await SharedPreferences.getInstance();
    final suffix = _suffixFor(userId);
    await Future.wait([
      prefs.remove('$_downloadedKey:$suffix'),
      prefs.remove('$_uploadedKey:$suffix'),
      prefs.remove('$_sinceKey:$suffix'),
    ]);
  }

  /// Remove the signed-in account's counters from this device (the account
  /// was deleted) and stop counting. Stops first, so a response still in
  /// flight cannot write them back.
  Future<void> forgetCurrentUser() async {
    final prefs = _prefs;
    final suffix = _keySuffix;
    setUser(null);
    if (prefs == null || suffix == null) return;
    try {
      await Future.wait([
        prefs.remove('$_downloadedKey:$suffix'),
        prefs.remove('$_uploadedKey:$suffix'),
        prefs.remove('$_sinceKey:$suffix'),
      ]);
    } on Object catch (e) {
      debugPrint('[DataUsageTracker] forget failed: $e');
    }
  }

  /// Record bytes received from a server response.
  void addDownloaded(int bytes) {
    if (bytes <= 0 || _userId == null) return;
    _bytesDownloaded += bytes;
    notifyListeners();
    _persist();
  }

  /// Record bytes sent in a request.
  void addUploaded(int bytes) {
    if (bytes <= 0 || _userId == null) return;
    _bytesUploaded += bytes;
    notifyListeners();
    _persist();
  }

  /// Clear the current account's counters. Tracking restarts from now.
  Future<void> reset() async {
    if (_userId == null) return;
    _bytesDownloaded = 0;
    _bytesUploaded = 0;
    _trackingSince = _now();
    notifyListeners();
    final prefs = _prefs;
    final suffix = _keySuffix;
    if (prefs == null || suffix == null) return;
    await Future.wait([
      _persist(),
      prefs.setInt(
        '$_sinceKey:$suffix',
        _trackingSince!.millisecondsSinceEpoch,
      ),
    ]);
  }

  /// Format a byte count as a human-readable string (B / KB / MB).
  static String formatBytes(int bytes) {
    if (bytes < 1024) return '$bytes B';
    if (bytes < 1024 * 1024) {
      return '${(bytes / 1024).toStringAsFixed(1)} KB';
    }
    return '${(bytes / (1024 * 1024)).toStringAsFixed(2)} MB';
  }

  void _load() {
    final prefs = _prefs;
    final suffix = _keySuffix;
    if (prefs == null || suffix == null) {
      _bytesDownloaded = 0;
      _bytesUploaded = 0;
      _trackingSince = null;
      notifyListeners();
      return;
    }
    _bytesDownloaded = prefs.getInt('$_downloadedKey:$suffix') ?? 0;
    _bytesUploaded = prefs.getInt('$_uploadedKey:$suffix') ?? 0;
    final sinceMs = prefs.getInt('$_sinceKey:$suffix');
    if (sinceMs != null) {
      _trackingSince = DateTime.fromMillisecondsSinceEpoch(sinceMs);
    } else {
      final since = _now();
      _trackingSince = since;
      prefs.setInt('$_sinceKey:$suffix', since.millisecondsSinceEpoch);
    }
    notifyListeners();
  }

  /// Writes the byte counters. The start date is written only when it
  /// changes (first sight in [_load], [reset]), not on every request.
  Future<void> _persist() async {
    final prefs = _prefs;
    final suffix = _keySuffix;
    if (prefs == null || suffix == null) return;
    // setInt updates the in-memory cache synchronously, so starting both
    // writes before awaiting means an immediate account switch (or a later
    // persist) always reads the latest values, never a half-written set.
    try {
      await Future.wait([
        prefs.setInt('$_downloadedKey:$suffix', _bytesDownloaded),
        prefs.setInt('$_uploadedKey:$suffix', _bytesUploaded),
      ]);
    } on Object catch (e) {
      // Metering must never surface as an unhandled error; the in-memory
      // counters stay correct and the next request retries the write.
      debugPrint('[DataUsageTracker] persist failed: $e');
    }
  }
}

/// Estimates byte count for a request/response body object.
int estimateBodyBytes(dynamic data) {
  if (data == null) return 0;
  if (data is String) return utf8.encode(data).length;
  if (data is List<int>) return data.length;
  if (data is FormData) return data.length;
  try {
    return utf8.encode(jsonEncode(data)).length;
  } on Object catch (_) {
    return 0;
  }
}

/// Estimates the header bytes of a request or response: one `name: value`
/// line per header plus CRLF, as in HTTP/1.1. HTTP/2 compresses headers, so
/// this is an upper bound there. On web the browser also adds headers the
/// app cannot see.
int estimateHeaderBytes(Map<String, dynamic> headers) {
  var total = 0;
  headers.forEach((name, value) {
    final values = value is List ? value : [value];
    for (final v in values) {
      if (v == null) continue;
      total += utf8.encode('$name: $v\r\n').length;
    }
  });
  return total;
}

/// Estimates the full size of an outgoing request: request line, Host
/// header, the other headers (including the bearer token) and body.
int estimateRequestBytes(RequestOptions options) {
  final uri = options.uri;
  final query = uri.hasQuery ? '?${uri.query}' : '';
  final head = '${options.method} ${uri.path}$query HTTP/1.1\r\n'
      'Host: ${uri.host}\r\n';
  return utf8.encode(head).length +
      estimateHeaderBytes(options.headers) +
      estimateBodyBytes(options.data);
}
