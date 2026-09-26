import 'dart:convert' show jsonDecode;

/// Decode a response body into a JSON object.
///
/// Dio hands back a decoded Map on mobile, but on Flutter web the body can
/// arrive as a raw JSON String. Returns null when the body is empty, not
/// JSON, or not an object.
Map<String, dynamic>? decodeJsonMap(Object? data) {
  if (data is Map<String, dynamic>) return data;
  if (data is String && data.isNotEmpty) {
    try {
      final decoded = jsonDecode(data);
      if (decoded is Map<String, dynamic>) return decoded;
    } on FormatException {
      return null;
    }
  }
  return null;
}

/// Like [decodeJsonMap] but throws a [FormatException] naming [what] when
/// the body is not a JSON object.
Map<String, dynamic> requireJsonMap(Object? data, {String what = 'response'}) =>
    decodeJsonMap(data) ?? (throw FormatException('Unexpected $what shape'));
