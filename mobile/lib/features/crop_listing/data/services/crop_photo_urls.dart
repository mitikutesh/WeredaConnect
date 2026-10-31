import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';

/// Reads a listing's photos with their full-size URLs (`GET /crops/{id}`,
/// crop-listing-api.md 1.6.0). The marketplace list only carries thumbs, so
/// the detail asks for these when it opens. Throws when the read fails; the
/// caller keeps the thumbs it has.
Future<List<CropPhoto>> fetchCropPhotos(String listingId) async {
  final api = ApiClient();
  if (!api.isInitialized) return const [];
  final data = await api.get<Map<String, dynamic>>('/crops/$listingId');
  // GET /crops/{id} answers {"crop": {...}}; accept a bare object too.
  final crop = data['crop'] ?? data['item'] ?? data;
  return crop is Map ? parseCropPhotos(crop['photos']) : const [];
}
