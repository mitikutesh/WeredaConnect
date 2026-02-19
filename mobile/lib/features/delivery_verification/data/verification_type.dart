/// Determines QrScanScreen behavior for pickup vs delivery verification.
enum VerificationType {
  /// Trucker scanning farmer's QR at pickup.
  /// Triggers: ConfirmPickup event with gpsPickup.
  pickup,

  /// Buyer scanning trucker's QR at delivery.
  /// Triggers: ConfirmDelivery event with gpsDropoff.
  delivery,
}
