import 'package:cached_network_image/cached_network_image.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_urls.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';
import 'package:wereda_connect/features/crop_listing/presentation/widgets/crop_photo_local_image.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// Loads a listing's photos with full-size URLs (see [fetchCropPhotos]).
typedef CropPhotoLoader = Future<List<CropPhoto>> Function(String listingId);

/// One rendition of a crop photo: this device's own file when it has one,
/// else the server's rendition through its presigned URL, cached by photo id
/// ([cropPhotoCacheKey]) so an expired URL still shows what was downloaded.
/// Anything missing (still processing, offline and never cached, a listing
/// from before photos) shows the crop's sprout tile.
class CropPhotoImage extends StatelessWidget {
  final CropPhoto photo;

  /// The 1024 px image instead of the 320 px thumb. Falls back to the thumb
  /// while the full URL is unknown, and shows the cached thumb while the
  /// full image loads.
  final bool full;

  /// Pixels to decode at most; keeps 2 GB phones from decoding big images
  /// for small tiles.
  final int? cacheWidth;
  final BoxFit fit;
  final String? cropKey;

  const CropPhotoImage({
    required this.photo,
    super.key,
    this.full = false,
    this.cacheWidth,
    this.fit = BoxFit.cover,
    this.cropKey,
  });

  @override
  Widget build(BuildContext context) {
    final local = localCropPhotoImage(photo.localPath, cacheWidth: cacheWidth);
    if (local != null) {
      return Image(
        image: local,
        fit: fit,
        gaplessPlayback: true,
        errorBuilder: (context, _, __) => _placeholder(context),
      );
    }
    final fullUrl = full ? photo.fullUrl : null;
    final url = fullUrl ?? photo.thumbUrl;
    if (url == null) return _placeholder(context);
    // While the full image loads (or if it fails), the cached thumb shows.
    final thumb = fullUrl != null && photo.thumbUrl != null
        ? _network(context, photo.thumbUrl!, full: false)
        : null;
    return _network(context, url, full: fullUrl != null, fallback: thumb);
  }

  Widget _network(
    BuildContext context,
    String url, {
    required bool full,
    Widget? fallback,
  }) {
    return CachedNetworkImage(
      imageUrl: url,
      cacheKey: cropPhotoCacheKey(photo.photoId, full: full),
      memCacheWidth: cacheWidth,
      fit: fit,
      fadeInDuration: const Duration(milliseconds: 150),
      placeholder: (context, _) => fallback ?? _placeholder(context),
      errorWidget: (context, _, __) => fallback ?? _placeholder(context),
    );
  }

  Widget _placeholder(BuildContext context) {
    final colors = cropTileColors(cropKey);
    return Semantics(
      label: AppLocalizations.of(context)?.cropPhotoUnavailable,
      child: ColoredBox(
        color: colors.background,
        child: Center(child: FwSprout(color: colors.foreground, size: 24)),
      ),
    );
  }
}

/// The marketplace card's photo: the cover thumb in a rounded square, with
/// the number of photos in the corner when there are more. Listings from
/// before photos (no photos at all) keep the crop's sprout tile.
class CropPhotoThumb extends StatelessWidget {
  final CropListing listing;
  final double size;

  const CropPhotoThumb({required this.listing, super.key, this.size = 64});

  @override
  Widget build(BuildContext context) {
    final photos = listing.photos;
    final colors = cropTileColors(listing.cropType);
    final count = photos.length;
    final pixels = (size * MediaQuery.devicePixelRatioOf(context)).round();
    return Semantics(
      image: true,
      label: count > 0
          ? AppLocalizations.of(context)!.cropPhotosCountLabel(count)
          : null,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(14),
        child: SizedBox(
          width: size,
          height: size,
          child: count == 0
              ? ColoredBox(
                  color: colors.background,
                  child: Center(
                    child: FwSprout(color: colors.foreground, size: 24),
                  ),
                )
              : Stack(
                  fit: StackFit.expand,
                  children: [
                    CropPhotoImage(
                      photo: photos.first,
                      cacheWidth: pixels,
                      cropKey: listing.cropType,
                    ),
                    if (count > 1)
                      PositionedDirectional(
                        end: 4,
                        bottom: 4,
                        child: _CountBadge(count: count),
                      ),
                  ],
                ),
        ),
      ),
    );
  }
}

class _CountBadge extends StatelessWidget {
  final int count;

  const _CountBadge({required this.count});

