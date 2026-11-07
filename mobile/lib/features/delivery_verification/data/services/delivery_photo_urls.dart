import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_types.dart';

/// Loads the presigned GET of a delivery's [PhotoType] photo, or null when
/// the delivery has none. Throws when the read fails (offline).
typedef DeliveryPhotoUrlLoader = Future<String?> Function(
  String deliveryId,
  PhotoType type,
);

/// One read of `GET /deliveries/{id}` per delivery serves both of its
/// photos for this long; the URLs themselves live 15 minutes.
const Duration _urlReuse = Duration(minutes: 10);

final Map<String, ({DateTime at, Future<Map<String, dynamic>> read})> _reads =
    {};

/// The presigned GET of the [type] photo of [deliveryId] (FRD-12,
/// delivery-verification-api.md 1.25.0). The server signs it for the
/// delivery's buyer, farmer and trucker only, on the single-delivery read;
/// lists never carry it. The pickup and drop-off tiles share one read.
Future<String?> fetchDeliveryPhotoUrl(String deliveryId, PhotoType type) async {
  final api = ApiClient();
  if (!api.isInitialized) return null;

  final now = DateTime.now();
  var entry = _reads[deliveryId];
  if (entry == null || now.difference(entry.at) >= _urlReuse) {
    entry = (
      at: now,
      read: api.get<Map<String, dynamic>>('/deliveries/$deliveryId'),
    );
    _reads[deliveryId] = entry;
  }
  final read = entry.read;

  final Map<String, dynamic> data;
  try {
    data = await read;
  } on Object {
    // A failed read is not kept: the next tile or visit asks again.
    if (identical(_reads[deliveryId]?.read, read)) _reads.remove(deliveryId);
    rethrow;
  }
  // GET /deliveries/{id} answers {"delivery": {...}}; accept a bare object too.
  final delivery = data['delivery'] ?? data;
  if (delivery is! Map) return null;
  final url =
      delivery[type == PhotoType.pickup ? 'pickupPhotoUrl' : 'dropoffPhotoUrl'];
  return url is String && url.isNotEmpty ? url : null;
}
