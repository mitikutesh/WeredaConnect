import 'package:equatable/equatable.dart';

/// Base class for crop listing events.
abstract class CropListingEvent extends Equatable {
  const CropListingEvent();

  @override
  List<Object?> get props => [];
}

/// Event to load crop listings for a farmer.
class LoadCropListings extends CropListingEvent {
  final String farmerId;

  const LoadCropListings({required this.farmerId});

  @override
  List<Object?> get props => [farmerId];
}

/// Event to create a new crop listing.
class CreateCropListing extends CropListingEvent {
  final String farmerId;
  final String? farmerName;
  final String? farmerPhone;
  final String cropType;
  final double quantity;
  final double pricePerKg;
  final double latitude;
  final double longitude;
  final String locationName;
  final String description;
  final String? audioNoteRef;
  final String? woreda;

  /// Canonical place id when [locationName] was picked from the place selector;
  /// null for free-text entries. Stored locally only.
  final String? placeId;

  const CreateCropListing({
    required this.farmerId,
    required this.cropType,
    required this.quantity,
    required this.pricePerKg,
    required this.latitude,
    required this.longitude,
    required this.locationName,
    required this.description,
    this.farmerName,
    this.farmerPhone,
    this.audioNoteRef,
    this.woreda,
    this.placeId,
  });

  @override
  List<Object?> get props => [
        farmerId,
        farmerName,
        farmerPhone,
        cropType,
        quantity,
        pricePerKg,
        latitude,
        longitude,
        locationName,
        description,
        audioNoteRef,
        woreda,
        placeId,
      ];
}

/// Event to delete a crop listing.
class DeleteCropListing extends CropListingEvent {
  final String listingId;
  final String farmerId;

  const DeleteCropListing({
    required this.listingId,
    required this.farmerId,
  });

  @override
  List<Object?> get props => [listingId, farmerId];
}

/// Event to load all active listings (browse mode for buyers).
///
/// [searchQuery] is the search box text, matched against the crop, the
/// farmer's name and the place (SIM-51).
class LoadActiveListings extends CropListingEvent {
  final String? cropTypeFilter;
  final String? searchQuery;
  final double? priceMin;
  final double? priceMax;

  const LoadActiveListings({
    this.cropTypeFilter,
    this.searchQuery,
    this.priceMin,
    this.priceMax,
  });

  @override
  List<Object?> get props => [cropTypeFilter, searchQuery, priceMin, priceMax];
}

/// Event to filter crop listings by crop type and/or status.
class FilterCropListings extends CropListingEvent {
  final String farmerId;
  final String? cropType;
  final String? statusFilter;

  const FilterCropListings({
    required this.farmerId,
    this.cropType,
    this.statusFilter,
  });

  @override
  List<Object?> get props => [farmerId, cropType, statusFilter];
}
