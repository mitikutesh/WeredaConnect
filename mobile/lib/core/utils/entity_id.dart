/// Server entity ids as the app puts them in a path: a UUID or another plain
/// token of letters, digits, `-` and `_`.
final _plainId = RegExp(r'^[A-Za-z0-9_-]{1,64}$');

/// Whether [id] is safe to place in a URL path segment as it is. An id from
/// outside the app (a web URL, push data) that is not is ignored.
bool isPlainEntityId(String id) => _plainId.hasMatch(id);

/// A UUID, the shape of every user id (the Cognito `sub`) and of the ids
/// the apps make, with or without the hyphens.
final _uuid = RegExp(
  r'^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$',
  caseSensitive: false,
);

/// Whether [text] is an id rather than words: a UUID ([_uuid]). A name or
/// a place field holding one shows nothing instead (SIM-33).
bool looksLikeEntityId(String text) => _uuid.hasMatch(text.trim());
