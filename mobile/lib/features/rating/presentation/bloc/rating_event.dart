import 'package:equatable/equatable.dart';

/// Base class for rating events.
abstract class RatingEvent extends Equatable {
  const RatingEvent();

  @override
  List<Object?> get props => [];
}

/// Submit a rating for a counterparty (buyer action).
class SubmitRating extends RatingEvent {
  final String orderId;
  final String raterId;
  final String rateeId;

  /// `farmer` or `trucker`.
  final String rateeRole;
  final int stars;
  final String? comment;

  const SubmitRating({
    required this.orderId,
    required this.raterId,
    required this.rateeId,
    required this.rateeRole,
    required this.stars,
    this.comment,
  });

  @override
  List<Object?> get props =>
      [orderId, raterId, rateeId, rateeRole, stars, comment];
}