  @override
  Widget build(BuildContext context) {
    return ExcludeSemantics(
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: Colors.black.withValues(alpha: 0.6),
          borderRadius: BorderRadius.circular(999),
        ),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 2),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(
                Icons.photo_library_outlined,
                size: 10,
                color: Colors.white,
              ),
              const SizedBox(width: 2),
              Text(
                '$count',
                style: const TextStyle(
                  fontSize: 10,
                  fontWeight: FontWeight.w700,
                  color: Colors.white,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The listing detail's photos: a swipeable 4:3 strip with page dots and
/// previous/next arrows, each page the full 1024 px image (thumb first while
/// it loads). Tapping opens [CropPhotoViewer] to zoom. On open it reads the
/// full-size URLs once (`GET /crops/{id}`); offline or refused, it keeps the
/// thumbs. Renders nothing for a listing without photos.
class CropPhotoGallery extends StatefulWidget {
  final CropListing listing;

  /// Reads the full-size URLs; [fetchCropPhotos] by default.
  final CropPhotoLoader? loadPhotos;

  const CropPhotoGallery({required this.listing, super.key, this.loadPhotos});

  @override
  State<CropPhotoGallery> createState() => _CropPhotoGalleryState();
}

class _CropPhotoGalleryState extends State<CropPhotoGallery> {
  late List<CropPhoto> _photos = widget.listing.photos;
  final PageController _controller = PageController();
  int _page = 0;

  @override
  void initState() {
    super.initState();
    final needsUrls = _photos.any(
      (photo) => photo.fullUrl == null && photo.localPath == null,
    );
    if (needsUrls) _loadFullUrls();
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  Future<void> _loadFullUrls() async {
    try {
      final load = widget.loadPhotos ?? fetchCropPhotos;
      final fresh = await load(widget.listing.listingId);
      if (!mounted || fresh.isEmpty) return;
      setState(() => _photos = mergeLocalPhotos(fresh, _photos));
    } on Object catch (e) {
      debugPrint('[CropPhotoGallery] full photos not read, keeping thumbs: $e');
    }
  }

  void _open(int index) {
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        fullscreenDialog: true,
        builder: (_) => CropPhotoViewer(
          photos: _photos,
          initialIndex: index,
          cropKey: widget.listing.cropType,
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    if (_photos.isEmpty) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context)!;
    final width = MediaQuery.sizeOf(context).width;
    final pixels =
        (width * MediaQuery.devicePixelRatioOf(context)).round().clamp(1, 1024);
    final count = _photos.length;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        ClipRRect(
          borderRadius: BorderRadius.circular(18),
          child: AspectRatio(
            aspectRatio: 4 / 3,
            child: Stack(
              fit: StackFit.expand,
              children: [
                ScrollConfiguration(
                  behavior: _photoPagerBehavior(context),
                  child: PageView.builder(
                    key: const Key('crop_photo_gallery'),
                    controller: _controller,
                    itemCount: count,
                    onPageChanged: (page) => setState(() => _page = page),
                    itemBuilder: (context, index) => Semantics(
                      image: true,
                      button: true,
                      label: l10n.cropPhotoPosition(index + 1, count),
                      child: GestureDetector(
                        onTap: () => _open(index),
                        child: CropPhotoImage(
                          photo: _photos[index],
                          full: true,
                          cacheWidth: pixels,
                          cropKey: widget.listing.cropType,
                        ),
                      ),
                    ),
                  ),
                ),
                ..._photoArrows(
                  page: _page,
                  count: count,
                  size: 36,
                  onGo: (page) => _goToPhoto(_controller, page, count),
                ),
              ],
            ),
          ),
        ),
        if (_photos.length > 1) ...[
          const SizedBox(height: 8),
          ExcludeSemantics(
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                for (var i = 0; i < _photos.length; i++)
                  AnimatedContainer(
                    duration: const Duration(milliseconds: 150),
                    margin: const EdgeInsets.symmetric(horizontal: 3),
                    width: i == _page ? 16 : 6,
                    height: 6,
                    decoration: BoxDecoration(
                      color: i == _page
                          ? AppTheme.fieldGreen
                          : AppTheme.whisperBorder,
                      borderRadius: BorderRadius.circular(999),
                    ),
                  ),
              ],
            ),
          ),
        ],
      ],
    );
  }
}

/// Full-screen photos on black, pinch to zoom. Swipe, tap the arrows or
/// press the left/right keys to move between them.
class CropPhotoViewer extends StatefulWidget {
  final List<CropPhoto> photos;
  final int initialIndex;
  final String? cropKey;

