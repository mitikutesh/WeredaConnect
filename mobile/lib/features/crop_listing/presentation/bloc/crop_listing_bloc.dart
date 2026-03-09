import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';

/// BLoC for managing crop listing state.
///
/// Handles creating, loading, filtering, and deleting crop listings.
/// All operations go through the BaseCropListingRepository (mobile or web).
class CropListingBloc extends Bloc<CropListingEvent, CropListingState> {
  final BaseCropListingRepository _repository;

  CropListingBloc({required BaseCropListingRepository repository})
      : _repository = repository,
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
      emit(CropListingError(message: e.toString()));
    }
  }

  Future<void> _onLoadActiveListings(
    LoadActiveListings event,
    Emitter<CropListingState> emit,
  ) async {
    emit(const CropListingLoading());

    try {
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
      emit(CropListingError(message: e.toString()));
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
        cropType: event.cropType,
        quantity: event.quantity,
        pricePerKg: event.pricePerKg,
        latitude: event.latitude,
        longitude: event.longitude,
        locationName: event.locationName,
        description: event.description,
        audioNoteRef: event.audioNoteRef,
        woreda: event.woreda,
      );
      emit(CropListingCreated(listing: listing));
    } on Object catch (e) {
      emit(CropListingError(message: e.toString()));
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
      emit(CropListingError(message: e.toString()));
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
      emit(CropListingError(message: e.toString()));
    }
  }
}
