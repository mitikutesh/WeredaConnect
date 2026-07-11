import 'package:isar/isar.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';

/// Native resolver: reads the linked crop/trip from the local Isar store.
/// Works fully offline — the entities a user chats about are the ones they
/// created or browsed, so they're normally already synced locally.
class IsarConversationContextResolver extends ConversationContextResolver {
  final Isar _isar;

  IsarConversationContextResolver({required Isar isar}) : _isar = isar;

  @override
  Future<ConversationContextInfo?> lookup(
    String contextType,
    String contextId,
  ) async {
    switch (contextType) {
      case 'crop':
        final crop = await _isar.cropListings
            .filter()
            .listingIdEqualTo(contextId)
            .findFirst();
        if (crop == null) return null;
        return ConversationContextInfo(
          contextType: contextType,
          cropTypeKey: crop.cropType,
          quantityKg: crop.quantity,
        );
      case 'trip':
        final trip =
            await _isar.trips.filter().tripIdEqualTo(contextId).findFirst();
        if (trip == null) return null;
        return ConversationContextInfo(
          contextType: contextType,
          fromWoreda: trip.fromWoreda,
          toWoreda: trip.toWoreda,
        );
      case 'order':
        // Orders denormalize their crop, so no second lookup is needed.
        final order =
            await _isar.orders.filter().orderIdEqualTo(contextId).findFirst();
        if (order == null) return null;
        return ConversationContextInfo(
          contextType: contextType,
          cropTypeKey: order.cropType,
          quantityKg: order.quantity,
        );
      case 'delivery':
        // Deliveries only reference the listing — chain to it for the crop.
        // Best-effort: a trucker may not hold the listing locally (they never
        // browse crops), in which case the generic label stands.
        final delivery = await _isar.deliverys
            .filter()
            .deliveryIdEqualTo(contextId)
            .findFirst();
        if (delivery == null) return null;
        final crop = await _isar.cropListings
            .filter()
            .listingIdEqualTo(delivery.listingId)
            .findFirst();
        if (crop == null) return null;
        return ConversationContextInfo(
          contextType: contextType,
          cropTypeKey: crop.cropType,
          quantityKg: crop.quantity,
        );
      default:
        return null;
    }
  }
}
