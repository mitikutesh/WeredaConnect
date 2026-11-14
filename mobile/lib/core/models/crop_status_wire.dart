/// Crop listing rows as the server sends them (FUP-149).
///
/// The app has no `ListingStatus` for a deleted listing: a row the server
/// deleted is removed from the device (or left out of a list), never stored
/// under a status the app guesses. One reader for every place a server crop
/// row is read (the marketplace pull, a conflict echo, the web repository
/// and the web matcher), so they cannot drift apart.
library;

/// Whether the server crop row [m] is a deleted listing, by the server's own
/// rule (`CropRepository.IsDeleted`, crop-listing-api.md 1.10.0): it carries
/// a `deletedAt` stamp, or its `status` is `deleted` (a row deleted by an
/// update before the stamp was written has only the status).
bool isDeletedCropRow(Map<String, dynamic> m) {
  final deletedAt = m['deletedAt'];
  if (deletedAt != null && deletedAt.toString().isNotEmpty) return true;
  final status = m['status'];
  return status is String && status.toLowerCase() == 'deleted';
}
