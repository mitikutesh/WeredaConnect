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

/// Order money for one order: crop, transport and platform commission, in ETB.
class OrderPricing {
  const OrderPricing({
    required this.cropCost,
    this.transportCost = 0,
    this.commissionRate = defaultOrderCommissionRate,
  });

  /// Quantity (kg) x price per kg.
  final double cropCost;

  /// The trucker's leg; 0 without a trip.
  final double transportCost;

  final double commissionRate;

  /// Commission is charged on the crop cost only, never on transport.
  double get commissionAmount => cropCost * commissionRate;

  /// Same addition order as the server (crop + transport + commission), so
  /// the same inputs give the same double.
  double get orderTotal => cropCost + transportCost + commissionAmount;
}
