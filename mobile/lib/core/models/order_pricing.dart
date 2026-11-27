/// The client copy of the server's order money formula (SyncFunction order
/// create, specs/contracts/order-lifecycle.md "Order Create Validation").
///
/// The server computes and stores these amounts and is the source of truth
/// for what the buyer pays. Every client preview of an order total (place
/// order, listing detail, match "Estimated Total") goes through
/// [OrderPricing] so the preview equals the server total (SIM-53).
library;

/// Mirrors `SyncFunction.DefaultCommissionRate` and the CDK SSM default for
/// `/{prefix}/escrow/commission-rate`.
const double defaultOrderCommissionRate = 0.05;

/// An ETB amount to the santim (2 decimals), halves away from zero, as the
/// server rounds order and escrow money (SIM-30: `Math.Round(x, 2,
/// MidpointRounding.AwayFromZero)` on exact decimals). The double is first
/// taken to the nearest millionth, which drops binary noise: 170 kg x 18.99
/// ETB x 0.05 is 161.41499999999999 in doubles, 161.415 exactly, so 161.42
/// here as on the server.
double roundEtb(double amount) {
  if (!amount.isFinite) return amount;
  final micros = (amount.abs() * 1e6).round();
  final santim = (micros + 5000) ~/ 10000;
  return (amount.isNegative ? -santim : santim) / 100;
}

/// Order money for one order: crop, transport and platform commission, in ETB.
class OrderPricing {
  const OrderPricing({
    required this.cropCost,
    this.transportCost = 0,
    this.commissionRate = defaultOrderCommissionRate,
  });

  /// Quantity (kg) x price per kg, as computed.
  final double cropCost;

  /// The trucker's leg as computed; 0 without a trip.
  final double transportCost;

  final double commissionRate;

  /// The crop leg to the santim, what the server stores as `cropCost`.
  double get cropCostEtb => roundEtb(cropCost);

  /// The transport leg to the santim, what the server stores as
  /// `transportCost`.
  double get transportCostEtb => roundEtb(transportCost);

  /// Commission is charged on the crop cost only, never on transport: the
  /// rate times the rounded crop cost, to the santim (SIM-30).
  double get commissionAmount => roundEtb(cropCostEtb * commissionRate);

  /// Crop + transport + commission, each to the santim and added in whole
  /// santim, so the total is the server's `orderTotal` with no float tail.
  double get orderTotal =>
      (_santim(cropCostEtb) +
          _santim(transportCostEtb) +
          _santim(commissionAmount)) /
      100;

  static int _santim(double etb) => (etb * 100).round();
}
