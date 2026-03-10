// Conditional export: Isar schema on mobile, plain model on web.
export 'package:wereda_connect/core/database/schemas/crop_listing.dart'
    if (dart.library.html) 'package:wereda_connect/core/models/web/crop_listing.dart';
