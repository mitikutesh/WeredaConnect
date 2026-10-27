/// Server entity ids as the app puts them in a path: a UUID or another plain
/// token of letters, digits, `-` and `_`.
final _plainId = RegExp(r'^[A-Za-z0-9_-]{1,64}$');

/// Whether [id] is safe to place in a URL path segment as it is. An id from
/// outside the app (a web URL, push data) that is not is ignored.
bool isPlainEntityId(String id) => _plainId.hasMatch(id);
