import 'dart:io';

import 'package:flutter/material.dart';

import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Displays photo evidence thumbnail or a capture button.
///
/// Shows the existing photo as a 120 by 120 thumbnail (radius 14, like the
/// boards' tiles), or a chalk tile with a camera icon when no photo exists.
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
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (photoRef != null)
          ClipRRect(
            borderRadius: BorderRadius.circular(14),
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
        const SizedBox(height: 6),
        Text(
          photoType == PhotoType.pickup
              ? AppLocalizations.of(context)!.deliveryPhotoPickup
              : AppLocalizations.of(context)!.deliveryPhotoDropoff,
          style: const TextStyle(
            fontSize: 12,
            fontWeight: FontWeight.w500,
            color: AppTheme.mutedSteel,
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
    final ink = canCapture ? AppTheme.fieldGreen : AppTheme.mutedSteel;
    return Semantics(
      button: canCapture,
      child: Material(
        color: AppTheme.chalkBackground,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(14),
          side: const BorderSide(color: AppTheme.whisperBorder),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: canCapture ? onCapture : null,
          child: SizedBox(
            width: 120,
            height: 120,
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(Icons.camera_alt_outlined, size: 28, color: ink),
                const SizedBox(height: 6),
                Text(
                  // Read-only evidence with no photo says so instead of
                  // offering a capture that cannot happen.
                  canCapture
                      ? AppLocalizations.of(context)!.deliveryAddPhoto
                      : AppLocalizations.of(context)!.restNoPhoto,
                  style: TextStyle(
                    fontSize: 12,
                    fontWeight: FontWeight.w700,
                    color: ink,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
