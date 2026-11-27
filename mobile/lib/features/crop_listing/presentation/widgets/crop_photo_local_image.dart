// The photo file a farmer's own device took, as an image (crop photos,
// crop-listing-api.md 1.6.0). Files exist on mobile only: on web this is
// always null and the photo is read from the server.
export 'package:wereda_connect/features/crop_listing/presentation/widgets/crop_photo_local_image_io.dart'
    if (dart.library.html) 'package:wereda_connect/features/crop_listing/presentation/widgets/crop_photo_local_image_web.dart';
