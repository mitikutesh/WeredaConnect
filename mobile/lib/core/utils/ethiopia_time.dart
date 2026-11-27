/// Ethiopia keeps East Africa Time, UTC+3, all year (no daylight saving).
const Duration _ethiopiaUtcOffset = Duration(hours: 3);

/// FUP-58: [instant] as the matching wall-clock time in Ethiopia (UTC+3),
/// carried on a UTC DateTime so `DateFormat` renders Ethiopia's own date and
/// time fields whatever the device timezone. Display only: the returned value
/// is not the real instant, so never compare it or convert it to local.
///
/// Server timestamps (createdAt, pickedUpAt, confirmedAt, a payment's
/// expiresAt and the rest) come back as UTC. Web keeps them in UTC and formats
/// them, so an order placed at 01:00 in Ethiopia (22:00 UTC the day before)
/// showed the previous day and the UTC hour. These events happen in Ethiopia,
/// so every viewer sees Ethiopia time whatever the device timezone.
DateTime instantInEthiopia(DateTime instant) =>
    instant.toUtc().add(_ethiopiaUtcOffset);

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
  final inEthiopia = instantInEthiopia(instant);
  return DateTime.utc(inEthiopia.year, inEthiopia.month, inEthiopia.day);
}

/// FUP-85: the instant at which the Ethiopia calendar day of [instant] begins
/// (local midnight, UTC+3). Unlike [dayInEthiopia] this is a real instant, so
/// it is safe to compare against stored instants such as a trip's
/// departureDate. A trip departing "today" is then eligible until Ethiopia
/// midnight, not only until the instant it would have left.
DateTime startOfDayInEthiopia(DateTime instant) {
  final inEthiopia = instant.toUtc().add(_ethiopiaUtcOffset);
  final dayMidnightEat =
      DateTime.utc(inEthiopia.year, inEthiopia.month, inEthiopia.day);
  return dayMidnightEat.subtract(_ethiopiaUtcOffset);
}
