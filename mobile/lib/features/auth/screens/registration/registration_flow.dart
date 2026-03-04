import 'dart:async';

import 'package:app_links/app_links.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:url_launcher/url_launcher.dart';

import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
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
/// 2. Fayda eKYC verification (OIDC browser flow)
/// 3. Role-specific details (trucker/buyer only, farmer skips)
/// 4. Personal info (phone, name, woreda, password)
/// → Dispatches AuthRegisterRequested with Fayda verification token
class RegistrationFlow extends StatefulWidget {
  const RegistrationFlow({super.key});

  @override
  State<RegistrationFlow> createState() => _RegistrationFlowState();
}

class _RegistrationFlowState extends State<RegistrationFlow>
    with WidgetsBindingObserver {
  final _pageController = PageController();
  final _personalInfoFormKey = GlobalKey<FormState>();
  final _faydaService = FaydaOidcService();
  int _currentStep = 0;

  // Collected data across steps
  UserRole? _selectedRole;
  String? _drivingLicensePath;
  String _buyerType = 'private';
  String _companyName = '';
  String _phone = '';
  String _name = '';
  String _woreda = '';
  String _password = '';
  String _confirmPassword = '';
  // Fayda OIDC state
  String? _faydaVerificationToken;
  String? _faydaVerifiedName;
  bool _faydaVerified = false;
  bool _faydaVerifying = false;
  String? _faydaState;
  String? _faydaError;
  StreamSubscription<Uri>? _deepLinkSub;

  // Whether the browser was launched and we're waiting for a callback
  bool _awaitingBrowserReturn = false;
  Timer? _browserReturnTimer;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _listenForDeepLinks();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _browserReturnTimer?.cancel();
    _pageController.dispose();
    _deepLinkSub?.cancel();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // When app resumes from browser and we're awaiting a Fayda callback,
    // give a short grace period for the deep link to arrive, then reset.
    if (state == AppLifecycleState.resumed &&
        _faydaVerifying &&
        _awaitingBrowserReturn) {
      _browserReturnTimer?.cancel();
      _browserReturnTimer = Timer(const Duration(seconds: 2), () {
        // If still verifying after 2s, the callback didn't come — reset.
        if (mounted && _faydaVerifying) {
          setState(() {
            _faydaVerifying = false;
            _awaitingBrowserReturn = false;
            _faydaError = AppLocalizations.of(context)!.faydaVerificationFailed;
          });
        }
      });
    }
  }

  void _listenForDeepLinks() {
    final appLinks = AppLinks();
    _deepLinkSub = appLinks.uriLinkStream.listen(_handleFaydaCallback);
  }

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
        return true; // Fayda verification is optional (post-registration)
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
    // Dismiss keyboard to prevent scroll jumps from viewport resizing
    FocusScope.of(context).unfocus();

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

  void _cancelIfLoading() {
    if (context.read<AuthBloc>().state is AuthLoading) {
      context.read<AuthBloc>().add(const AuthCancelRequested());
    }
  }

  void _previousStep() {
    _cancelIfLoading();
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
      AuthErrorCode.faydaVerificationFailed => l10n.faydaVerificationFailed,
      AuthErrorCode.faydaTokenExpired => l10n.faydaVerificationFailed,
      AuthErrorCode.faydaAlreadyLinked => l10n.faydaAlreadyLinked,
      AuthErrorCode.faydaServiceUnavailable => l10n.faydaUnavailable,
      _ => l10n.genericError,
    };
  }

  Future<void> _handleFaydaVerify() async {
    // Fail fast if Fayda OIDC credentials are not configured via --dart-define
    if (!FaydaOidcService.isConfigured) {
      setState(() {
        _faydaError =
            'Fayda is not configured. Provide FAYDA_AUTHORIZE_URL and '
            'FAYDA_CLIENT_ID via --dart-define or .env file.';
      });
      return;
    }

    setState(() {
      _faydaVerifying = true;
      _faydaError = null;
    });

    _faydaState = _faydaService.generateState();
    final url = _faydaService.buildAuthorizationUrl(_faydaState!);

    debugPrint('[Fayda] isConfigured: ${FaydaOidcService.isConfigured}');
    debugPrint('[Fayda] Launch URL: $url');

    try {
      final canLaunch = await canLaunchUrl(url);
      debugPrint('[Fayda] canLaunchUrl: $canLaunch');

      final launched =
          await launchUrl(url, mode: LaunchMode.externalApplication);
      debugPrint('[Fayda] launchUrl result: $launched');

      if (launched) {
        _awaitingBrowserReturn = true;
      } else {
        setState(() {
          _faydaVerifying = false;
          _faydaError = AppLocalizations.of(context)!.faydaUnavailable;
        });
      }
    } on Exception catch (e) {
      debugPrint('[Fayda] Launch error: $e');
      setState(() {
        _faydaVerifying = false;
        _faydaError = 'Failed to open browser: $e';
      });
    }
  }

  Future<void> _handleFaydaCallback(Uri uri) async {
    // Only handle our callback scheme
    if (uri.scheme != 'weredaconnect' || uri.host != 'fayda-callback') return;

    // Callback arrived — cancel the browser-return timer
    _browserReturnTimer?.cancel();
    _awaitingBrowserReturn = false;

    final callback = _faydaService.parseCallback(uri);

    // Validate state to prevent CSRF
    if (callback.state != _faydaState) {
      setState(() {
        _faydaVerifying = false;
        _faydaError = AppLocalizations.of(context)!.faydaVerificationFailed;
      });
      return;
    }

    if (!callback.isSuccess) {
      setState(() {
        _faydaVerifying = false;
        _faydaError = callback.errorDescription ??
            AppLocalizations.of(context)!.faydaVerificationFailed;
      });
      return;
    }

    // Exchange code for verification token via our server
    final result = await _faydaService.exchangeCode(
      authCode: callback.code!,
      state: _faydaState!,
    );

    if (result.isSuccess) {
      setState(() {
        _faydaVerificationToken = result.verificationToken;
        _faydaVerifiedName = result.verifiedName;
        _faydaVerified = true;
        _faydaVerifying = false;
        _faydaError = null;
        // Verified name is shown in a read-only card on the personal info screen
      });
    } else {
      setState(() {
        _faydaVerifying = false;
        _faydaError = _localizeAuthError(
          result.errorCode ?? 'FAYDA_001',
          result.errorMessage ?? 'Verification failed',
        );
      });
    }
  }

  void _handleRegister() {
    if (!_personalInfoFormKey.currentState!.validate()) return;

    // Clear any previous error snackbar
    ScaffoldMessenger.of(context).hideCurrentSnackBar();

    final languageCode = Localizations.localeOf(context).languageCode;

    context.read<AuthBloc>().add(
          AuthRegisterRequested(
            phoneNumber: _phone,
            password: _password,
            name: _name,
            role: _selectedRole!,
            woreda: _woreda,
            faydaVerificationToken: _faydaVerificationToken,
            preferredLanguage: languageCode,
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        if (state is AuthError) {
          final msg = _localizeAuthError(state.errorCode, state.errorMessage);
          ScaffoldMessenger.of(context)
            ..hideCurrentSnackBar()
            ..showSnackBar(
              SnackBar(
                content: Text(msg),
                backgroundColor: AppTheme.statusError,
                behavior: SnackBarBehavior.floating,
                duration: const Duration(seconds: 5),
              ),
            );
        }
        if (state is AuthAuthenticated) {
          // Registration succeeded — pop back to AuthGate
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
                  onPressed: () {
                    _cancelIfLoading();
                    Navigator.of(context).pop();
                  },
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
                  // Step 1: Fayda OIDC verification
                  FayidaIdScreen(
                    onFaydaVerifyRequested: _handleFaydaVerify,
                    onCancelVerification: _faydaVerifying
                        ? () {
                            _browserReturnTimer?.cancel();
                            setState(() {
                              _faydaVerifying = false;
                              _awaitingBrowserReturn = false;
                              _faydaError = null;
                            });
                          }
                        : null,
                    isVerifying: _faydaVerifying,
                    isVerified: _faydaVerified,
                    verifiedName: _faydaVerifiedName,
                    errorMessage: _faydaError,
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
                    faydaVerified: _faydaVerified,
                    faydaVerifiedName: _faydaVerifiedName,
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
