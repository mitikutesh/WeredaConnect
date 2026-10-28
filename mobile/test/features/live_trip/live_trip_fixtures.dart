import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';

/// Towns on the Addis Ababa to Adama road, as in the bundled place list,
/// plus one far away (Gondar) that no corridor map shows.
const corridorPlaces = <Place>[
  Place(
    id: 'gn-344979',
    en: 'Addis Ababa',
    am: 'አዲስ አበባ',
    om: 'Finfinnee',
    lat: 9.025,
    lng: 38.7469,
    population: 3860000,
  ),
  Place(
    id: 'gn-330186',
    en: 'Nazrēt',
    display: 'Adama',
    am: 'አዳማ',
    om: 'Adaamaa',
    lat: 8.55,
    lng: 39.2667,
    population: 456900,
  ),
  Place(
    id: 'gn-339666',
    en: 'Bishoftu',
    am: 'ቢሾፍቱ',
    om: 'Bishooftuu',
    lat: 8.7523,
    lng: 38.9785,
    population: 207400,
  ),
  Place(
    id: 'gn-330491',
    en: 'Mojo',
    am: 'ሞጆ',
    om: 'Mojoo',
    lat: 8.5868,
    lng: 39.1211,
    population: 61300,
  ),
  Place(id: 'gn-338488', en: 'Dukem', lat: 8.8, lng: 38.9),
  Place(id: 'gn-10378531', en: 'Akaki', lat: 8.8333, lng: 38.8333),
  Place(id: 'gn-12640451', en: 'Wonji', lat: 8.4541, lng: 39.2175),
  Place(id: 'gn-7670632', en: "Ada'a Chukala", lat: 8.5833, lng: 38.9167),
  Place(
    id: 'gn-337853',
    en: 'Gonder',
    display: 'Gondar',
    lat: 12.6,
    lng: 37.4667,
    population: 466000,
  ),
];

/// The server's clock in the fixtures.
final asOf = DateTime.utc(2026, 10, 27, 9, 10);

DateTime at(int hour, int minute) => DateTime.utc(2026, 10, 27, hour, minute);

TrackFix fix(
  double lat,
  double lng,
  DateTime capturedAt, {
  String source = 'hourly',
  DateTime? receivedAt,
}) =>
    TrackFix(
      point: GeoPoint(lat, lng),
      capturedAt: capturedAt,
      receivedAt: receivedAt ?? capturedAt.add(const Duration(seconds: 3)),
      source: source,
    );

/// Picked up near Akaki at 05:05, hourly check-ins past Dukem and Bishoftu,
/// no network for 97 minutes, last check-in near Mojo at 08:42.
final corridorTrail = <TrackFix>[
  fix(8.88, 38.79, at(5, 5), source: 'scan'),
  fix(8.80, 38.90, at(6, 5)),
  fix(8.752, 38.978, at(7, 5)),
  fix(8.64, 39.06, at(8, 42)),
];

/// A track on the corridor in [sharing], with the trail and stops above.
DeliveryTrack corridorTrack({
  TrackSharing sharing = TrackSharing.live,
  List<TrackFix>? trail,
  DateTime? pausedUntil,
  String? dropoffNote,
}) {
  final fixes = trail ?? corridorTrail;
  return DeliveryTrack(
    deliveryId: 'del-101',
    sharing: sharing,
    asOf: asOf,
    pausedUntil: pausedUntil,
    lastFix: fixes.isEmpty ? null : fixes.last,
    trail: fixes,
    pickup: const GeoPoint(8.88, 38.79),
    pickedUpAt: at(5, 5),
    dropoff: const GeoPoint(8.55, 39.27),
    dropoffNote: dropoffNote,
    otherStops: const [
      TrackStop(kind: 'pickup', point: GeoPoint(8.83, 38.86), done: true),
      TrackStop(kind: 'dropoff', point: GeoPoint(8.6, 39.18), done: false),
    ],
  );
}

/// The contract's example answer (live-trip-api.md section 2).
Map<String, dynamic> contractTrackJson() => {
      'deliveryId': 'del-101',
      'sharing': 'live',
      'pausedUntil': null,
      'asOf': '2026-10-27T09:10:00.000Z',
      'lastFix': {
        'latitude': 8.752,
        'longitude': 38.979,
        'capturedAt': '2026-10-27T08:42:00.000Z',
        'receivedAt': '2026-10-27T08:42:03.000Z',
        'accuracyM': 35,
      },
      'trail': [
        {
          'latitude': 8.150,
          'longitude': 38.820,
          'capturedAt': '2026-10-27T05:05:00.000Z',
          'receivedAt': '2026-10-27T05:05:02.000Z',
          'source': 'scan',
        },
      ],
      'pauses': [
        {
          'from': '2026-10-27T07:55:00.000Z',
          'until': '2026-10-27T08:25:00.000Z',
        },
      ],
      'pickup': {
        'latitude': 8.150,
        'longitude': 38.820,
        'at': '2026-10-27T05:05:00.000Z',
      },
      'dropoff': {
        'latitude': 9.03,
        'longitude': 38.74,
        'note': 'Merkato, near Anwar Mosque',
      },
      'otherStops': [
        {
          'kind': 'dropoff',
          'latitude': 8.80,
          'longitude': 38.90,
          'done': false,
        },
      ],
      'eta': {
        'from': '2026-10-27T10:20:00.000Z',
        'to': '2026-10-27T11:00:00.000Z',
      },
      'remainingKm': 52.7,
      'summary': null,
    };

/// An `ended` answer: no coordinates anywhere, a summary.
Map<String, dynamic> endedTrackJson() => {
      'deliveryId': 'del-101',
      'sharing': 'ended',
      'pausedUntil': null,
      'asOf': '2026-10-27T12:00:00.000Z',
      'lastFix': null,
      'trail': <Object>[],
      'pauses': <Object>[],
      'pickup': {
        'latitude': null,
        'longitude': null,
        'at': '2026-10-27T05:05:00.000Z',
      },
      'dropoff': {'latitude': null, 'longitude': null, 'note': null},
      'otherStops': <Object>[],
      'eta': null,
      'remainingKm': null,
      'summary': {'distanceKm': 96.4, 'durationMin': 335, 'fixes': 7},
    };
