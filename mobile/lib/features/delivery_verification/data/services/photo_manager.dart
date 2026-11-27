// Handoff photo capture (delivery-verification-api.md, Photo Evidence). The
// phone compresses to WebP and keeps the file until the sync queue uploads
// it (FRD-12); the browser keeps no files, so the web version uploads the
// photo as soon as it is taken.
export 'package:wereda_connect/features/delivery_verification/data/services/photo_types.dart';
export 'package:wereda_connect/features/delivery_verification/data/services/photo_manager_io.dart'
    if (dart.library.html) 'package:wereda_connect/features/delivery_verification/data/services/photo_manager_web.dart';
