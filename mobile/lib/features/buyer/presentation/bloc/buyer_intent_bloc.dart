import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/buyer/data/repositories/buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/matching_service.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';

/// BLoC for managing buyer intent state.
///
/// Handles creating intents, loading active intents for the home screen,
/// running on-device matching, and refreshing match counts after sync.
class BuyerIntentBloc extends Bloc<BuyerIntentEvent, BuyerIntentState> {
  final BuyerIntentRepository _repository;
  final MatchingService _matchingService;

  BuyerIntentBloc({
    required BuyerIntentRepository repository,
    required MatchingService matchingService,
  })  : _repository = repository,
        _matchingService = matchingService,
        super(const BuyerIntentInitial()) {
    on<CreateBuyerIntent>(_onCreateBuyerIntent);
    on<LoadActiveIntents>(_onLoadActiveIntents);
    on<LoadMatchResults>(_onLoadMatchResults);
    on<FulfillIntent>(_onFulfillIntent);
    on<DeleteIntent>(_onDeleteIntent);
    on<RefreshAllMatches>(_onRefreshAllMatches);
  }

  Future<void> _onCreateBuyerIntent(
    CreateBuyerIntent event,
    Emitter<BuyerIntentState> emit,
  ) async {
    emit(const BuyerIntentLoading());
    try {
      final intent = await _repository.createIntent(
        buyerId: event.buyerId,
        cropType: event.cropType,
        quantityNeeded: event.quantityNeeded,
        maxPricePerKg: event.maxPricePerKg,
        deliveryWoreda: event.deliveryWoreda,
      );

      // Immediately run matching against cached data
      final matches =
          await _matchingService.matchListingsForIntent(intent);

      // Update match count
      await _repository.updateMatchCount(
        intent.intentId,
        matches.length,
        DateTime.now().toUtc(),
      );
      await _repository.updateLastViewed(intent.intentId);

      emit(BuyerIntentCreated(intent: intent, matches: matches));
    } on Exception catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }

  Future<void> _onLoadActiveIntents(
    LoadActiveIntents event,
    Emitter<BuyerIntentState> emit,
  ) async {
    emit(const BuyerIntentLoading());
    try {
      final intents = await _repository.getActiveIntents(event.buyerId);
      emit(ActiveIntentsLoaded(intents: intents));
    } on Exception catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }

  Future<void> _onLoadMatchResults(
    LoadMatchResults event,
    Emitter<BuyerIntentState> emit,
  ) async {
    emit(const BuyerIntentLoading());
    try {
      final intent = await _repository.getIntentById(event.intentId);
      if (intent == null) {
        emit(const BuyerIntentError(message: 'Intent not found'));
        return;
      }

      final matches =
          await _matchingService.matchListingsForIntent(intent);

      // Update match count and mark as viewed
      await _repository.updateMatchCount(
        intent.intentId,
        matches.length,
        DateTime.now().toUtc(),
      );
      await _repository.updateLastViewed(intent.intentId);

      emit(MatchResultsLoaded(intent: intent, matches: matches));
    } on Exception catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }

  Future<void> _onFulfillIntent(
    FulfillIntent event,
    Emitter<BuyerIntentState> emit,
  ) async {
    try {
      await _repository.fulfillIntent(event.intentId);
    } on Exception catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }

  Future<void> _onDeleteIntent(
    DeleteIntent event,
    Emitter<BuyerIntentState> emit,
  ) async {
    try {
      await _repository.deleteIntent(event.intentId);
    } on Exception catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }

  Future<void> _onRefreshAllMatches(
    RefreshAllMatches event,
    Emitter<BuyerIntentState> emit,
  ) async {
    try {
      await _matchingService.refreshMatchCounts(
        event.buyerId,
        _repository,
      );
    } on Exception catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }
}
