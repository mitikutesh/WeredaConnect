/// Ethiopia keeps East Africa Time, UTC+3, all year (no daylight saving).
const Duration _ethiopiaUtcOffset = Duration(hours: 3);

/// SIM-50: the calendar day [instant] falls on in Ethiopia, as a UTC midnight
/// for `DateFormat` (which formats a UTC DateTime's own fields). Only for
/// display: do not compare it with other instants or convert it to local.
///
/// A trip's departureDate is stored as the instant of local midnight on the
/// day the trucker picked: 25 Oct picked in Addis Ababa is
/// 2026-10-24T21:00:00.000Z. Formatting that UTC value shows 24 Oct, and
/// `toLocal()` still does for a viewer west of Ethiopia. Trips run in
/// Ethiopia, so every viewer gets the day in Ethiopia whatever the device
/// timezone.
DateTime dayInEthiopia(DateTime instant) {
  final inEthiopia = instant.toUtc().add(_ethiopiaUtcOffset);
  return DateTime.utc(inEthiopia.year, inEthiopia.month, inEthiopia.day);
}
