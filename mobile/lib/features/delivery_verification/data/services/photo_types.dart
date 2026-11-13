import 'dart:typed_data';

/// Type of photo evidence for delivery verification.
enum PhotoType {
  /// Photo taken at pickup (farmer's location).
  pickup,

  /// Photo taken at dropoff (buyer's location).
  dropoff,

  /// The trucker's arrival photo at the drop-off, taken before the drop-off
  /// check-in (ESC-36, FUP-336).
  arrival,
}

/// Result of photo capture and compression.
class PhotoResult {
  final bool success;

  /// True when the user closed the camera without a photo: not an error to
  /// show.
  final bool cancelled;

  /// The compressed photo on the phone (mobile). Null on web.
  final String? localPath;

  /// The server's upload key when the photo already went up (web, FRD-12).
  final String? serverPhotoRef;

  /// The photo's bytes for the preview where there is no file (web).
  final Uint8List? previewBytes;

  final int? fileSizeBytes;
  final String? errorMessage;

  const PhotoResult.success({
    required this.localPath,
    required this.fileSizeBytes,
    this.serverPhotoRef,
    this.previewBytes,
  })  : success = true,
        cancelled = false,
        errorMessage = null;

  const PhotoResult.error(this.errorMessage)
      : success = false,
        cancelled = false,
        localPath = null,
        serverPhotoRef = null,
        previewBytes = null,
        fileSizeBytes = null;

  const PhotoResult.cancelled()
      : success = false,
        cancelled = true,
        errorMessage = 'Photo capture cancelled',
        localPath = null,
        serverPhotoRef = null,
        previewBytes = null,
        fileSizeBytes = null;

  /// What the handoff confirm carries: the server key when the photo is up
  /// already (web), else the local file the sync queue uploads (mobile).
  String? get photoRef => serverPhotoRef ?? localPath;
}

/// Result of photo upload to server.
class PhotoUploadResult {
  final bool success;

  /// S3 key returned by server.
  final String? serverPhotoRef;
  final String? errorMessage;

  const PhotoUploadResult.success(this.serverPhotoRef)
      : success = true,
        errorMessage = null;

  const PhotoUploadResult.error(this.errorMessage)
      : success = false,
        serverPhotoRef = null;
}
