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
abstract class ConversationContextResolver {
  final Map<String, ConversationContextInfo> _cache = {};

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
  ) async {
    final key = _key(contextType, contextId);
    final cached = _cache[key];
    if (cached != null) return cached;
    try {
      final info = await lookup(contextType, contextId);
      if (info != null) _cache[key] = info;
      return info;
    } on Object {
      return null;
    }
  }

  /// Platform-specific lookup. May throw; [resolve] handles failures.
  Future<ConversationContextInfo?> lookup(
    String contextType,
    String contextId,
  );
}
