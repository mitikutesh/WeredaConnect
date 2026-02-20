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
  final String cropType;
  final double quantity;
  final double pricePerKg;
  final double latitude;
  final double longitude;
  final String description;
  final String? audioNoteRef;

  const CreateCropListing({
    required this.farmerId,
    required this.cropType,
    required this.quantity,
    required this.pricePerKg,
    required this.latitude,
    required this.longitude,
    required this.description,
    this.audioNoteRef,
  });

  @override
  List<Object?> get props => [
        farmerId,
        cropType,
        quantity,
        pricePerKg,
        latitude,
        longitude,
        description,
        audioNoteRef,
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

/// Event to load all active listings (browse mode for truckers/buyers).
class LoadActiveListings extends CropListingEvent {
  final String? cropTypeFilter;

  const LoadActiveListings({this.cropTypeFilter});

  @override
  List<Object?> get props => [cropTypeFilter];
}

/// Event to filter crop listings by crop type.
class FilterCropListings extends CropListingEvent {
  final String farmerId;
  final String? cropType;

  const FilterCropListings({
    required this.farmerId,
    this.cropType,
  });

  @override
  List<Object?> get props => [farmerId, cropType];
}
