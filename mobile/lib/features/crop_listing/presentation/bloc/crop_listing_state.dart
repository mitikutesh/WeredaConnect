import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';

/// Base class for crop listing states.
abstract class CropListingState extends Equatable {
  const CropListingState();

  @override
  List<Object?> get props => [];
}

/// Initial state before any data is loaded.
class CropListingInitial extends CropListingState {
  const CropListingInitial();
}

/// Loading state during data operations.
class CropListingLoading extends CropListingState {
  const CropListingLoading();
}

/// State when listings have been successfully loaded.
class CropListingsLoaded extends CropListingState {
  final List<CropListing> listings;
  final String? activeFilter;

  const CropListingsLoaded({
    required this.listings,
    this.activeFilter,
  });

  @override
  List<Object?> get props => [listings, activeFilter];
}

/// State after a listing has been successfully created.
class CropListingCreated extends CropListingState {
  final CropListing listing;

  const CropListingCreated({required this.listing});

  @override
  List<Object?> get props => [listing];
}

/// Error state when an operation fails.
class CropListingError extends CropListingState {
  final String message;

  const CropListingError({required this.message});

  @override
  List<Object?> get props => [message];
}
