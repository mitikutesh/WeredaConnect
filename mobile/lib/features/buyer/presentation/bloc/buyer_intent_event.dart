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
class FulfillIntent extends BuyerIntentEvent {
  final String intentId;

  const FulfillIntent({required this.intentId});

  @override
  List<Object?> get props => [intentId];
}

/// Delete an intent.
class DeleteIntent extends BuyerIntentEvent {
  final String intentId;

  const DeleteIntent({required this.intentId});

  @override
  List<Object?> get props => [intentId];
}

/// Refresh match counts for all active intents (called after sync).
class RefreshAllMatches extends BuyerIntentEvent {
  final String buyerId;

  const RefreshAllMatches({required this.buyerId});

  @override
  List<Object?> get props => [buyerId];
}
