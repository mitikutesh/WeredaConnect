import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/registration/fayida_id_screen.dart';
import 'package:wereda_connect/features/auth/screens/registration/personal_info_screen.dart';
import 'package:wereda_connect/features/auth/screens/registration/role_details_screen.dart';
import 'package:wereda_connect/features/auth/screens/registration/role_selection_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/language_selection_dialog.dart';

/// Multi-step registration flow.
///
/// Steps:
/// 1. Role selection
/// 2. Fayida ID verification
/// 3. Role-specific details (trucker/buyer only, farmer skips)
/// 4. Personal info (phone, name, woreda, password)
/// → Dispatches AuthRegisterRequested → OTP verification
class RegistrationFlow extends StatefulWidget {
  const RegistrationFlow({super.key});

  @override
  State<RegistrationFlow> createState() => _RegistrationFlowState();
}

class _RegistrationFlowState extends State<RegistrationFlow> {
  final _pageController = PageController();
  final _personalInfoFormKey = GlobalKey<FormState>();
  int _currentStep = 0;

  // Collected data across steps
  UserRole? _selectedRole;
  String _faydaId = '';
  String? _faydaIdFrontPath;
  String? _faydaIdBackPath;
  String? _drivingLicensePath;
  String _buyerType = 'private';
  String _companyName = '';
  String _phone = '';
  String _name = '';
  String _woreda = '';
  String _password = '';
  String _confirmPassword = '';
  String? _errorMessage;

  int get _totalSteps {
    if (_selectedRole == UserRole.farmer) return 3; // skip role details
    return 4;
  }

  int _adjustedPageIndex(int step) {
    // For farmers, skip step 2 (role details)
    if (_selectedRole == UserRole.farmer && step >= 2) {
      return step + 1; // jump over role details page
    }
    return step;
  }

  bool get _canContinue {
    switch (_currentStep) {
      case 0:
        return _selectedRole != null;
      case 1:
        return true; // Fayida ID is optional
      case 2:
        if (_selectedRole == UserRole.farmer) {
          // Farmer sees personal info as step 2
          return true;
        }
        return true; // Role details are optional
      case 3:
        return true;
      default:
        return true;
    }
  }

  void _nextStep() {
    if (_currentStep < _totalSteps - 1) {
      setState(() => _currentStep++);
      _pageController.animateToPage(
        _adjustedPageIndex(_currentStep),
        duration: const Duration(milliseconds: 300),
        curve: Curves.easeInOut,
      );
    } else {
      _handleRegister();
    }
  }

  void _previousStep() {
    if (_currentStep > 0) {
      setState(() => _currentStep--);
      _pageController.animateToPage(
        _adjustedPageIndex(_currentStep),
        duration: const Duration(milliseconds: 300),
        curve: Curves.easeInOut,
      );
    }
  }

  String _localizeAuthError(String errorCode, String fallback) {
    final l10n = AppLocalizations.of(context)!;
    return switch (errorCode) {
      AuthErrorCode.invalidPhoneFormat => l10n.invalidPhoneFormat,
      AuthErrorCode.invalidPassword => l10n.passwordRequirements,
      AuthErrorCode.phoneAlreadyRegistered => l10n.phoneAlreadyRegistered,
      AuthErrorCode.rateLimitExceeded => l10n.authRateLimitExceeded,
      AuthErrorCode.internalServerError => l10n.authServerError,
      AuthErrorCode.cognitoUnavailable => l10n.authServiceUnavailable,
      AuthErrorCode.networkError => l10n.authNetworkError,
      AuthErrorCode.apiNotConfigured => l10n.authApiNotConfigured,
      _ => l10n.genericError,
    };
  }

  void _handleRegister() {
    if (!_personalInfoFormKey.currentState!.validate()) return;

    setState(() => _errorMessage = null);

    final languageCode = Localizations.localeOf(context).languageCode;

    context.read<AuthBloc>().add(
          AuthRegisterRequested(
            phoneNumber: _phone,
            password: _password,
            name: _name,
            role: _selectedRole!,
            woreda: _woreda,
            preferredLanguage: languageCode,
          ),
        );
  }

