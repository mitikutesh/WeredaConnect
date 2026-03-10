// Conditional export: Isar schema on mobile, plain model on web.
export 'package:wereda_connect/core/database/schemas/payment_intent.dart'
    if (dart.library.html) 'package:wereda_connect/core/models/web/payment_intent.dart';
