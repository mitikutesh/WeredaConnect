import 'package:equatable/equatable.dart';

/// Base class for buyer intent events.
abstract class BuyerIntentEvent extends Equatable {
  const BuyerIntentEvent();

  @override
  List<Object?> get props => [];
}

/// Create a new buyer intent and run matching.
class CreateBuyerIntent extends BuyerIntentEvent {
  final String buyerId;
  final String cropType;
  final double quantityNeeded;
  final double maxPricePerKg;
  final String deliveryWoreda;

  const CreateBuyerIntent({
    required this.buyerId,
    required this.cropType,
    required this.quantityNeeded,
    required this.maxPricePerKg,
    required this.deliveryWoreda,
  });

  @override
  List<Object?> get props => [
        buyerId,
        cropType,
        quantityNeeded,
        maxPricePerKg,
        deliveryWoreda,
      ];
}

/// Load all active intents for the home screen section.
class LoadActiveIntents extends BuyerIntentEvent {
  final String buyerId;

  const LoadActiveIntents({required this.buyerId});

  @override
  List<Object?> get props => [buyerId];
}

/// Load match results for a specific intent.
class LoadMatchResults extends BuyerIntentEvent {
  final String intentId;

  const LoadMatchResults({required this.intentId});

  @override
  List<Object?> get props => [intentId];
}

/// Mark an intent as fulfilled after the buyer places an order.
///
/// [buyerId] lets the BLoC reload the active-intent list after the mutation so
/// it can emit a terminal loaded state (and the fulfilled intent drops out).
class FulfillIntent extends BuyerIntentEvent {
  final String intentId;
  final String buyerId;

  const FulfillIntent({required this.intentId, required this.buyerId});

  @override
  List<Object?> get props => [intentId, buyerId];
}

/// Delete an intent. [buyerId] lets the BLoC reload and emit a terminal state.
class DeleteIntent extends BuyerIntentEvent {
  final String intentId;
  final String buyerId;

  const DeleteIntent({required this.intentId, required this.buyerId});

  @override
  List<Object?> get props => [intentId, buyerId];
}

/// Refresh match counts for all active intents (called after sync).
class RefreshAllMatches extends BuyerIntentEvent {
  final String buyerId;

  const RefreshAllMatches({required this.buyerId});

  @override
  List<Object?> get props => [buyerId];
}
