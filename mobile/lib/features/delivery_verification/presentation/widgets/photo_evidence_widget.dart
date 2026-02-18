import 'dart:io';

import 'package:flutter/material.dart';

import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Displays photo evidence thumbnail or a capture button.
///
/// Shows existing photo as a 120x120 thumbnail, or a dashed-border
/// container with camera icon when no photo exists.
class PhotoEvidenceWidget extends StatelessWidget {
  final String? photoRef;
  final PhotoType photoType;
  final String deliveryId;
  final VoidCallback? onCapture;
  final bool canCapture;

  const PhotoEvidenceWidget({
    required this.photoType,
    required this.deliveryId,
    super.key,
    this.photoRef,
    this.onCapture,
    this.canCapture = true,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (photoRef != null)
          ClipRRect(
            borderRadius: BorderRadius.circular(8),
            child: Image.file(
              File(photoRef!),
              width: 120,
              height: 120,
              fit: BoxFit.cover,
              errorBuilder: (context, error, stackTrace) {
                return _PlaceholderBox(
                  canCapture: canCapture,
                  onCapture: onCapture,
                );
              },
            ),
          )
        else
          _PlaceholderBox(
            canCapture: canCapture,
            onCapture: onCapture,
          ),
        const SizedBox(height: 4),
        Text(
          photoType == PhotoType.pickup
              ? AppLocalizations.of(context)!.deliveryPhotoPickup
              : AppLocalizations.of(context)!.deliveryPhotoDropoff,
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
          ),
        ),
      ],
    );
  }
}

class _PlaceholderBox extends StatelessWidget {
  final bool canCapture;
  final VoidCallback? onCapture;

  const _PlaceholderBox({
    required this.canCapture,
    this.onCapture,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return GestureDetector(
      onTap: canCapture ? onCapture : null,
      child: Container(
        width: 120,
        height: 120,
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(8),
          border: Border.all(
            color: theme.colorScheme.outline.withValues(alpha: 0.5),
          ),
          color:
              theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.3),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              Icons.camera_alt,
              size: 32,
              color: canCapture
                  ? theme.colorScheme.primary
                  : theme.colorScheme.onSurface.withValues(alpha: 0.3),
            ),
            const SizedBox(height: 4),
            Text(
              AppLocalizations.of(context)!.deliveryAddPhoto,
              style: theme.textTheme.bodySmall?.copyWith(
                color: canCapture
                    ? theme.colorScheme.primary
                    : theme.colorScheme.onSurface.withValues(alpha: 0.3),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