  const CropPhotoViewer({
    required this.photos,
    super.key,
    this.initialIndex = 0,
    this.cropKey,
  });

  @override
  State<CropPhotoViewer> createState() => _CropPhotoViewerState();
}

class _CropPhotoViewerState extends State<CropPhotoViewer> {
  late final PageController _controller =
      PageController(initialPage: widget.initialIndex);
  late int _page = widget.initialIndex;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final count = widget.photos.length;
    void go(int page) => _goToPhoto(_controller, page, count);
    return CallbackShortcuts(
      bindings: {
        const SingleActivator(LogicalKeyboardKey.arrowLeft): () =>
            go(_page - 1),
        const SingleActivator(LogicalKeyboardKey.arrowRight): () =>
            go(_page + 1),
      },
      child: Focus(
        autofocus: true,
        child: Scaffold(
          backgroundColor: Colors.black,
          appBar: AppBar(
            backgroundColor: Colors.black,
            foregroundColor: Colors.white,
            leading: IconButton(
              icon: const Icon(Icons.close),
              tooltip: l10n.close,
              onPressed: () => Navigator.of(context).pop(),
            ),
            title: Text(
              l10n.cropPhotoPosition(_page + 1, count),
              style: const TextStyle(fontSize: 15, color: Colors.white),
            ),
          ),
          body: Stack(
            fit: StackFit.expand,
            children: [
              // No mouse drag here: the zoom's pan claims a mouse drag
              // first, so a mouse uses the arrows or the arrow keys.
              PageView.builder(
                controller: _controller,
                itemCount: count,
                onPageChanged: (page) => setState(() => _page = page),
                itemBuilder: (context, index) => InteractiveViewer(
                  maxScale: 4,
                  child: Center(
                    child: CropPhotoImage(
                      photo: widget.photos[index],
                      full: true,
                      fit: BoxFit.contain,
                      cropKey: widget.cropKey,
                    ),
                  ),
                ),
              ),
              ..._photoArrows(page: _page, count: count, size: 48, onGo: go),
            ],
          ),
        ),
      ),
    );
  }
}

/// Flutter lets only touch drag a [PageView] by default, so on desktop web a
/// mouse could not get past the first photo. The detail strip takes any
/// pointer.
ScrollBehavior _photoPagerBehavior(BuildContext context) =>
    ScrollConfiguration.of(context).copyWith(
      dragDevices: PointerDeviceKind.values.toSet(),
      scrollbars: false,
    );

/// Slides [controller] to [page]; out of range (an arrow key at either end)
/// does nothing.
void _goToPhoto(PageController controller, int page, int count) {
  if (!controller.hasClients || page < 0 || page >= count) return;
  controller.animateToPage(
    page,
    duration: const Duration(milliseconds: 250),
    curve: Curves.easeOut,
  );
}

/// Previous and next buttons for a [Stack] over a photo pager: one swipe
/// gesture is not enough on a desktop browser. Hidden at either end.
List<Widget> _photoArrows({
  required int page,
  required int count,
  required double size,
  required ValueChanged<int> onGo,
}) {
  if (count < 2) return const [];
  return [
    if (page > 0)
      PositionedDirectional(
        start: 8,
        top: 0,
        bottom: 0,
        child: Center(
          child:
              _PhotoArrow(next: false, size: size, onTap: () => onGo(page - 1)),
        ),
      ),
    if (page < count - 1)
      PositionedDirectional(
        end: 8,
        top: 0,
        bottom: 0,
        child: Center(
          child:
              _PhotoArrow(next: true, size: size, onTap: () => onGo(page + 1)),
        ),
      ),
  ];
}

class _PhotoArrow extends StatelessWidget {
  final bool next;
  final double size;
  final VoidCallback onTap;

  const _PhotoArrow({
    required this.next,
    required this.size,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return IconButton(
      tooltip: next ? l10n.cropPhotoNext : l10n.cropPhotoPrevious,
      onPressed: onTap,
      iconSize: size * 0.6,
      style: IconButton.styleFrom(
        backgroundColor: Colors.black.withValues(alpha: 0.45),
        foregroundColor: Colors.white,
        fixedSize: Size.square(size),
        minimumSize: Size.square(size),
        padding: EdgeInsets.zero,
      ),
      icon: Icon(next ? Icons.chevron_right : Icons.chevron_left),
    );
  }
}
