import 'dart:io';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/contact_in_text.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Step 3: Role-specific fields (Fieldway Canvas: RegisterRoleDetails.dc.html).
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

    return Align(
      alignment: Alignment.topCenter,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Fayda already verified the user on the previous step; the
              // flow above shows the step counter.
              Align(
                alignment: Alignment.centerRight,
                child: Container(
                  height: 30,
                  padding: const EdgeInsets.symmetric(horizontal: 10),
                  decoration: BoxDecoration(
                    color: const Color(0xFFE3EEE6),
                    borderRadius: BorderRadius.circular(999),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(
                        Icons.check,
                        size: 14,
                        color: AppTheme.fieldGreen,
                      ),
                      const SizedBox(width: 6),
                      Text(
                        l10n.faydaVerifiedSuccess,
                        style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.fieldGreen,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 12),
              Text(
                l10n.drivingLicense,
                style: const TextStyle(
                  fontSize: 28,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -0.5,
                  color: Color(0xFF15201A),
                ),
              ),
              const SizedBox(height: 6),
              Text(
                l10n.drivingLicenseInstructions,
                style: const TextStyle(
                  fontSize: 15,
                  color: Color(0xFF5E6862),
                ),
              ),
              const SizedBox(height: 20),

              // Camera frame in slate #22302A with amber brackets
              GestureDetector(
                onTap: _pickLicenseImage,
                child: Container(
                  width: double.infinity,
                  height: 230,
                  decoration: BoxDecoration(
                    color: const Color(0xFF22302A),
                    borderRadius: BorderRadius.circular(24),
                  ),
                  child: Stack(
                    alignment: Alignment.center,
                    children: [
                      if (hasImage)
                        ClipRRect(
                          borderRadius: BorderRadius.circular(20),
                          child: kIsWeb
                              ? Image.network(
                                  widget.drivingLicensePath!,
                                  fit: BoxFit.cover,
                                  width: double.infinity,
                                  height: double.infinity,
                                  errorBuilder: (_, __, ___) => const Icon(
                                    Icons.broken_image,
                                    color: Colors.white,
                                  ),
                                )
                              : Image.file(
                                  File(widget.drivingLicensePath!),
                                  fit: BoxFit.cover,
                                  width: double.infinity,
                                  height: double.infinity,
                                ),
                        )
                      else ...[
                        // Dashed frame center
                        Container(
                          width: 260,
                          height: 160,
                          decoration: BoxDecoration(
                            borderRadius: BorderRadius.circular(14),
                            border: Border.all(
                              color: const Color(0xFF6F7D76),
                              width: 2,
                            ),
                          ),
                          child: Center(
                            child: Text(
                              l10n.placeLicenseHere,
                              style: const TextStyle(
                                color: Color(0xFFB9C2BD),
                                fontSize: 13,
                                fontWeight: FontWeight.w600,
                              ),
                            ),
                          ),
                        ),
                        // Amber corner brackets
                        Positioned(
                          left: 44,
                          top: 26,
                          child: Container(
                            width: 24,
                            height: 24,
                            decoration: const BoxDecoration(
                              border: Border(
                                left: BorderSide(
                                  color: Color(0xFFD9922B),
                                  width: 4,
                                ),
                                top: BorderSide(
                                  color: Color(0xFFD9922B),
                                  width: 4,
                                ),
                              ),
                            ),
                          ),
                        ),
                        Positioned(
                          right: 44,
                          top: 26,
                          child: Container(
                            width: 24,
                            height: 24,
                            decoration: const BoxDecoration(
                              border: Border(
                                right: BorderSide(
                                  color: Color(0xFFD9922B),
                                  width: 4,
                                ),
                                top: BorderSide(
                                  color: Color(0xFFD9922B),
                                  width: 4,
                                ),
                              ),
                            ),
                          ),
                        ),
                        Positioned(
                          left: 44,
                          bottom: 26,
                          child: Container(
                            width: 24,
                            height: 24,
                            decoration: const BoxDecoration(
                              border: Border(
                                left: BorderSide(
                                  color: Color(0xFFD9922B),
                                  width: 4,
                                ),
                                bottom: BorderSide(
                                  color: Color(0xFFD9922B),
                                  width: 4,
                                ),
                              ),
                            ),
                          ),
                        ),
                        Positioned(
                          right: 44,
                          bottom: 26,
                          child: Container(
                            width: 24,
                            height: 24,
                            decoration: const BoxDecoration(
                              border: Border(
                                right: BorderSide(
                                  color: Color(0xFFD9922B),
                                  width: 4,
                                ),
                                bottom: BorderSide(
                                  color: Color(0xFFD9922B),
                                  width: 4,
                                ),
                              ),
                            ),
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 16),

              // Amber banner
              Container(
                padding: const EdgeInsets.all(14),
                decoration: BoxDecoration(
                  color: const Color(0xFFFAEED8),
                  borderRadius: BorderRadius.circular(16),
                ),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Icon(
                      Icons.info_outline,
                      size: 18,
                      color: Color(0xFF6B400B),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        l10n.drivingLicenseRequired,
                        style: const TextStyle(
                          fontSize: 13,
                          height: 1.4,
                          color: Color(0xFF6B400B),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 16),

              // Scan Button
              SizedBox(
                width: double.infinity,
                height: 56,
                child: FilledButton.icon(
                  onPressed: _pickLicenseImage,
                  style: FilledButton.styleFrom(
                    backgroundColor: const Color(0xFF15201A),
                    foregroundColor: Colors.white,
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(16),
                    ),
                  ),
                  icon: const Icon(Icons.camera_alt_outlined, size: 20),
                  label: Text(
                    hasImage ? l10n.retakeLicensePhoto : l10n.scanLicense,
                    style: const TextStyle(
                      fontSize: 16,
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildBuyerFields(AppLocalizations l10n) {
    final buyerType = widget.buyerType ?? 'private';

    return Align(
      alignment: Alignment.topCenter,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Fayda already verified the user on the previous step; the
              // flow above shows the step counter.
              Align(
                alignment: Alignment.centerRight,
                child: Container(
                  height: 30,
                  padding: const EdgeInsets.symmetric(horizontal: 10),
                  decoration: BoxDecoration(
                    color: const Color(0xFFE3EEE6),
                    borderRadius: BorderRadius.circular(999),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(
                        Icons.check,
                        size: 14,
                        color: AppTheme.fieldGreen,
                      ),
                      const SizedBox(width: 6),
                      Text(
                        l10n.faydaVerifiedSuccess,
                        style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.fieldGreen,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 12),
              Text(
                l10n.buyerType,
                style: const TextStyle(
                  fontSize: 28,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -0.5,
                  color: Color(0xFF15201A),
                ),
              ),
              const SizedBox(height: 6),
              Text(
                l10n.buyerTypeDescription,
                style: const TextStyle(
                  fontSize: 15,
                  color: Color(0xFF5E6862),
                ),
              ),
              const SizedBox(height: 20),
              // Private / Company options
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
                    // FUP-52: the server refuses a phone number here.
                    errorText: containsPhoneNumber(_companyController.text)
                        ? l10n.errorContactInText
                        : null,
                    errorMaxLines: 3,
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
        borderRadius: BorderRadius.circular(16),
        child: Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: isSelected ? const Color(0xFFE3EEE6) : Colors.white,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(
              color: isSelected ? AppTheme.fieldGreen : const Color(0xFFE3E1D8),
              width: isSelected ? 2 : 1,
            ),
          ),
          child: Row(
            children: [
              Icon(
                icon,
                color:
                    isSelected ? AppTheme.fieldGreen : const Color(0xFF5E6862),
              ),
              const SizedBox(width: 12),
              Text(
                title,
                style: TextStyle(
                  fontSize: 15,
                  fontWeight: FontWeight.w700,
                  color: isSelected
                      ? const Color(0xFF15201A)
                      : const Color(0xFF3F4843),
                ),
              ),
              const Spacer(),
              if (isSelected)
                const Icon(
                  Icons.check_circle,
                  color: AppTheme.fieldGreen,
                ),
            ],
          ),
        ),
      ),
    );
  }
}
