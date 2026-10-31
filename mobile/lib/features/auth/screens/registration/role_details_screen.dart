import 'dart:io';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/contact_in_text.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';

/// Step 3: Role-specific fields (the RegisterRoleDetails board).
///
/// - Trucker: Driving license scan (required). The camera frame and the
///   amber note scroll; "Scan License" sits at the foot until a photo
///   exists, then the flow's Continue takes its place and "Retake License
///   Photo" moves under the frame.
/// - Buyer: Private/Company selection + company name
/// - Farmer: Skipped
///
/// The flow above draws the step header with the Identity Verified pill.
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

  static const _stepPadding = EdgeInsets.fromLTRB(20, 22, 20, 16);

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

    return Column(
      children: [
        Expanded(
          child: SingleChildScrollView(
            padding: _stepPadding,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                JoinTitle(
                  title: l10n.drivingLicense,
                  subtitle: l10n.drivingLicenseInstructions,
                ),
                const SizedBox(height: 24),
                _CameraFrame(
                  imagePath: widget.drivingLicensePath,
                  placeholder: l10n.placeLicenseHere,
                  onTap: _pickLicenseImage,
                ),
                const SizedBox(height: 16),
                JoinNote.warning(text: l10n.drivingLicenseRequired),
                if (hasImage) ...[
                  const SizedBox(height: 14),
                  OutlinedButton.icon(
                    onPressed: _pickLicenseImage,
                    icon: const Icon(Icons.camera_alt_outlined, size: 20),
                    label: Text(l10n.retakeLicensePhoto),
                  ),
                ],
              ],
            ),
          ),
        ),
        if (!hasImage)
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 28),
            child: SizedBox(
              width: double.infinity,
              height: 56,
              child: FilledButton.icon(
                onPressed: _pickLicenseImage,
                style: FilledButton.styleFrom(
                  backgroundColor: AppTheme.soilDark,
                  foregroundColor: Colors.white,
                ),
                icon: const Icon(Icons.camera_alt_outlined, size: 20),
                label: Text(l10n.scanLicense),
              ),
            ),
          ),
      ],
    );
  }

  Widget _buildBuyerFields(AppLocalizations l10n) {
    final buyerType = widget.buyerType ?? 'private';

    return SingleChildScrollView(
      padding: _stepPadding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          JoinTitle(
            title: l10n.buyerType,
            subtitle: l10n.buyerTypeDescription,
          ),
          const SizedBox(height: 24),
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
            icon: Icons.business_outlined,
            isSelected: buyerType == 'company',
            onTap: () => widget.onBuyerTypeChanged('company'),
          ),
          if (buyerType == 'company') ...[
            const SizedBox(height: 20),
            LabeledField(
              label: l10n.companyName,
              field: TextField(
                controller: _companyController,
                decoration: labeledFieldDecoration().copyWith(
                  helperText: _companyController.text.trim().isEmpty
                      ? l10n.companyNameRequired
                      : null,
                  // FUP-52: the server refuses a phone number here.
                  errorText: containsPhoneNumber(_companyController.text)
                      ? l10n.errorContactInText
                      : null,
                  errorMaxLines: 3,
                ),
                textCapitalization: TextCapitalization.words,
                onChanged: widget.onCompanyNameChanged,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// The board's camera frame: a dark slate panel, 230 tall, with a dashed
/// guide and gold corner brackets; shows the photo once one is taken.
class _CameraFrame extends StatelessWidget {
  final String? imagePath;
  final String placeholder;
  final VoidCallback onTap;

  const _CameraFrame({
    required this.imagePath,
    required this.placeholder,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final path = imagePath;
    return Semantics(
      button: true,
      label: placeholder,
      child: GestureDetector(
        onTap: onTap,
        child: Container(
          width: double.infinity,
          height: 230,
          decoration: BoxDecoration(
            color: AppTheme.soilCard,
            borderRadius: BorderRadius.circular(24),
          ),
          clipBehavior: Clip.antiAlias,
          child: path != null
              ? (kIsWeb
                  ? Image.network(
                      path,
                      fit: BoxFit.cover,
                      errorBuilder: (_, __, ___) => const Icon(
                        Icons.broken_image,
                        color: Colors.white,
                      ),
                    )
                  : Image.file(File(path), fit: BoxFit.cover))
              : Stack(
                  alignment: Alignment.center,
                  children: [
                    // Dashed guide in the middle.
                    CustomPaint(
                      painter: const _DashedRoundedRectPainter(
                        color: Color(0xFF6F7D76),
                        radius: 14,
                      ),
                      child: SizedBox(
                        width: 260,
                        height: 160,
                        child: Center(
                          child: Text(
                            placeholder,
                            style: const TextStyle(
                              color: AppTheme.soilMuted,
                              fontSize: 13,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                        ),
                      ),
                    ),
                    // Gold corner brackets.
                    const Positioned(
                      left: 44,
                      top: 26,
                      child: _Bracket(top: true, left: true),
                    ),
                    const Positioned(
                      right: 44,
                      top: 26,
                      child: _Bracket(top: true, left: false),
                    ),
                    const Positioned(
                      left: 44,
                      bottom: 26,
                      child: _Bracket(top: false, left: true),
                    ),
                    const Positioned(
                      right: 44,
                      bottom: 26,
                      child: _Bracket(top: false, left: false),
                    ),
                  ],
                ),
        ),
      ),
    );
  }
}

class _Bracket extends StatelessWidget {
  final bool top;
  final bool left;

  const _Bracket({required this.top, required this.left});

  @override
  Widget build(BuildContext context) {
    const side = BorderSide(color: AppTheme.accentSoft, width: 4);
    const corner = Radius.circular(6);
    return Container(
      width: 26,
      height: 26,
      decoration: BoxDecoration(
        border: Border(
          top: top ? side : BorderSide.none,
          bottom: top ? BorderSide.none : side,
          left: left ? side : BorderSide.none,
          right: left ? BorderSide.none : side,
        ),
        borderRadius: BorderRadius.only(
          topLeft: top && left ? corner : Radius.zero,
          topRight: top && !left ? corner : Radius.zero,
          bottomLeft: !top && left ? corner : Radius.zero,
          bottomRight: !top && !left ? corner : Radius.zero,
        ),
      ),
    );
  }
}

/// A 2 px dashed rounded rectangle along the child's edge.
class _DashedRoundedRectPainter extends CustomPainter {
  final Color color;
  final double radius;

  const _DashedRoundedRectPainter({required this.color, required this.radius});

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2;
    final path = Path()
      ..addRRect(
        RRect.fromRectAndRadius(
          Rect.fromLTWH(1, 1, size.width - 2, size.height - 2),
          Radius.circular(radius),
        ),
      );
    const dash = 6.0;
    const gap = 5.0;
    for (final metric in path.computeMetrics()) {
      var distance = 0.0;
      while (distance < metric.length) {
        final end = (distance + dash).clamp(0.0, metric.length);
        canvas.drawPath(metric.extractPath(distance, end), paint);
        distance += dash + gap;
      }
    }
  }

  @override
  bool shouldRepaint(_DashedRoundedRectPainter oldDelegate) =>
      oldDelegate.color != color || oldDelegate.radius != radius;
}

/// A buyer type choice, drawn like the role cards: white, radius 16, a dark
/// border and dot when selected.
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
    return Semantics(
      button: true,
      selected: isSelected,
      inMutuallyExclusiveGroup: true,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(16),
          child: Container(
            padding: EdgeInsets.all(isSelected ? 15 : 16),
            decoration: BoxDecoration(
              color: AppTheme.surfaceWhite,
              borderRadius: BorderRadius.circular(16),
              border: Border.all(
                color: isSelected ? AppTheme.soilDark : AppTheme.whisperBorder,
                width: isSelected ? 2 : 1,
              ),
            ),
            child: Row(
              children: [
                Container(
                  width: 40,
                  height: 40,
                  decoration: BoxDecoration(
                    color: AppTheme.dropoffBlueTint,
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Icon(icon, size: 22, color: AppTheme.dropoffBlue),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    title,
                    style: const TextStyle(
                      fontSize: 15,
                      fontWeight: FontWeight.w700,
                      color: AppTheme.soilDark,
                    ),
                  ),
                ),
                Container(
                  width: 22,
                  height: 22,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    border: Border.all(
                      color: isSelected
                          ? AppTheme.soilDark
                          : const Color(0xFFB9BDB7),
                      width: 2,
                    ),
                  ),
                  child: isSelected
                      ? Container(
                          width: 12,
                          height: 12,
                          decoration: const BoxDecoration(
                            shape: BoxShape.circle,
                            color: AppTheme.soilDark,
                          ),
                        )
                      : null,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
