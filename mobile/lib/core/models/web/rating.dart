/// Web-safe Rating model (no Isar dependency).
///
/// Mirrors [core/database/schemas/rating.dart] without Isar annotations or
/// generated code, so it compiles to JavaScript without 64-bit integer errors.
class Rating {
  int id = 0;

  late String ratingId;
  late String orderId;
  late String raterId;
  late String rateeId;
  late String rateeRole;
  late int stars;
  String? comment;
  late DateTime createdAt;
  late DateTime updatedAt;
  late int version;
}
