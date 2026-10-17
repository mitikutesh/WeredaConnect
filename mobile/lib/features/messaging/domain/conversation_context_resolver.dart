import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';
import 'package:wereda_connect/core/network/api_exception.dart';

/// What a conversation is about — the crop or trip the thread is linked to,
/// resolved from its `contextType`/`contextId`. Only the fields for the
/// matching context type are set; everything else stays null.
class ConversationContextInfo {
  final String contextType;

  /// Crop threads: the raw crop key (`teff`, `wheat`, …) and quantity in kg.
  final String? cropTypeKey;
  final double? quantityKg;

  /// Trip threads: the route endpoints.
  final String? fromWoreda;
  final String? toWoreda;

  const ConversationContextInfo({
    required this.contextType,
    this.cropTypeKey,
    this.quantityKg,
    this.fromWoreda,
    this.toWoreda,
  });
}

/// Resolves the marketplace entity a conversation is linked to, best-effort.
///
/// Platform implementations: Isar lookups on mobile, single-entity GETs on
/// web. Results are cached for the session; a null result (entity not
/// reachable — offline, deleted, not yet synced) is NOT cached so a later
/// attempt can still succeed, and callers fall back to a generic type label.
/// Two exceptions (SIM-21): concurrent resolves of one context share a
/// single lookup, and a context the server refused (401/403) stays
/// unresolved for the session instead of firing the same 403 on every
/// rebuild.
abstract class ConversationContextResolver {
  final Map<String, ConversationContextInfo> _cache = {};
  final Map<String, Future<ConversationContextInfo?>> _inflight = {};
  final Set<String> _forbidden = {};

  static String _key(String contextType, String contextId) =>
      '$contextType:$contextId';

  /// Synchronous cached view — null until [resolve] has succeeded for this
  /// context. Lets list tiles render a resolved label without a flicker.
  ConversationContextInfo? peek(String contextType, String contextId) =>
      _cache[_key(contextType, contextId)];

  /// Resolve (and cache) the context entity. Never throws.
  Future<ConversationContextInfo?> resolve(
    String contextType,
    String contextId,
  ) {
    final key = _key(contextType, contextId);
    final cached = _cache[key];
    if (cached != null) return Future.value(cached);
    if (_forbidden.contains(key)) return Future.value();
    final pending = _inflight[key];
    if (pending != null) return pending;
    final lookupFuture = _lookupSafely(key, contextType, contextId);
    _inflight[key] = lookupFuture;
    return lookupFuture;
  }

  Future<ConversationContextInfo?> _lookupSafely(
    String key,
    String contextType,
    String contextId,
  ) async {
    try {
      final info = await lookup(contextType, contextId);
      if (info != null) _cache[key] = info;
      return info;
    } on AuthException {
      _forbidden.add(key);
      return null;
    } on Object {
      return null;
    } finally {
      _inflight.remove(key);
    }
  }

  /// Platform-specific lookup. May throw; [resolve] handles failures.
  Future<ConversationContextInfo?> lookup(
    String contextType,
    String contextId,
  );

  /// ESC-48: whether the thread takes new messages, and the counterpart's
  /// number for Call/Copy. Crop and trip threads are closed; an order thread
  /// follows its order, a delivery thread the delivery's order
  /// (order-lifecycle.md 1.18.0). Never cached, the order's status moves.
  /// Never throws: an unreachable order is [ConversationContact.unknown].
  Future<ConversationContact> resolveContact(
    String contextType,
    String contextId,
    String counterpartId,
  ) async {
    if (contextType == 'crop' || contextType == 'trip') {
      return const ConversationContact(
        open: false,
        closedReason: ContactClosedReason.beforePayment,
      );
    }
    try {
      final order = await lookupContactOrder(contextType, contextId);
      if (order == null) return ConversationContact.unknown;
      if (isOrderContactOpen(order)) {
        return ConversationContact(
          open: true,
          phone: orderPartyPhone(order, counterpartId),
        );
      }
      return ConversationContact(
        open: false,
        closedReason: order.status == OrderStatus.funded
            ? ContactClosedReason.beforeAccept
            : contactNotYetOpenOrderStatuses.contains(order.status)
                ? ContactClosedReason.beforePayment
                : ContactClosedReason.closed,
      );
    } on Object {
      return ConversationContact.unknown;
    }
  }

  /// The order behind an `order` or `delivery` thread, or null when it is not
  /// reachable (not synced, offline on web). May throw.
  Future<Order?> lookupContactOrder(String contextType, String contextId);
}

/// ESC-48: why a conversation takes no new messages, so the screen can say
/// when (or whether) contact opens.
enum ContactClosedReason {
  /// Before any order is paid (every crop and trip thread, an unpaid order).
  beforePayment,

  /// The order is paid but the farmer has not accepted it yet.
  beforeAccept,

  /// The money went back or the dispute was decided: contact stays closed.
  closed,
}

/// ESC-48: the contact state of a conversation.
class ConversationContact {
  /// True when the thread takes new messages, false when it is known closed,
  /// null when it could not be resolved (the composer stays, the server
  /// decides).
  final bool? open;

  /// The counterpart's number, only on an open order that carries it.
  final String? phone;

  /// Why the thread is closed; null unless [open] is false.
  final ContactClosedReason? closedReason;

  const ConversationContact({
    required this.open,
    this.phone,
    this.closedReason,
  });

  static const unknown = ConversationContact(open: null);
}
