import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
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

/// Error state when an operation fails. [code] drives the localized, user-facing
/// message; [message] is the raw detail, kept for logging and message-based
/// special cases (e.g. FRAUD_* codes) — do NOT show it directly to users.
class CropListingError extends CropListingState {
  final String message;
  final AppErrorCode code;

  const CropListingError({
    required this.message,
    this.code = AppErrorCode.unknown,
  });

  @override
  List<Object?> get props => [message, code];
}
