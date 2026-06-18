import 'package:flutter/foundation.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';

/// BLoC for managing buyer intent state.
///
/// Handles creating intents, loading active intents for the home screen,
/// running on-device matching, and refreshing match counts after sync.
class BuyerIntentBloc extends Bloc<BuyerIntentEvent, BuyerIntentState> {
  final BaseBuyerIntentRepository _repository;
  final BaseMatchingService _matchingService;

  /// Mobile-only: pulls the marketplace into Isar before matching so the
  /// on-device matcher has data to read. Null on web, where matching queries
  /// the API directly. See [MarketplaceSync].
  final MarketplaceSync? _marketplaceSync;

  BuyerIntentBloc({
    required BaseBuyerIntentRepository repository,
    required BaseMatchingService matchingService,
    MarketplaceSync? marketplaceSync,
  })  : _repository = repository,
        _matchingService = matchingService,
        _marketplaceSync = marketplaceSync,
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

      // Refresh the local marketplace from the server so matching sees other
      // users' crops/trips, then run matching against the cached data.
      await _refreshMarketplace();
      final matches = await _matchingService.matchListingsForIntent(intent);

      // Update match count
      await _repository.updateMatchCount(
        intent.intentId,
        matches.length,
        DateTime.now().toUtc(),
      );
      await _repository.updateLastViewed(intent.intentId);

      emit(BuyerIntentCreated(intent: intent, matches: matches));
    } on Object catch (e) {
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
    } on Object catch (e) {
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

      await _refreshMarketplace();
      final matches = await _matchingService.matchListingsForIntent(intent);

      // Update match count and mark as viewed
      await _repository.updateMatchCount(
        intent.intentId,
        matches.length,
        DateTime.now().toUtc(),
      );
      await _repository.updateLastViewed(intent.intentId);

      emit(MatchResultsLoaded(intent: intent, matches: matches));
    } on Object catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }

  Future<void> _onFulfillIntent(
    FulfillIntent event,
    Emitter<BuyerIntentState> emit,
  ) async {
    try {
      await _repository.fulfillIntent(event.intentId);
    } on Object catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }

  Future<void> _onDeleteIntent(
    DeleteIntent event,
    Emitter<BuyerIntentState> emit,
  ) async {
    try {
      await _repository.deleteIntent(event.intentId);
    } on Object catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }

  Future<void> _onRefreshAllMatches(
    RefreshAllMatches event,
    Emitter<BuyerIntentState> emit,
  ) async {
    try {
      // Deliberately no BuyerIntentLoading here: the caller is a
      // pull-to-refresh that shows its own spinner, so we keep the current
      // list on screen and just swap in fresh counts when done.
      await _refreshMarketplace();
      await _matchingService.refreshMatchCounts(
        event.buyerId,
        _repository,
      );
      final intents = await _repository.getActiveIntents(event.buyerId);
      emit(ActiveIntentsLoaded(intents: intents));
    } on Object catch (e) {
      emit(BuyerIntentError(message: e.toString()));
    }
  }

  /// Pull the latest marketplace (all crops + trips) into local storage so the
  /// on-device matcher has fresh data. Failures (offline, transient network)
  /// are swallowed on purpose: matching then falls back to the last-cached
  /// data, preserving the offline-first guarantee. No-op on web (null sync).
  Future<void> _refreshMarketplace() async {
    final sync = _marketplaceSync;
    if (sync == null) return;
    try {
      await sync.pullAll();
    } on Object catch (e) {
      debugPrint('[BuyerIntentBloc] marketplace pull failed, '
          'matching against cached data: $e');
    }
  }
}
