import 'package:flutter/material.dart';

import 'package:wereda_connect/features/crop_listing/data/services/crop_photo_picker.dart';
import 'package:wereda_connect/features/crop_listing/domain/crop_photos.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';

/// The create form's required photo section (crop-listing-api.md 1.6.0):
/// [CropPhotoRules.minPhotos] to [CropPhotoRules.maxPhotos] photos taken
/// with the camera or chosen from the gallery, shown as square tiles (the
/// first tagged as the cover buyers see in the list), each removable. Saving
/// with too few fails the form's validation with the error under the tiles.
class CropPhotoPickerField extends StatefulWidget {
  final List<CropPhotoDraft> photos;
  final ValueChanged<List<CropPhotoDraft>> onChanged;

  /// Camera and gallery access; a real [CropPhotoPicker] by default.
  final CropPhotoPicker? picker;

  const CropPhotoPickerField({
    required this.photos,
    required this.onChanged,
    super.key,
    this.picker,
  });

  @override
  State<CropPhotoPickerField> createState() => _CropPhotoPickerFieldState();
}

class _CropPhotoPickerFieldState extends State<CropPhotoPickerField> {
  late final CropPhotoPicker _picker = widget.picker ?? CropPhotoPicker();
  final _fieldKey = GlobalKey<FormFieldState<List<CropPhotoDraft>>>();
  bool _picking = false;

  int get _room => CropPhotoRules.maxPhotos - widget.photos.length;

  Future<void> _add(
    Future<CropPhotoPickResult> Function(int room) pick,
  ) async {
    if (_picking) return;
    final l10n = AppLocalizations.of(context)!;
    final messenger = ScaffoldMessenger.of(context);
    final room = _room;
    if (room <= 0) {
      messenger.showSnackBar(
        SnackBar(
          content: Text(l10n.cropPhotosLimitReached(CropPhotoRules.maxPhotos)),
        ),
      );
      return;
    }
    setState(() => _picking = true);
    final result = await pick(room);
    if (!mounted) return;
    setState(() => _picking = false);

    final added = result.photos.take(room).toList();
    if (added.isNotEmpty) _changed([...widget.photos, ...added]);

    final message = switch (result.issue) {
      CropPhotoPickIssue.unsupported => l10n.cropPhotosUnsupported,
      CropPhotoPickIssue.tooLarge => l10n.cropPhotosTooLarge,
      CropPhotoPickIssue.failed => l10n.cropPhotosPickFailed,
      null => result.photos.length > room
          ? l10n.cropPhotosLimitReached(CropPhotoRules.maxPhotos)
          : null,
    };
    if (message != null) {
      messenger.showSnackBar(SnackBar(content: Text(message)));
    }
  }

  void _remove(CropPhotoDraft photo) {
    _changed([
      for (final p in widget.photos)
        if (p.photoId != photo.photoId) p,
    ]);
  }

