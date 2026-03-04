/// Conditional export: Isar schema on mobile, plain model on web.
export 'package:wereda_connect/core/database/schemas/user.dart'
    if (dart.library.html) 'package:wereda_connect/core/models/web/user.dart';
