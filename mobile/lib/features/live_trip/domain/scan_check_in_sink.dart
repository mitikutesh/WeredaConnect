/// Lets the delivery flow tell the check-in engine about a successful QR
/// scan (LT-08). The engine records a `scan` fix when the trip shares its
/// location, or shows "Scans only", and ignores every other trip.
abstract interface class ScanCheckInSink {
  /// Never throws: a check-in must never fail a handoff.
  Future<void> onScan(String tripId);
}
