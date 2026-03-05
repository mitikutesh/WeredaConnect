/// Conditional export: Isar schema on mobile, plain model on web.
export 'package:wereda_connect/core/database/schemas/sync_operation.dart'
    if (dart.library.html) 'package:wereda_connect/core/models/web/sync_operation.dart';
