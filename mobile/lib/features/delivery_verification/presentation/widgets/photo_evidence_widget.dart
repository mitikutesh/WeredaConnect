import 'package:cached_network_image/cached_network_image.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import 'package:wereda_connect/features/crop_listing/presentation/widgets/crop_photo_local_image.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/delivery_photo_urls.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Displays photo evidence thumbnail or a capture button.
///
/// Shows the existing photo as a 120 by 120 thumbnail (radius 14, like the
/// boards' tiles), or a chalk tile with a camera icon when no photo exists.
///
/// FRD-12: the photo is this phone's own file while it waits to upload, the
/// bytes the browser just took ([previewBytes]), or, once the server holds
/// it, the presigned GET that `GET /deliveries/{id}` gives the delivery's
/// parties, cached on the phone by its key. Offline and never cached, the
/// tile says it shows when online.
///
/// [size] is the tile's side (120 by default; the trip log shows an 80 px
/// tile and a large one in its viewer); under 100 the placeholders keep
/// their icon and drop their words. [showLabel] off leaves the photo's name
/// to the caller.
class PhotoEvidenceWidget extends StatelessWidget {
  final String? photoRef;
  final PhotoType photoType;
  final String deliveryId;
  final VoidCallback? onCapture;
  final bool canCapture;

  /// The photo the browser just took (no file on web).
  final Uint8List? previewBytes;

  /// Loads the server photo's URL; [fetchDeliveryPhotoUrl] by default.
  final DeliveryPhotoUrlLoader? photoUrlLoader;

  /// The side of the square tile.
  final double size;

  /// The photo's name under the tile.
  final bool showLabel;

  const PhotoEvidenceWidget({
    required this.photoType,
    required this.deliveryId,
    super.key,
    this.photoRef,
    this.onCapture,
    this.canCapture = true,
    this.previewBytes,
    this.photoUrlLoader,
    this.size = 120,
    this.showLabel = true,
  });

  /// Pixels the tile decodes at most: three per dp, 360 for the 120 dp
  /// tile (2 GB phones).
  int get _decodeWidth => (size * 3).round();

  @override
  Widget build(BuildContext context) {
    if (!showLabel) return _photo(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _photo(context),
        const SizedBox(height: 6),
        Text(
          photoTypeLabel(AppLocalizations.of(context)!, photoType),
          style: const TextStyle(
            fontSize: 12,
            fontWeight: FontWeight.w500,
            color: AppTheme.slate,
          ),
        ),
      ],
    );
  }

  Widget _photo(BuildContext context) {
    final placeholder = _PlaceholderBox(
      size: size,
      canCapture: canCapture,
      onCapture: onCapture,
    );
    final bytes = previewBytes;
    if (bytes != null) {
      return _Frame(
        size: size,
        child: Image.memory(
          bytes,
          fit: BoxFit.cover,
          cacheWidth: _decodeWidth,
          errorBuilder: (context, _, __) => placeholder,
        ),
      );
    }
    final ref = photoRef;
    if (ref == null) return placeholder;
    if (DeliveryConstants.isServerPhotoRef(ref, deliveryId)) {
      return _RemotePhoto(
        key: ValueKey(ref),
        deliveryId: deliveryId,
        photoType: photoType,
        photoRef: ref,
        size: size,
        loader: photoUrlLoader ?? fetchDeliveryPhotoUrl,
      );
    }
    final local = localCropPhotoImage(ref, cacheWidth: _decodeWidth);
    if (local == null) return placeholder;
    return _Frame(
      size: size,
      child: Image(
        image: local,
        fit: BoxFit.cover,
        errorBuilder: (context, _, __) => placeholder,
      ),
    );
  }
}

/// The name of a handoff photo under its tile.
String photoTypeLabel(AppLocalizations l10n, PhotoType type) => switch (type) {
      PhotoType.pickup => l10n.deliveryPhotoPickup,
      PhotoType.dropoff => l10n.deliveryPhotoDropoff,
      // ESC-36 (FUP-336): the trucker's arrival photo at the drop-off.
      PhotoType.arrival => l10n.deliveryPhotoArrival,
    };

/// The rounded square tile a photo sits in.
class _Frame extends StatelessWidget {
  final double size;
  final Widget child;

  const _Frame({required this.size, required this.child});

  @override
  Widget build(BuildContext context) {
    return ClipRRect(
      borderRadius: BorderRadius.circular(14),
      child: SizedBox(width: size, height: size, child: child),
    );
  }
}

/// A photo the server holds: its URL is read once, the image is cached by
/// its key (a fresh URL each read must not download it again).
class _RemotePhoto extends StatefulWidget {
  final String deliveryId;
  final PhotoType photoType;
  final String photoRef;
  final double size;
  final DeliveryPhotoUrlLoader loader;