  @override
  void dispose() {
    _pageController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        if (state is AuthError) {
          setState(() {
            _errorMessage =
                _localizeAuthError(state.errorCode, state.errorMessage);
          });
        }
        if (state is AuthOtpVerificationRequired) {
          // Return to AuthGate, which will show OTP screen
          while (Navigator.of(context).canPop()) {
            Navigator.of(context).pop();
          }
        }
      },
      child: Scaffold(
        backgroundColor: AppTheme.backgroundCream,
        appBar: AppBar(
          backgroundColor: Colors.transparent,
          elevation: 0,
          foregroundColor: AppTheme.textDark,
          leading: _currentStep > 0
              ? IconButton(
                  icon: const Icon(Icons.arrow_back),
                  onPressed: _previousStep,
                )
              : IconButton(
                  icon: const Icon(Icons.close),
                  onPressed: () => Navigator.of(context).pop(),
                ),
          title: Text(l10n.register),
          actions: [
            IconButton(
              icon: const Icon(Icons.language),
              onPressed: () => showDialog<void>(
                context: context,
                builder: (_) => const LanguageSelectionDialog(),
              ),
              tooltip: l10n.language,
            ),
          ],
        ),
        body: Column(
          children: [
            // Progress indicator
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 24),
              child: _StepProgress(
                current: _currentStep,
                total: _totalSteps,
              ),
            ),
            const SizedBox(height: 8),

            // Error message
            if (_errorMessage != null)
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 24),
                child: Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: AppTheme.statusError.withAlpha(25),
                    border:
                        Border.all(color: AppTheme.statusError.withAlpha(50)),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Row(
                    children: [
                      const Icon(
                        Icons.error_outline,
                        color: AppTheme.statusError,
                        size: 20,
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          _errorMessage!,
                          style: const TextStyle(
                            color: AppTheme.statusError,
                            fontSize: 13,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              ),

            // Step content
            Expanded(
              child: PageView(
                controller: _pageController,
                physics: const NeverScrollableScrollPhysics(),
                children: [
                  // Step 0: Role selection
                  RoleSelectionScreen(
                    selectedRole: _selectedRole,
                    onRoleSelected: (role) {
                      setState(() => _selectedRole = role);
                    },
                  ),
                  // Step 1: Fayida ID
                  FayidaIdScreen(
                    faydaId: _faydaId.isNotEmpty ? _faydaId : null,
                    frontImagePath: _faydaIdFrontPath,
                    backImagePath: _faydaIdBackPath,
                    onFaydaIdChanged: (v) => setState(() => _faydaId = v),
                    onFrontImageChanged: (v) =>
                        setState(() => _faydaIdFrontPath = v),
                    onBackImageChanged: (v) =>
                        setState(() => _faydaIdBackPath = v),
                  ),
                  // Step 2: Role-specific details
                  if (_selectedRole != null && _selectedRole != UserRole.farmer)
                    RoleDetailsScreen(
                      role: _selectedRole!,
                      drivingLicensePath: _drivingLicensePath,
                      buyerType: _buyerType,
                      companyName: _companyName,
                      onDrivingLicenseChanged: (v) =>
                          setState(() => _drivingLicensePath = v),
                      onBuyerTypeChanged: (v) => setState(() => _buyerType = v),
                      onCompanyNameChanged: (v) =>
                          setState(() => _companyName = v),
                    )
                  else
                    const SizedBox.shrink(),
                  // Step 3: Personal info
                  PersonalInfoScreen(
                    phone: _phone,
                    name: _name,
                    woreda: _woreda,
                    password: _password,
                    confirmPassword: _confirmPassword,
                    formKey: _personalInfoFormKey,
                    onPhoneChanged: (v) => _phone = v,
                    onNameChanged: (v) => _name = v,
                    onWeredaChanged: (v) => _woreda = v,
                    onPasswordChanged: (v) => _password = v,
                    onConfirmPasswordChanged: (v) => _confirmPassword = v,
                  ),
                ],
              ),
            ),

            // Bottom action bar
            Padding(
              padding: const EdgeInsets.fromLTRB(24, 8, 24, 24),
              child: Builder(
                builder: (context) {
                  final isLoading =
                      context.watch<AuthBloc>().state is AuthLoading;
                  final isLastStep = _currentStep == _totalSteps - 1;

                  return SizedBox(
                    width: double.infinity,
                    height: 50,
                    child: FilledButton(
                      onPressed:
                          (_canContinue && !isLoading) ? _nextStep : null,
                      child: isLoading
                          ? const SizedBox(
                              width: 20,
                              height: 20,
                              child: CircularProgressIndicator(
                                strokeWidth: 2,
                                color: Colors.white,
                              ),
                            )
                          : Text(
                              isLastStep ? l10n.signUp : l10n.continueButton,
                              style: const TextStyle(
                                fontSize: 16,
                                fontWeight: FontWeight.w600,
                              ),
                            ),
                    ),
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _StepProgress extends StatelessWidget {
  final int current;
  final int total;

  const _StepProgress({required this.current, required this.total});

  @override
  Widget build(BuildContext context) {
    return Row(
      children: List.generate(total, (index) {
        final isActive = index <= current;
        return Expanded(
          child: Container(
            height: 4,
            margin: EdgeInsets.only(right: index < total - 1 ? 4 : 0),
            decoration: BoxDecoration(
              color: isActive ? AppTheme.primaryGreen : AppTheme.borderLight,
              borderRadius: BorderRadius.circular(2),
            ),
          ),
        );
      }),
    );
  }
}
