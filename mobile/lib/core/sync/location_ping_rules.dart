/// Queue rules for the trucker's check-ins (LT-08, `live-trip.md` section
/// 5.3, `sync-protocol.md` Location pings). Pure Dart: the mobile sync queue
/// and the web check-in sender both use them.
library;

/// The sync entity type of a check-in or sharing event.
const String locationPingEntityType = 'location_ping';

/// The `kind` of a check-in with coordinates; every other kind is an event.
const String locationPingFixKind = 'fix';

/// When more unsent pings than this wait, they are coalesced.
const int locationPingCoalesceThreshold = 24;

/// The trip id of a `location_ping` entity id (`{tripId}#{capturedAt ms}`),
/// or null when it has no `#`.
String? tripIdOfLocationPing(String entityId) {
  final separator = entityId.lastIndexOf('#');
  return separator > 0 ? entityId.substring(0, separator) : null;
}

/// One unsent ping as the coalescing rule sees it. [key] is the caller's
/// handle (an operation, a ping id).
class UnsentLocationPing<K> {
  const UnsentLocationPing({
    required this.key,
    required this.tripId,
    required this.kind,
    required this.capturedAt,
    this.source,
  });

  final K key;
  final String tripId;
  final String kind;
  final String? source;
  final DateTime capturedAt;
}

/// The order in which a `fix` is kept when several share a clock hour: the
/// first check-in after consent, then a QR scan, I've arrived, Check in now,
/// the 15 minute one near a drop-off, and the hourly one.
const List<String> _keepOrder = [
  'start',
  'scan',
  'arrived',
  'manual',
  'near_stop',
  'hourly',
];

int _rank(String? source) {
  final index = _keepOrder.indexOf(source ?? '');
  return index < 0 ? _keepOrder.length : index;
}

/// The keys of the unsent pings to drop when more than [threshold] wait: per
/// trip and clock hour (UTC) of `capturedAt` at most one `fix` stays (the one
/// whose source ranks first in [_keepOrder], the latest on a tie), and every
/// event (`paused`, `resumed`, `declined`) stays. Nothing is dropped while
/// [threshold] or fewer wait, so a short dead zone keeps every check-in.
Set<K> locationPingsToCoalesce<K>(
  List<UnsentLocationPing<K>> unsent, {
  int threshold = locationPingCoalesceThreshold,
}) {
  if (unsent.length <= threshold) return <K>{};

  final kept = <String, UnsentLocationPing<K>>{};
  final drop = <K>{};
  for (final ping in unsent) {
    if (ping.kind != locationPingFixKind) continue;
    final at = ping.capturedAt.toUtc();
    final hour = '${ping.tripId}|${at.year}-${at.month}-${at.day}T${at.hour}';
    final current = kept[hour];
    if (current == null) {
      kept[hour] = ping;
      continue;
    }
    final better = _rank(ping.source) < _rank(current.source) ||
        (_rank(ping.source) == _rank(current.source) &&
            ping.capturedAt.isAfter(current.capturedAt));
    if (better) {
      drop.add(current.key);
      kept[hour] = ping;
    } else {
      drop.add(ping.key);
    }
  }
  return drop;
}
