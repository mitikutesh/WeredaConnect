import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'package:wereda_connect/core/utils/contact_in_text.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

/// Step 4: Personal Info — phone, name, woreda, password.
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
      text: widget.phone.isEmpty ? '' : widget.phone,
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

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Form(
            key: widget.formKey,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // Top step indicator
                const Text(
                  'Step 4 of 4',
                  style: TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w700,
                    color: Color(0xFF5E6862),
                  ),
                ),
                const SizedBox(height: 12),
                // 4-step progress bar
                Row(
                  children: [
                    for (int i = 0; i < 4; i++) ...[
                      if (i > 0) const SizedBox(width: 6),
                      Expanded(
                        child: Container(
                          height: 4,
                          decoration: BoxDecoration(
                            color: AppTheme.fieldGreen,
                            borderRadius: BorderRadius.circular(999),
                          ),
                        ),
                      ),
                    ],
                  ],
                ),
                const SizedBox(height: 16),
                Text(
                  l10n.personalInfo,
                  style: const TextStyle(
                    fontSize: 28,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.5,
                    color: Color(0xFF15201A),
                  ),
                ),
                const SizedBox(height: 6),
                Text(
                  l10n.personalInfoDescription,
                  style: const TextStyle(
                    fontSize: 15,
                    color: Color(0xFF5E6862),
                  ),
                ),
                const SizedBox(height: 24),

                // Phone
                TextFormField(
                  controller: _phoneController,
                  decoration: InputDecoration(
                    labelText: l10n.phoneNumber,
                    prefixIcon: const Icon(Icons.phone),
                    hintText: l10n.ethiopianPhoneExample,
                  ),
                  keyboardType: TextInputType.phone,
                  inputFormatters: [
                    FilteringTextInputFormatter.allow(RegExp(r'[+\d]')),
                  ],
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
                const SizedBox(height: 16),

                // Verified legal name card (shown when Fayda verified)
                if (widget.faydaVerified &&
                    widget.faydaVerifiedName != null) ...[
                  Container(
                    width: double.infinity,
                    height: 52,
                    padding: const EdgeInsets.symmetric(horizontal: 16),
                    decoration: BoxDecoration(
                      color: const Color(0xFFECEAE2),
                      borderRadius: BorderRadius.circular(14),
                    ),
                    child: Row(
                      mainAxisAlignment: MainAxisAlignment.spaceBetween,
                      children: [
                        Text(
                          widget.faydaVerifiedName!,
                          style: const TextStyle(
                            fontSize: 15,
                            fontWeight: FontWeight.w700,
                            color: AppTheme.soilDark,
                          ),
                        ),
                        Container(
                          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                          decoration: BoxDecoration(
                            color: const Color(0xFFE3EEE6),
                            borderRadius: BorderRadius.circular(8),
                          ),
                          child: const Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              Icon(Icons.lock, size: 13, color: AppTheme.fieldGreen),
                              SizedBox(width: 4),
                              Text(
                                'From Fayda',
                                style: TextStyle(
                                  fontSize: 12,
                                  fontWeight: FontWeight.w700,
                                  color: AppTheme.fieldGreen,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 16),
                ],

                // Display name
                TextFormField(
                  controller: _nameController,
                  decoration: InputDecoration(
                    labelText: l10n.displayName,
                    helperText: l10n.displayNameHelper,
                    prefixIcon: const Icon(Icons.person),
                    errorMaxLines: 3,
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
                const SizedBox(height: 16),

                // Woreda
                PlaceSelector(
                  controller: _weredaController,
                  labelText: l10n.wereda,
                  prefixIcon: Icons.location_on,
                  maxLength: 100,
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
                  // Registration stores the woreda name only (no placeId on the
                  // User entity / auth contract). Selecting a place writes its
                  // canonical English name; typing reports the raw text.
                  onChanged: widget.onWeredaChanged,
                  onPlaceSelected: (place) {
                    if (place != null) widget.onWeredaChanged(place.en);
                  },
                ),
                const SizedBox(height: 16),

                // Password
                TextFormField(
                  controller: _passwordController,
                  decoration: InputDecoration(
                    labelText: l10n.password,
                    prefixIcon: const Icon(Icons.lock),
                    helperText: l10n.passwordRequirements,
                    suffixIcon: IconButton(
                      icon: Icon(
                        _obscurePassword
                            ? Icons.visibility_off
                            : Icons.visibility,
                      ),
                      onPressed: () {
                        setState(() => _obscurePassword = !_obscurePassword);
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
                const SizedBox(height: 16),

                // Confirm password
                TextFormField(
                  controller: _confirmController,
                  decoration: InputDecoration(
                    labelText: l10n.confirmPassword,
                    prefixIcon: const Icon(Icons.lock_outline),
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
              ],
            ),
          ),
        ),
      ),
    );
  }
}
