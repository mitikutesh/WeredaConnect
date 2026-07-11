import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';

/// Web resolver: fetches the linked crop/trip/order/delivery with
/// single-entity GETs. Best-effort — server role gates (e.g. a farmer can
/// only GET own crops) always permit a chat's own participants for their
/// context entity; a chained lookup that isn't visible (a trucker resolving
/// a delivery's crop) just falls back to the generic type label.
class WebConversationContextResolver extends ConversationContextResolver {
  final ApiClient _api;

  WebConversationContextResolver({required ApiClient api}) : _api = api;

  @override
  Future<ConversationContextInfo?> lookup(
    String contextType,
    String contextId,
  ) async {
    switch (contextType) {
      case 'crop':
        return _cropInfo(contextType, contextId);
      case 'trip':
        final data = await _api.get<Map<String, dynamic>>('/trips/$contextId');
        final trip = _unwrap(data, 'trip');
        if (trip == null) return null;
        return ConversationContextInfo(
          contextType: contextType,
          fromWoreda: trip['fromWoreda'] as String?,
          toWoreda: trip['toWoreda'] as String?,
        );
      case 'order':
        // Orders denormalize their crop on the wire (cropType + quantity).
        final data = await _api.get<Map<String, dynamic>>('/orders/$contextId');
        final order = _unwrap(data, 'order');
        if (order == null) return null;
        return ConversationContextInfo(
          contextType: contextType,
          cropTypeKey: order['cropType'] as String?,
          quantityKg: (order['quantity'] as num?)?.toDouble(),
        );
      case 'delivery':
        // Deliveries only reference the listing — chain to it for the crop.
        final data =
            await _api.get<Map<String, dynamic>>('/deliveries/$contextId');
        final delivery = _unwrap(data, 'delivery');
        final listingId = delivery?['listingId'] as String?;
        if (listingId == null || listingId.isEmpty) return null;
        return _cropInfo(contextType, listingId);
      default:
        return null;
    }
  }

  /// Fetch a crop and shape it as context info for [contextType]. The crop
  /// GET is role-gated server-side (farmer: own only; buyer/admin: any) — a
  /// 403/404 throws and [ConversationContextResolver.resolve] falls back to
  /// the generic label.
  Future<ConversationContextInfo?> _cropInfo(
    String contextType,
    String listingId,
  ) async {
    final data = await _api.get<Map<String, dynamic>>('/crops/$listingId');
    final crop = _unwrap(data, 'crop');
    if (crop == null) return null;
    return ConversationContextInfo(
      contextType: contextType,
      cropTypeKey: crop['cropType'] as String?,
      // API uses "quantityKg"; sync-shaped payloads use "quantity".
      quantityKg: (crop['quantity'] as num?)?.toDouble() ??
          (crop['quantityKg'] as num?)?.toDouble(),
    );
  }

  /// Server single-GET responses wrap the entity as {"crop": {...}} /
  /// {"trip": {...}} / {"order": {...}} / {"delivery": {...}} (camelCase
  /// envelope); tolerate an `item` envelope and a bare object too.
  Map<String, dynamic>? _unwrap(Map<String, dynamic> data, String key) {
    if (data[key] is Map<String, dynamic>) {
      return data[key] as Map<String, dynamic>;
    }
    if (data['item'] is Map<String, dynamic>) {
      return data['item'] as Map<String, dynamic>;
    }
    return data.isEmpty ? null : data;
  }
}
