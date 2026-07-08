import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/errors/app_error.dart';

/// Base class for rating states.
abstract class RatingState extends Equatable {
  const RatingState();

  @override
  List<Object?> get props => [];
}

class RatingInitial extends RatingState {
  const RatingInitial();
}

class RatingSubmitting extends RatingState {
  const RatingSubmitting();
}

/// A rating was saved (locally at minimum; sync reconciles later).
class RatingSubmitted extends RatingState {
  final String rateeId;

  const RatingSubmitted({required this.rateeId});

  @override
  List<Object?> get props => [rateeId];
}

class RatingError extends RatingState {
  final String message;
  final AppErrorCode code;

  const RatingError({
    required this.message,
    this.code = AppErrorCode.unknown,
  });

  @override
  List<Object?> get props => [message, code];
}
