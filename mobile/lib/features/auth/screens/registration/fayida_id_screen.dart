import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';
import 'dart:io';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Step 2: Fayida ID Verification — scan or manually enter ID.
class FayidaIdScreen extends StatefulWidget {
  final String? faydaId;
  final String? frontImagePath;
  final String? backImagePath;
  final ValueChanged<String> onFaydaIdChanged;
  final ValueChanged<String?> onFrontImageChanged;
  final ValueChanged<String?> onBackImageChanged;

  const FayidaIdScreen({
    required this.onFaydaIdChanged,
    required this.onFrontImageChanged,
    required this.onBackImageChanged,
    super.key,
    this.faydaId,
    this.frontImagePath,
    this.backImagePath,
  });

  @override
  State<FayidaIdScreen> createState() => _FayidaIdScreenState();
}

class _FayidaIdScreenState extends State<FayidaIdScreen> {
  late TextEditingController _idController;
  bool _useManualEntry = false;
  final _picker = ImagePicker();

  @override
  void initState() {
    super.initState();
    _idController = TextEditingController(text: widget.faydaId ?? '');
    _useManualEntry = widget.faydaId?.isNotEmpty ?? false;
  }

  @override
  void dispose() {
    _idController.dispose();
    super.dispose();
  }

  Future<void> _pickImage({required bool isFront}) async {
    final image = await _picker.pickImage(
      source: ImageSource.camera,
      maxWidth: 1200,
      maxHeight: 800,
      imageQuality: 80,
    );
    if (image != null) {
      if (isFront) {
        widget.onFrontImageChanged(image.path);
      } else {
        widget.onBackImageChanged(image.path);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.faydaIdVerification,
            style: const TextStyle(
              fontSize: 22,
              fontWeight: FontWeight.w700,
              color: AppTheme.textDark,
            ),
          ),
          const SizedBox(height: 8),
          Text(
            l10n.faydaIdInstructions,
            style: const TextStyle(
              fontSize: 14,
              color: AppTheme.textMuted,
            ),
          ),
          const SizedBox(height: 24),

          // Scan buttons
          Row(
            children: [
              Expanded(
                child: _ScanButton(
                  label: l10n.scanFront,
                  icon: Icons.credit_card,
                  imagePath: widget.frontImagePath,
                  onTap: () => _pickImage(isFront: true),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: _ScanButton(
                  label: l10n.scanBack,
                  icon: Icons.credit_card,
                  imagePath: widget.backImagePath,
                  onTap: () => _pickImage(isFront: false),
                ),
              ),
            ],
          ),
          const SizedBox(height: 24),

          // Manual entry toggle
          InkWell(
            onTap: () {
              setState(() => _useManualEntry = !_useManualEntry);
            },
            child: Row(
              children: [
                Icon(
                  _useManualEntry
                      ? Icons.keyboard_arrow_up
                      : Icons.keyboard_arrow_down,
                  color: AppTheme.primaryGreen,
                ),
                const SizedBox(width: 8),
                Text(
                  l10n.enterManually,
                  style: const TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: AppTheme.primaryGreen,
                  ),
                ),
              ],
            ),
          ),

          if (_useManualEntry) ...[
            const SizedBox(height: 16),
            TextField(
              controller: _idController,
              decoration: InputDecoration(
                labelText: l10n.faydaIdNumber,
                prefixIcon: const Icon(Icons.badge_outlined),
              ),
              maxLength: 20,
              onChanged: widget.onFaydaIdChanged,
            ),
          ],
        ],
      ),
    );
  }
}

class _ScanButton extends StatelessWidget {
  final String label;
  final IconData icon;
  final String? imagePath;
  final VoidCallback onTap;

  const _ScanButton({
    required this.label,
    required this.icon,
    required this.onTap,
    this.imagePath,
  });

  @override
  Widget build(BuildContext context) {
    final hasImage = imagePath != null;

    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          height: 120,
          decoration: BoxDecoration(
            color: hasImage
                ? AppTheme.statusSuccess.withAlpha(15)
                : AppTheme.surfaceWhite,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: hasImage ? AppTheme.statusSuccess : AppTheme.borderLight,
              style: hasImage ? BorderStyle.solid : BorderStyle.solid,
            ),
          ),
          child: hasImage
              ? Stack(
                  children: [
                    ClipRRect(
                      borderRadius: BorderRadius.circular(11),
                      child: Image.file(
                        File(imagePath!),
                        fit: BoxFit.cover,
                        width: double.infinity,
                        height: double.infinity,
                      ),
                    ),
                    Positioned(
                      top: 4,
                      right: 4,
                      child: Container(
                        padding: const EdgeInsets.all(4),
                        decoration: const BoxDecoration(
                          color: AppTheme.statusSuccess,
                          shape: BoxShape.circle,
                        ),
                        child: const Icon(
                          Icons.check,
                          size: 14,
                          color: Colors.white,
                        ),
                      ),
                    ),
                  ],
                )
              : Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    const Icon(
                      Icons.camera_alt_outlined,
                      size: 32,
                      color: AppTheme.textMuted,
                    ),
                    const SizedBox(height: 8),
                    Text(
                      label,
                      style: const TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w500,
                        color: AppTheme.textMuted,
                      ),
                    ),
                  ],
                ),
        ),
      ),
    );
  }
}
