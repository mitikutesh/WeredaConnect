import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';

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

  /// Canonical place id when [deliveryWoreda] was picked from the place
  /// selector; null for free-text entries. Stored locally only.
  final String? placeId;

  /// Optional delivery deadline; drives trip date-fit. Null = no deadline.
  final DateTime? neededByDate;

  const CreateBuyerIntent({
    required this.buyerId,
    required this.cropType,
    required this.quantityNeeded,
    required this.maxPricePerKg,
    required this.deliveryWoreda,
    this.placeId,
    this.neededByDate,
  });

  @override
  List<Object?> get props => [
        buyerId,
        cropType,
        quantityNeeded,
        maxPricePerKg,
        deliveryWoreda,
        placeId,
        neededByDate,
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

/// Telemetry: the buyer opened the match at [rank] (0-based). Handled as a
/// pure side effect — no state is emitted. See MatchEventLogger.
class LogMatchOpened extends BuyerIntentEvent {
  final BuyerIntent intent;
  final CropMatchResult result;
  final int rank;

  const LogMatchOpened({
    required this.intent,
    required this.result,
    required this.rank,
  });

  @override
  List<Object?> get props => [intent.intentId, result.listing.listingId, rank];
}

/// Telemetry: an order was created for [listingId] from this intent's
/// matches. Handled as a pure side effect — no state is emitted.
class LogMatchOrderCreated extends BuyerIntentEvent {
  final BuyerIntent intent;
  final String listingId;

  const LogMatchOrderCreated({required this.intent, required this.listingId});

  @override
  List<Object?> get props => [intent.intentId, listingId];
}
