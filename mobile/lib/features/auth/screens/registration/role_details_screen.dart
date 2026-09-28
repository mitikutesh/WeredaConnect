import 'dart:io';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Step 3: Role-specific fields.
///
/// - Trucker: Driving license scan (required)
/// - Buyer: Private/Company selection + company name
/// - Farmer: Skipped
class RoleDetailsScreen extends StatefulWidget {
  final UserRole role;
  final String? drivingLicensePath;
  final String? buyerType;
  final String? companyName;
  final ValueChanged<String?> onDrivingLicenseChanged;
  final ValueChanged<String> onBuyerTypeChanged;
  final ValueChanged<String> onCompanyNameChanged;

  const RoleDetailsScreen({
    required this.role,
    required this.onDrivingLicenseChanged,
    required this.onBuyerTypeChanged,
    required this.onCompanyNameChanged,
    super.key,
    this.drivingLicensePath,
    this.buyerType,
    this.companyName,
  });

  @override
  State<RoleDetailsScreen> createState() => _RoleDetailsScreenState();
}

class _RoleDetailsScreenState extends State<RoleDetailsScreen> {
  late TextEditingController _companyController;
  final _picker = ImagePicker();

  @override
  void initState() {
    super.initState();
    _companyController = TextEditingController(text: widget.companyName ?? '');
  }

  @override
  void dispose() {
    _companyController.dispose();
    super.dispose();
  }

  Future<void> _pickLicenseImage() async {
    final image = await _picker.pickImage(
      source: ImageSource.camera,
      maxWidth: 1200,
      maxHeight: 800,
      imageQuality: 80,
    );
    if (image != null) {
      widget.onDrivingLicenseChanged(image.path);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    if (widget.role == UserRole.trucker) {
      return _buildTruckerFields(l10n);
    }
    if (widget.role == UserRole.buyer) {
      return _buildBuyerFields(l10n);
    }
    // Farmer — should not reach here (step is skipped)
    return const SizedBox.shrink();
  }

  Widget _buildTruckerFields(AppLocalizations l10n) {
    final hasImage = widget.drivingLicensePath != null;

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                l10n.drivingLicense,
                style: const TextStyle(
                  fontSize: 22,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.textDark,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                l10n.drivingLicenseInstructions,
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(height: 24),
              // License scan
              Material(
                color: Colors.transparent,
                child: InkWell(
                  onTap: _pickLicenseImage,
                  borderRadius: BorderRadius.circular(12),
                  child: Container(
                    width: double.infinity,
                    height: 160,
                    decoration: BoxDecoration(
                      color: hasImage
                          ? AppTheme.statusSuccess.withAlpha(15)
                          : AppTheme.surfaceWhite,
                      borderRadius: BorderRadius.circular(12),
                      border: Border.all(
                        color: hasImage
                            ? AppTheme.statusSuccess
                            : AppTheme.borderLight,
                      ),
                    ),
                    child: hasImage
                        ? Stack(
                            children: [
                              ClipRRect(
                                borderRadius: BorderRadius.circular(11),
                                // dart:io File is unavailable on web (throws at
                                // runtime). On web the picker yields a blob URL, so
                                // render it via Image.network; File path only on IO.
                                child: kIsWeb
                                    ? Image.network(
                                        widget.drivingLicensePath!,
                                        fit: BoxFit.cover,
                                        width: double.infinity,
                                        height: double.infinity,
                                        errorBuilder: (_, __, ___) =>
                                            const Icon(Icons.broken_image),
                                      )
                                    : Image.file(
                                        File(widget.drivingLicensePath!),
                                        fit: BoxFit.cover,
                                        width: double.infinity,
                                        height: double.infinity,
                                      ),
                              ),
                              Positioned(
                                top: 8,
                                right: 8,
                                child: Container(
                                  padding: const EdgeInsets.all(4),
                                  decoration: const BoxDecoration(
                                    color: AppTheme.statusSuccess,
                                    shape: BoxShape.circle,
                                  ),
                                  child: const Icon(
                                    Icons.check,
                                    size: 16,
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
                                size: 40,
                                color: AppTheme.textMuted,
                              ),
                              const SizedBox(height: 8),
                              Text(
                                l10n.scanLicense,
                                style: const TextStyle(
                                  fontSize: 14,
                                  color: AppTheme.textMuted,
                                ),
                              ),
                            ],
                          ),
                  ),
                ),
              ),
              if (!hasImage) ...[
                const SizedBox(height: 12),
                Text(
                  l10n.drivingLicenseRequired,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.textMuted,
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildBuyerFields(AppLocalizations l10n) {
    final buyerType = widget.buyerType ?? 'private';

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                l10n.buyerType,
                style: const TextStyle(
                  fontSize: 22,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.textDark,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                l10n.buyerTypeDescription,
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(height: 24),
              // Private / Company radio
              _BuyerTypeOption(
                title: l10n.privateBuyer,
                icon: Icons.person_outline,
                isSelected: buyerType == 'private',
                onTap: () => widget.onBuyerTypeChanged('private'),
              ),
              const SizedBox(height: 12),
              _BuyerTypeOption(
                title: l10n.companyBuyer,
                icon: Icons.business,
                isSelected: buyerType == 'company',
                onTap: () => widget.onBuyerTypeChanged('company'),
              ),
              if (buyerType == 'company') ...[
                const SizedBox(height: 20),
                TextField(
                  controller: _companyController,
                  decoration: InputDecoration(
                    labelText: l10n.companyName,
                    prefixIcon: const Icon(Icons.business_outlined),
                    helperText: _companyController.text.trim().isEmpty
                        ? l10n.companyNameRequired
                        : null,
                  ),
                  onChanged: widget.onCompanyNameChanged,
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _BuyerTypeOption extends StatelessWidget {
  final String title;
  final IconData icon;
  final bool isSelected;
  final VoidCallback onTap;

  const _BuyerTypeOption({
    required this.title,
    required this.icon,
    required this.isSelected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: isSelected
                ? AppTheme.primaryGreen.withAlpha(15)
                : AppTheme.surfaceWhite,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: isSelected ? AppTheme.primaryGreen : AppTheme.borderLight,
              width: isSelected ? 2 : 1,
            ),
          ),
          child: Row(
            children: [
              Icon(
                icon,
                color: isSelected ? AppTheme.primaryGreen : AppTheme.textMuted,
              ),
              const SizedBox(width: 12),
              Text(
                title,
                style: TextStyle(
                  fontSize: 15,
                  fontWeight: FontWeight.w600,
                  color: isSelected ? AppTheme.primaryGreen : AppTheme.textDark,
                ),
              ),
              const Spacer(),
              if (isSelected)
                const Icon(
                  Icons.check_circle,
                  color: AppTheme.primaryGreen,
                ),
            ],
          ),
        ),
      ),
    );
  }
}
