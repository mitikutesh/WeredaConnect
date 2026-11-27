import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/live_trip/data/repositories/api_track_repository.dart';

/// Mobile track repository (LT-10). Checks the phone's connectivity first, so
/// an offline read answers the stored snapshot at once instead of waiting
/// for the 30 second timeout on the road.
class TrackRepository extends ApiTrackRepository {
  TrackRepository({
    required super.api,
    required ConnectivityMonitor connectivity,
    super.store,
    super.clock,
  }) : _connectivity = connectivity;

  final ConnectivityMonitor _connectivity;

  @override
  @protected
  Future<bool> isOnline() async {
    try {
      return await _connectivity.isConnected();
    } on Exception catch (e) {
      // Unknown: try the network, the read itself will tell.
      debugPrint('[TrackRepository] connectivity check failed: $e');
      return true;
    }
  }
}
