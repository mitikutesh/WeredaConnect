import 'package:flutter/foundation.dart';

import 'package:wereda_connect/features/live_trip/data/repositories/api_track_repository.dart';

/// Web track repository (LT-10). The browser has no connectivity monitor in
/// the web app, so every read tries the network and a network error answers
/// the stored snapshot (kept in localStorage) as offline.
class WebTrackRepository extends ApiTrackRepository {
  WebTrackRepository({required super.api, super.store, super.clock});

  @override
  @protected
  Future<bool> isOnline() async => true;
}
