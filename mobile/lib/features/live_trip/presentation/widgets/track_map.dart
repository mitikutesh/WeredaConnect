import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_painter.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

/// The map of a delivery's track (LT-10): [TrackMapPainter] over the bundled
/// place list, in the app language. It fills the space it is given, so give
/// it a bounded size (for example an [AspectRatio] or a [SizedBox]).
///
/// [now] decides the freshness band and the halo; pass the screen's clock so
/// the map ages while it is shown. The town labels are kept between paints
/// ([TrackLabelCache]), so a new [now] alone does not search the place list
/// again. [places] defaults to the bundled list, loaded once through
/// [PlaceRepository.instance]; until it is loaded the map draws without town
/// labels.
class TrackMap extends StatefulWidget {
  const TrackMap({
    required this.track,
    required this.now,
    this.places,
    this.style = const TrackMapStyle(),
    this.semanticLabel,
    super.key,
  });

  final DeliveryTrack track;
  final DateTime now;
  final List<Place>? places;
  final TrackMapStyle style;

  /// What a screen reader says for the map (the screen's summary of it).
  final String? semanticLabel;

  @override
  State<TrackMap> createState() => _TrackMapState();
}

class _TrackMapState extends State<TrackMap> {
  List<Place> _bundled = const [];
  bool _loading = false;
  final TrackLabelCache _labelCache = TrackLabelCache();

  @override
  void initState() {
    super.initState();
    if (widget.places == null) _loadPlaces();
  }

  @override
  void didUpdateWidget(TrackMap oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.places == null && _bundled.isEmpty) _loadPlaces();
  }

  Future<void> _loadPlaces() async {
    if (_loading) return;
    _loading = true;
    final repository = PlaceRepository.instance;
    try {
      await repository.ensureLoaded();
    } on Exception catch (e) {
      debugPrint('[TrackMap] place list unavailable: $e');
      return;
    } finally {
      _loading = false;
    }
    if (mounted) setState(() => _bundled = repository.places);
  }

  @override
  Widget build(BuildContext context) {
    final painter = TrackMapPainter(
      track: widget.track,
      now: widget.now,
      places: widget.places ?? _bundled,
      languageCode: Localizations.maybeLocaleOf(context)?.languageCode ?? 'en',
      style: widget.style,
      textDirection: Directionality.of(context),
      textScaler: MediaQuery.textScalerOf(context),
      labelCache: _labelCache,
    );
    return Semantics(
      image: true,
      label: widget.semanticLabel,
      child: RepaintBoundary(
        child: CustomPaint(
          painter: painter,
          child: const SizedBox.expand(),
        ),
      ),
    );
  }
}
