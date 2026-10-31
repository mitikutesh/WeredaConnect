import 'package:flutter/material.dart';

import 'package:wereda_connect/core/utils/contact_in_text.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

/// Step 4: Personal Info — phone, name, woreda, password (the
/// RegisterPersonal board: labels above the fields, the locked legal name
/// first, the two passwords side by side). The flow above draws the header
/// and the Register button.
class PersonalInfoScreen extends StatefulWidget {
  final String phone;
  final String name;
  final String woreda;
  final String password;
  final String confirmPassword;
  final ValueChanged<String> onPhoneChanged;
  final ValueChanged<String> onNameChanged;
  final ValueChanged<String> onWeredaChanged;
  final ValueChanged<String> onPasswordChanged;
  final ValueChanged<String> onConfirmPasswordChanged;
  final GlobalKey<FormState> formKey;
  final bool faydaVerified;
  final String? faydaVerifiedName;

  const PersonalInfoScreen({
    required this.phone,
    required this.name,
    required this.woreda,
    required this.password,
    required this.confirmPassword,
    required this.onPhoneChanged,
    required this.onNameChanged,
    required this.onWeredaChanged,
    required this.onPasswordChanged,
    required this.onConfirmPasswordChanged,
    required this.formKey,
    this.faydaVerified = false,
    this.faydaVerifiedName,
    super.key,
  });

  @override
  State<PersonalInfoScreen> createState() => _PersonalInfoScreenState();
}

class _PersonalInfoScreenState extends State<PersonalInfoScreen> {
  late TextEditingController _phoneController;
  late TextEditingController _nameController;
  late TextEditingController _weredaController;
  late TextEditingController _passwordController;
  late TextEditingController _confirmController;
  bool _obscurePassword = true;
  bool _obscureConfirm = true;

  @override
  void initState() {
    super.initState();
    _phoneController = TextEditingController(
      // Grouped as the field groups what is typed ("+251 911 234 567").
      text: widget.phone.isEmpty ? '' : groupPhoneDigits(widget.phone),
    );
    _nameController = TextEditingController(text: widget.name);
    _weredaController = TextEditingController(text: widget.woreda);
    _passwordController = TextEditingController(text: widget.password);
    _confirmController = TextEditingController(text: widget.confirmPassword);
  }

