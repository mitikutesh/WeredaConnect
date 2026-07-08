import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_event.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_state.dart';

/// BLoC for submitting ratings.
///
/// Kept intentionally small: it drives the submit flow only. Aggregate/local
/// reads (averages, "already rated?") are done directly against [repository]
/// from route-scoped widgets, so a read never clobbers the shared submit state.
class RatingBloc extends Bloc<RatingEvent, RatingState> {
  final BaseRatingRepository _repository;

  /// The repository backing this bloc, exposed so detail screens can perform
  /// short-lived local reads (average, already-rated) without routing them
  /// through this shared bloc's state.
  BaseRatingRepository get repository => _repository;

  RatingBloc({required BaseRatingRepository repository})
      : _repository = repository,
        super(const RatingInitial()) {
    on<SubmitRating>(_onSubmitRating);
  }

  Future<void> _onSubmitRating(
    SubmitRating event,
    Emitter<RatingState> emit,
  ) async {
    emit(const RatingSubmitting());
    try {
      await _repository.createRating(
        orderId: event.orderId,
        raterId: event.raterId,
        rateeId: event.rateeId,
        rateeRole: event.rateeRole,
        stars: event.stars,
        comment: event.comment,
      );
      emit(RatingSubmitted(rateeId: event.rateeId));
    } on Object catch (e) {
      emit(RatingError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }
}