  void _changed(List<CropPhotoDraft> photos) {
    widget.onChanged(photos);
    _fieldKey.currentState?.didChange(photos);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return FormField<List<CropPhotoDraft>>(
      key: _fieldKey,
      initialValue: widget.photos,
      // Once shown, the error goes as soon as there are enough photos.
      autovalidateMode: AutovalidateMode.onUserInteraction,
      validator: (_) => CropPhotoRules.isValidCount(widget.photos.length)
          ? null
          : l10n.cropPhotosRequired(CropPhotoRules.minPhotos),
      builder: (field) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child:
                    Text(l10n.cropPhotosTitle, style: LabeledField.labelStyle),
              ),
              FwPill(
                label: l10n.cropPhotosCount(
                  widget.photos.length,
                  CropPhotoRules.maxPhotos,
                ),
                background: CropPhotoRules.isValidCount(widget.photos.length)
                    ? AppTheme.fieldGreenTint
                    : AppTheme.segmentTrack,
                foreground: CropPhotoRules.isValidCount(widget.photos.length)
                    ? AppTheme.fieldGreen
                    : AppTheme.unselectedNav,
              ),
            ],
          ),
          const SizedBox(height: 4),
          Text(l10n.cropPhotosHint, style: LabeledField.helperStyle),
          const SizedBox(height: 10),
          _Tiles(
            photos: widget.photos,
            onRemove: _remove,
            onEmptyTap:
                _picking ? null : () => _add((_) => _picker.takePhoto()),
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  key: const Key('create_listing_photo_camera'),
                  onPressed: _picking || _room <= 0
                      ? null
                      : () => _add((_) => _picker.takePhoto()),
                  icon: const Icon(Icons.photo_camera_outlined, size: 18),
                  label: Text(l10n.cropPhotosTake),
                  style: OutlinedButton.styleFrom(
                    minimumSize: const Size.fromHeight(48),
                  ),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: OutlinedButton.icon(
                  key: const Key('create_listing_photo_gallery'),
                  onPressed: _picking || _room <= 0
                      ? null
                      : () => _add(
                            (room) => _picker.chooseFromGallery(limit: room),
                          ),
                  icon: const Icon(Icons.photo_library_outlined, size: 18),
                  label: Text(l10n.cropPhotosGallery),
                  style: OutlinedButton.styleFrom(
                    minimumSize: const Size.fromHeight(48),
                  ),
                ),
              ),
            ],
          ),
          if (field.hasError) ...[
            const SizedBox(height: 6),
            Text(
              field.errorText!,
              key: const Key('create_listing_photos_error'),
              style: const TextStyle(
                fontSize: 12,
                color: AppTheme.statusError,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// [CropPhotoRules.maxPhotos] square slots in one row: the photos, then
/// empty slots that open the camera.
class _Tiles extends StatelessWidget {
  final List<CropPhotoDraft> photos;
  final ValueChanged<CropPhotoDraft> onRemove;
  final VoidCallback? onEmptyTap;

  const _Tiles({
    required this.photos,
    required this.onRemove,
    required this.onEmptyTap,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return LayoutBuilder(
      builder: (context, constraints) {
        const gap = 8.0;
        const slots = CropPhotoRules.maxPhotos;
        final size = (constraints.maxWidth - gap * (slots - 1)) / slots;
        final pixels = (size * MediaQuery.devicePixelRatioOf(context)).round();
        return Row(
          children: [
            for (var i = 0; i < slots; i++) ...[
              if (i > 0) const SizedBox(width: gap),
              SizedBox(
                width: size,
                height: size,
                child: i < photos.length
                    ? _PhotoTile(
                        key: Key('create_listing_photo_$i'),
                        photo: photos[i],
                        label: l10n.cropPhotoPosition(i + 1, photos.length),
                        isCover: i == 0,
                        cacheWidth: pixels,
                        onRemove: () => onRemove(photos[i]),
                      )
                    : _EmptyTile(onTap: onEmptyTap),
              ),
            ],
          ],
        );
      },
    );
  }
}

class _PhotoTile extends StatelessWidget {
  final CropPhotoDraft photo;
  final String label;
  final bool isCover;
  final int cacheWidth;
  final VoidCallback onRemove;

  const _PhotoTile({
    required this.photo,
    required this.label,
    required this.isCover,
    required this.cacheWidth,
    required this.onRemove,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Stack(
      fit: StackFit.expand,
      children: [
        Semantics(
          image: true,
          label: label,
          child: ClipRRect(
            borderRadius: BorderRadius.circular(14),
            child: Image.memory(
              photo.bytes,
              fit: BoxFit.cover,
              cacheWidth: cacheWidth,
              gaplessPlayback: true,
            ),
          ),
        ),
        if (isCover)
          PositionedDirectional(
            start: 4,
            bottom: 4,
            child: DecoratedBox(
              decoration: BoxDecoration(
                color: AppTheme.fieldGreen,
                borderRadius: BorderRadius.circular(999),
              ),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                child: Text(
                  l10n.cropPhotoCover,
                  style: const TextStyle(
                    fontSize: 10,
                    fontWeight: FontWeight.w700,
                    color: Colors.white,
                  ),
                ),
              ),
            ),
          ),
        PositionedDirectional(
          top: 0,
          end: 0,
          child: IconButton(
            tooltip: l10n.cropPhotosRemove,
            onPressed: onRemove,
            visualDensity: VisualDensity.compact,
            style: IconButton.styleFrom(
              backgroundColor: Colors.black.withValues(alpha: 0.55),
              foregroundColor: Colors.white,
              minimumSize: const Size(32, 32),
            ),
            icon: const Icon(Icons.close, size: 16),
          ),
        ),
      ],
    );
  }
}

class _EmptyTile extends StatelessWidget {
  final VoidCallback? onTap;

  const _EmptyTile({required this.onTap});

  @override
  Widget build(BuildContext context) {
    return Material(
      color: AppTheme.surfaceWhite,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(14),
        side: const BorderSide(color: AppTheme.whisperBorder),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        // The buttons under the tiles carry the label for screen readers.
        excludeFromSemantics: true,
        child: const Center(
          child: Icon(
            Icons.add_a_photo_outlined,
            color: AppTheme.mutedSteel,
          ),
        ),
      ),
    );
  }
}