  const _RemotePhoto({
    required this.deliveryId,
    required this.photoType,
    required this.photoRef,
    required this.size,
    required this.loader,
    super.key,
  });

  @override
  State<_RemotePhoto> createState() => _RemotePhotoState();
}

class _RemotePhotoState extends State<_RemotePhoto> {
  String? _url;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    String? url;
    try {
      url = await widget.loader(widget.deliveryId, widget.photoType);
    } on Object catch (e) {
      debugPrint('[PhotoEvidence] photo URL not loaded: $e');
    }
    if (!mounted) return;
    setState(() {
      _url = url;
      _loading = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    final url = _url;
    if (url == null) {
      return _loading
          ? _LoadingBox(size: widget.size)
          : _OnlineOnlyBox(size: widget.size);
    }
    return _Frame(
      size: widget.size,
      child: Semantics(
        image: true,
        label: photoTypeLabel(AppLocalizations.of(context)!, widget.photoType),
        child: CachedNetworkImage(
          imageUrl: url,
          cacheKey: widget.photoRef,
          memCacheWidth: (widget.size * 3).round(),
          fit: BoxFit.cover,
          fadeInDuration: const Duration(milliseconds: 150),
          placeholder: (context, _) => _LoadingBox(size: widget.size),
          errorWidget: (context, _, __) => _OnlineOnlyBox(size: widget.size),
        ),
      ),
    );
  }
}

class _LoadingBox extends StatelessWidget {
  const _LoadingBox({required this.size});

  final double size;

  @override
  Widget build(BuildContext context) {
    return _ChalkTile(
      size: size,
      child: const SizedBox(
        width: 20,
        height: 20,
        child: CircularProgressIndicator(strokeWidth: 2),
      ),
    );
  }
}

/// The server holds a photo this phone cannot show now (offline, never
/// cached, or the read failed).
class _OnlineOnlyBox extends StatelessWidget {
  const _OnlineOnlyBox({required this.size});

  final double size;

  @override
  Widget build(BuildContext context) {
    final words = AppLocalizations.of(context)!.deliveryPhotoOnlineOnly;
    // A small tile has no room for the words: the icon carries them for a
    // screen reader.
    if (size < _wordsFrom) {
      return _ChalkTile(
        size: size,
        child: Icon(
          Icons.cloud_off_outlined,
          size: 24,
          color: AppTheme.slate,
          semanticLabel: words,
        ),
      );
    }
    return _ChalkTile(
      size: size,
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(
            Icons.cloud_off_outlined,
            size: 24,
            color: AppTheme.slate,
          ),
          const SizedBox(height: 6),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8),
            child: Text(
              words,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 11,
                fontWeight: FontWeight.w600,
                color: AppTheme.slate,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// From this side a placeholder tile has room for its words.
const double _wordsFrom = 100;

/// The chalk square tile of the placeholders.
class _ChalkTile extends StatelessWidget {
  final double size;
  final Widget child;

  const _ChalkTile({required this.size, required this.child});

  @override
  Widget build(BuildContext context) {
    return Material(
      color: AppTheme.ground,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(14),
        side: const BorderSide(color: AppTheme.line),
      ),
      clipBehavior: Clip.antiAlias,
      child: SizedBox(width: size, height: size, child: Center(child: child)),
    );
  }
}

class _PlaceholderBox extends StatelessWidget {
  final double size;
  final bool canCapture;
  final VoidCallback? onCapture;

  const _PlaceholderBox({
    required this.size,
    required this.canCapture,
    this.onCapture,
  });

  @override
  Widget build(BuildContext context) {
    final ink = canCapture ? context.roleColors.primary : AppTheme.slate;
    // Read-only evidence with no photo says so instead of offering a
    // capture that cannot happen.
    final words = canCapture
        ? AppLocalizations.of(context)!.deliveryAddPhoto
        : AppLocalizations.of(context)!.restNoPhoto;
    final small = size < _wordsFrom;
    return Semantics(
      button: canCapture,
      child: Material(
        color: AppTheme.ground,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(14),
          side: const BorderSide(color: AppTheme.line),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: canCapture ? onCapture : null,
          child: SizedBox(
            width: size,
            height: size,
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(
                  Icons.camera_alt_outlined,
                  size: 28,
                  color: ink,
                  semanticLabel: small ? words : null,
                ),
                if (!small) ...[
                  const SizedBox(height: 6),
                  Text(
                    words,
                    style: TextStyle(
                      fontSize: 12,
                      fontWeight: FontWeight.w700,
                      color: ink,
                    ),
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}
