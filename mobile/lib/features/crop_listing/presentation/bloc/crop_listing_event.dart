import 'dart:async';

import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';

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

  /// The 2 to 4 photos the farmer took or chose (crop-listing-api.md 1.6.0).
  final List<CropPhotoDraft> photos;

  /// The id the form made for this listing (it names the voice note).
  final String? listingId;

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
    this.photos = const [],
    this.listingId,
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
        // Ids, not the bytes: two drafts are the same photo by id.
        [for (final photo in photos) photo.photoId],
        listingId,
      ];
}

/// SIM-56: the farmer edits how much is left, the price and the description
/// of their own [listing], as the form showed it (its version is the edit's
/// base). A null field is left as it is.
class UpdateCropListing extends CropListingEvent {
  final CropListing listing;
  final String farmerId;

  /// Quintals.
  final double? quantity;
  final double? pricePerKg;
  final String? description;

  /// Completed with the saved listing, or with the error, for the screen that
  /// asked: the bloc is app-wide, so another screen's load must never be
  /// taken for this edit's answer.
  final Completer<CropListing>? done;

  const UpdateCropListing({
    required this.listing,
    required this.farmerId,
    this.quantity,
    this.pricePerKg,
    this.description,
    this.done,
  });

  @override
  List<Object?> get props => [
        listing.listingId,
        listing.version,
        farmerId,
        quantity,
        pricePerKg,
        description,
      ];
}

/// Event to delete a crop listing.
class DeleteCropListing extends CropListingEvent {
  final String listingId;
  final String farmerId;

  /// SIM-56: completed once the delete is saved (before the list reloads),
  /// or with the error, for the screen that asked (see
  /// [UpdateCropListing.done]).
  final Completer<void>? done;

  const DeleteCropListing({
    required this.listingId,
    required this.farmerId,
    this.done,
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
