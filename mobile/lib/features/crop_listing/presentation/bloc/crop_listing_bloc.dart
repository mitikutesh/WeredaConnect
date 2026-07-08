import 'package:flutter/foundation.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';

/// BLoC for managing crop listing state.
///
/// Handles creating, loading, filtering, and deleting crop listings.
/// All operations go through the BaseCropListingRepository (mobile or web).
class CropListingBloc extends Bloc<CropListingEvent, CropListingState> {
  final BaseCropListingRepository _repository;

  /// Mobile-only: pulls all active crops into Isar before the Buyer browses
  /// the marketplace ([LoadActiveListings]). Null on web (API-backed reads).
  /// Owner-scoped events (a Farmer's own listings) never trigger a pull.
  final MarketplaceSync? _marketplaceSync;

  CropListingBloc({
    required BaseCropListingRepository repository,
    MarketplaceSync? marketplaceSync,
  })  : _repository = repository,
        _marketplaceSync = marketplaceSync,
        super(const CropListingInitial()) {
    on<LoadCropListings>(_onLoadCropListings);
    on<LoadActiveListings>(_onLoadActiveListings);
    on<CreateCropListing>(_onCreateCropListing);
    on<DeleteCropListing>(_onDeleteCropListing);
    on<FilterCropListings>(_onFilterCropListings);
  }

  Future<void> _onLoadCropListings(
    LoadCropListings event,
    Emitter<CropListingState> emit,
  ) async {
    emit(const CropListingLoading());

    try {
      final listings = await _repository.getListingsByFarmer(event.farmerId);
      emit(CropListingsLoaded(listings: listings));
    } on Object catch (e) {
      emit(CropListingError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _onLoadActiveListings(
    LoadActiveListings event,
    Emitter<CropListingState> emit,
  ) async {
    emit(const CropListingLoading());

    try {
      // Buyer marketplace browse: refresh from the server first so the local
      // (offline-first) read below returns every farmer's active listings.
      await _refreshMarketplace();
      final listings = await _repository.getActiveListings(
        cropTypeFilter: event.cropTypeFilter,
        weredaFilter: event.weredaFilter,
        priceMin: event.priceMin,
        priceMax: event.priceMax,
      );
      emit(
        CropListingsLoaded(
          listings: listings,
          activeFilter: event.cropTypeFilter,
        ),
      );
    } on Object catch (e) {
      emit(CropListingError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _onCreateCropListing(
    CreateCropListing event,
    Emitter<CropListingState> emit,
  ) async {
    emit(const CropListingLoading());

    try {
      final listing = await _repository.createListing(
        farmerId: event.farmerId,
        farmerName: event.farmerName,
        farmerPhone: event.farmerPhone,
        cropType: event.cropType,
        quantity: event.quantity,
        pricePerKg: event.pricePerKg,
        latitude: event.latitude,
        longitude: event.longitude,
        locationName: event.locationName,
        description: event.description,
        audioNoteRef: event.audioNoteRef,
        woreda: event.woreda,
        placeId: event.placeId,
      );
      emit(CropListingCreated(listing: listing));
    } on Object catch (e) {
      emit(CropListingError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _onDeleteCropListing(
    DeleteCropListing event,
    Emitter<CropListingState> emit,
  ) async {
    emit(const CropListingLoading());

    try {
      await _repository.deleteListing(
        listingId: event.listingId,
        farmerId: event.farmerId,
      );
      // Reload listings after delete
      final listings = await _repository.getListingsByFarmer(event.farmerId);
      emit(CropListingsLoaded(listings: listings));
    } on Object catch (e) {
      emit(CropListingError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  /// Pull all active crops into local storage so the Buyer's marketplace
  /// browse is not empty. Offline/transient failures are swallowed so the
  /// cached data still renders (offline-first). No-op on web (null sync).
  Future<void> _refreshMarketplace() async {
    final sync = _marketplaceSync;
    if (sync == null) return;
    try {
      await sync.pullCrops();
    } on Object catch (e) {
      debugPrint('[CropListingBloc] crop pull failed, '
          'showing cached listings: $e');
    }
  }

  Future<void> _onFilterCropListings(
    FilterCropListings event,
    Emitter<CropListingState> emit,
  ) async {
    emit(const CropListingLoading());

    try {
      final listings = await _repository.getListingsByFarmerFiltered(
        farmerId: event.farmerId,
        statusFilter: event.statusFilter,
        cropTypeFilter: event.cropType,
      );
      emit(
        CropListingsLoaded(
          listings: listings,
          activeFilter: event.cropType,
        ),
      );
    } on Object catch (e) {
      emit(CropListingError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }
}