  @override
  void dispose() {
    _phoneController.dispose();
    _nameController.dispose();
    _weredaController.dispose();
    _passwordController.dispose();
    _confirmController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(20, 22, 20, 16),
      child: Form(
        key: widget.formKey,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            JoinTitle(
              title: l10n.personalInfo,
              subtitle: l10n.personalInfoDescription,
            ),
            const SizedBox(height: 20),

            // Verified legal name (shown when Fayda verified): a grey, read
            // only row with the "From Fayda" lock tag.
            if (widget.faydaVerified && widget.faydaVerifiedName != null) ...[
              Text(l10n.verifiedLegalName, style: LabeledField.labelStyle),
              const SizedBox(height: 6),
              Container(
                width: double.infinity,
                constraints: const BoxConstraints(minHeight: 48),
                padding: const EdgeInsets.symmetric(horizontal: 14),
                decoration: BoxDecoration(
                  color: AppTheme.segmentTrack,
                  borderRadius: BorderRadius.circular(14),
                ),
                child: Row(
                  children: [
                    Expanded(
                      child: Text(
                        widget.faydaVerifiedName!,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          fontSize: 16,
                          fontWeight: FontWeight.w600,
                          color: AppTheme.soilDark,
                        ),
                      ),
                    ),
                    const SizedBox(width: 8),
                    const Icon(
                      Icons.lock_outline,
                      size: 14,
                      color: AppTheme.fieldGreen,
                    ),
                    const SizedBox(width: 5),
                    Text(
                      l10n.fromFayda,
                      style: const TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w700,
                        color: AppTheme.fieldGreen,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 14),
            ],

            // Display name (Register personal board order: name,
            // phone, wereda, then the two passwords side by side).
            LabeledField(
              label: l10n.displayName,
              field: TextFormField(
                key: const Key('register_display_name'),
                controller: _nameController,
                decoration: labeledFieldDecoration().copyWith(
                  helperText: l10n.displayNameHelper,
                  errorMaxLines: 3,
                  counterText: '',
                ),
                keyboardType: TextInputType.name,
                textCapitalization: TextCapitalization.words,
                maxLength: 100,
                validator: (value) {
                  if (value == null || value.trim().isEmpty) {
                    return l10n.fieldRequired;
                  }
                  // FUP-52: listings show this name to every buyer.
                  if (containsPhoneNumber(value)) {
                    return l10n.errorContactInText;
                  }
                  return null;
                },
                onChanged: widget.onNameChanged,
              ),
            ),
            const SizedBox(height: 14),

            // Phone
            LabeledField(
              label: l10n.phoneNumber,
              field: TextFormField(
                key: const Key('register_phone'),
                controller: _phoneController,
                decoration: labeledFieldDecoration().copyWith(
                  helperText: l10n.ethiopianPhoneExample,
                  helperMaxLines: 2,
                ),
                keyboardType: TextInputType.phone,
                inputFormatters: [EthiopianPhoneInputFormatter()],
                validator: (value) {
                  if (value == null || value.isEmpty) {
                    return l10n.fieldRequired;
                  }
                  if (!isValidEthiopianPhone(value)) {
                    return l10n.invalidPhoneFormat;
                  }
                  return null;
                },
                onChanged: (value) {
                  // Normalize to canonical E.164 before passing upstream
                  final normalized = normalizeEthiopianPhone(value);
                  widget.onPhoneChanged(normalized ?? value);
                },
              ),
            ),
            const SizedBox(height: 14),

            // Woreda: the place search, with the board's helper under it.
            LabeledField(
              label: l10n.wereda,
              field: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  // No maxLength: its counter is not on the board; the
                  // server caps the name at 100 characters.
                  PlaceSelector(
                    key: const Key('register_wereda'),
                    controller: _weredaController,
                    validator: (value) {
                      if (value == null || value.trim().isEmpty) {
                        return l10n.fieldRequired;
                      }
                      // FUP-52: crop listings carry it to every buyer.
                      if (containsPhoneNumber(value)) {
                        return l10n.errorContactInText;
                      }
                      return null;
                    },
                    // Registration stores the woreda name only (no placeId on
                    // the User entity / auth contract). Selecting a place
                    // writes its canonical English name; typing reports the
                    // raw text.
                    onChanged: widget.onWeredaChanged,
                    onPlaceSelected: (place) {
                      if (place != null) widget.onWeredaChanged(place.en);
                    },
                  ),
                  const SizedBox(height: 6),
                  Text(
                    l10n.joinWeredaHelper,
                    style: const TextStyle(
                      fontSize: 12,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.mutedSteel,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 14),

            // Password and confirmation side by side
            LabeledFieldPair(
              first: LabeledField(
                label: l10n.password,
                field: TextFormField(
                  key: const Key('register_password'),
                  controller: _passwordController,
                  decoration: labeledFieldDecoration().copyWith(
                    counterText: '',
                    suffixIcon: IconButton(
                      icon: Icon(
                        _obscurePassword
                            ? Icons.visibility_off
                            : Icons.visibility,
                      ),
                      onPressed: () {
                        setState(
                          () => _obscurePassword = !_obscurePassword,
                        );
                      },
                    ),
                  ),
                  obscureText: _obscurePassword,
                  maxLength: 128,
                  validator: (value) {
                    if (value == null || value.isEmpty) {
                      return l10n.fieldRequired;
                    }
                    if (value.length < 8 ||
                        !RegExp(r'[a-z]').hasMatch(value) ||
                        !RegExp(r'[0-9]').hasMatch(value)) {
                      return l10n.passwordRequirements;
                    }
                    return null;
                  },
                  onChanged: widget.onPasswordChanged,
                ),
              ),
              second: LabeledField(
                label: l10n.confirmPassword,
                field: TextFormField(
                  key: const Key('register_confirm_password'),
                  controller: _confirmController,
                  decoration: labeledFieldDecoration().copyWith(
                    suffixIcon: IconButton(
                      icon: Icon(
                        _obscureConfirm
                            ? Icons.visibility_off
                            : Icons.visibility,
                      ),
                      onPressed: () {
                        setState(() => _obscureConfirm = !_obscureConfirm);
                      },
                    ),
                  ),
                  obscureText: _obscureConfirm,
                  validator: (value) {
                    if (value == null || value.isEmpty) {
                      return l10n.fieldRequired;
                    }
                    if (value != _passwordController.text) {
                      return l10n.passwordsDoNotMatch;
                    }
                    return null;
                  },
                  onChanged: widget.onConfirmPasswordChanged,
                ),
              ),
            ),
            const SizedBox(height: 8),
            Text(
              l10n.passwordRequirements,
              style: const TextStyle(
                fontSize: 12,
                color: AppTheme.mutedSteel,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
