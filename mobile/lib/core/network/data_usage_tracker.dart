import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Tracks cumulative bytes sent and received through the Dio HTTP client.
///
/// Counts are persisted to SharedPreferences so they survive app restarts.
/// Use [DataUsageInterceptor] to feed data into this tracker automatically.
class DataUsageTracker extends ChangeNotifier {
  static const String _downloadedKey = 'data_downloaded_bytes';
  static const String _uploadedKey = 'data_uploaded_bytes';
  static const String _sinceKey = 'data_tracking_since';

  int _bytesDownloaded = 0;
  int _bytesUploaded = 0;
  DateTime? _trackingSince;

  int get bytesDownloaded => _bytesDownloaded;
  int get bytesUploaded => _bytesUploaded;
  int get totalBytes => _bytesDownloaded + _bytesUploaded;
  DateTime? get trackingSince => _trackingSince;

  /// Load persisted counters on startup.
  Future<void> initialize() async {
    final prefs = await SharedPreferences.getInstance();
    _bytesDownloaded = prefs.getInt(_downloadedKey) ?? 0;
    _bytesUploaded = prefs.getInt(_uploadedKey) ?? 0;
    final sinceMs = prefs.getInt(_sinceKey);
    if (sinceMs != null) {
      _trackingSince = DateTime.fromMillisecondsSinceEpoch(sinceMs);
    }
    notifyListeners();
  }

  /// Record bytes received from a server response.
  void addDownloaded(int bytes) {
    if (bytes <= 0) return;
    _bytesDownloaded += bytes;
    _ensureTrackingStartDate();
    notifyListeners();
    _persist();
  }

  /// Record bytes sent in a request body.
  void addUploaded(int bytes) {
    if (bytes <= 0) return;
    _bytesUploaded += bytes;
    _ensureTrackingStartDate();
    notifyListeners();
    _persist();
  }

  /// Clear all counters and reset the tracking start date.
  Future<void> reset() async {
    _bytesDownloaded = 0;
    _bytesUploaded = 0;
    _trackingSince = null;
    notifyListeners();
    final prefs = await SharedPreferences.getInstance();
    await prefs.remove(_downloadedKey);
    await prefs.remove(_uploadedKey);
    await prefs.remove(_sinceKey);
  }

  /// Format a byte count as a human-readable string (B / KB / MB).
  static String formatBytes(int bytes) {
    if (bytes < 1024) return '$bytes B';
    if (bytes < 1024 * 1024) {
      return '${(bytes / 1024).toStringAsFixed(1)} KB';
    }
    return '${(bytes / (1024 * 1024)).toStringAsFixed(2)} MB';
  }

  void _ensureTrackingStartDate() {
    if (_trackingSince == null) {
      _trackingSince = DateTime.now();
    }
  }

  void _persist() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setInt(_downloadedKey, _bytesDownloaded);
    await prefs.setInt(_uploadedKey, _bytesUploaded);
    if (_trackingSince != null) {
      await prefs.setInt(
          _sinceKey, _trackingSince!.millisecondsSinceEpoch);
    }
  }
}

/// Estimates byte count for a request/response body object.
int estimateBodyBytes(dynamic data) {
  if (data == null) return 0;
  try {
    return utf8.encode(jsonEncode(data)).length;
  } catch (_) {
    return 0;
  }
}
