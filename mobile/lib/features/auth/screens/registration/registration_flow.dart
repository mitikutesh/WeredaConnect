import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:url_launcher/url_launcher.dart';

import 'package:wereda_connect/core/auth/fayda_callback_links.dart';
import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/fayda/screens/fayda_verification_screen.dart'
    show FaydaUrlLauncher;
import 'package:wereda_connect/features/auth/screens/otp_verification_screen.dart';
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
/// 2. Fayda eKYC verification (OIDC browser flow). Required: there is no
///    Continue on this step until Fayda has verified the user.
/// 3. Role-specific details (trucker/buyer only, farmer skips)
/// 4. Personal info (phone, name, woreda, password)
/// → Dispatches AuthRegisterRequested with Fayda verification token
/// → When Fayda's phone differs from the entered one the server asks for an SMS OTP
///   (AuthOtpVerificationRequired) and the OTP screen is pushed on top.
class RegistrationFlow extends StatefulWidget {
  const RegistrationFlow({
    super.key,
    this.faydaService,
    this.faydaLinks,
    this.launcher,
  });

  /// Test seams: the Fayda client, the callback stream and the URL launcher.
  final FaydaOidcService? faydaService;
  final Stream<Uri>? faydaLinks;
  final FaydaUrlLauncher? launcher;

  @override
  State<RegistrationFlow> createState() => _RegistrationFlowState();
}

class _RegistrationFlowState extends State<RegistrationFlow>
    with WidgetsBindingObserver {
  final _pageController = PageController();
  final _personalInfoFormKey = GlobalKey<FormState>();
  late final _faydaService = widget.faydaService ?? FaydaOidcService();
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
  // Text under the spinner while an automatic retry runs; null otherwise.
  String? _faydaProgressMessage;
  StreamSubscription<Uri>? _deepLinkSub;

  // Whether the browser was launched and we're waiting for a callback
  bool _awaitingBrowserReturn = false;
  Timer? _browserReturnTimer;

  // Whether the OTP screen is currently pushed on top of this flow.
  bool _otpScreenOpen = false;

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
    _deepLinkSub = (widget.faydaLinks ?? faydaCallbackLinks())
        .listen(_handleFaydaCallback);
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
        return _faydaVerified; // Registration goes through Fayda only
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

  String _localizeAuthError(String errorCode, {int? retryAfterSeconds}) {
    final l10n = AppLocalizations.of(context)!;
    // FAYDA_003 here means the automatic retries were used up as well.
    if (errorCode == AuthErrorCode.faydaServiceUnavailable) {
      return l10n.faydaRegistrationUnavailable;
    }
    return localizeAuthError(
      l10n,
      errorCode,
      retryAfterSeconds: retryAfterSeconds,
    );
  }

  /// Shows "still trying (n/total)" under the spinner during a retry.
  void _showFaydaRetry(int attempt, int total) {
    if (!mounted) return;
    setState(() {
      _faydaProgressMessage =
          AppLocalizations.of(context)!.faydaRetrying(attempt, total);
    });
  }

  /// A register refusal that means the Fayda proof is gone (missing, reused
  /// or expired): drop it and send the user back to the Fayda step.
  void _returnToFaydaStep(String message) {
    _faydaVerificationToken = null;
    _faydaVerifiedName = null;
    _faydaVerified = false;
    _faydaError = message;
    _currentStep = 1;
    _pageController.jumpToPage(_adjustedPageIndex(1));
  }

  Future<void> _handleFaydaVerify() async {
    setState(() {
      _faydaVerifying = true;
      _faydaError = null;
      _faydaProgressMessage = null;
    });

    // The server builds the authorize URL (client id, nonce, PKCE) and hands
    // back the state we must see again on the callback. Transient failures
    // are retried inside the service before an error reaches the user.
    final start = await _faydaService.startRegistration(
      phoneNumber: _phone.isEmpty ? null : _phone,
      onRetry: _showFaydaRetry,
    );
    if (!mounted) return;
    setState(() => _faydaProgressMessage = null);
    if (!start.isSuccess) {
      setState(() {
        _faydaVerifying = false;
        _faydaError = _localizeAuthError(
          start.errorCode ?? AuthErrorCode.faydaServiceUnavailable,
          retryAfterSeconds: start.retryAfterSeconds,
        );
      });
      return;
    }

    _faydaState = start.state;
    final url = start.authorizeUrl!;

    try {
      final launched = await (widget.launcher ??
          (u) => launchUrl(u, mode: LaunchMode.externalApplication))(url);

      if (!mounted) return;
      if (launched) {
        _awaitingBrowserReturn = true;
      } else {
        setState(() {
          _faydaVerifying = false;
          _faydaError = AppLocalizations.of(context)!.faydaCouldNotOpen;
        });
      }
    } on Exception catch (e) {
      debugPrint('[Fayda] Launch error: $e');
      if (!mounted) return;
      setState(() {
        _faydaVerifying = false;
        _faydaError = AppLocalizations.of(context)!.faydaCouldNotOpen;
      });
    }
  }

  Future<void> _handleFaydaCallback(Uri uri) async {
    // Only handle our callback scheme
    // Only a callback that answers an attempt (it carries a state); a
    // stateless one (the web callback tab reloaded) must not end the wait.
    if (!FaydaOidcService.isCallbackResponse(uri)) return;

    // Callback arrived — cancel the browser-return timer
    _browserReturnTimer?.cancel();
    _awaitingBrowserReturn = false;

    final callback = _faydaService.parseCallback(uri);

    // Validate state to prevent CSRF: it must match what fayda-start issued.
    if (_faydaState == null || callback.state != _faydaState) {
      setState(() {
        _faydaVerifying = false;
        _faydaError = AppLocalizations.of(context)!.faydaStateMismatch;
      });
      return;
    }

    if (!callback.isSuccess) {
      setState(() {
        _faydaVerifying = false;
        _faydaError = AppLocalizations.of(context)!.faydaVerificationFailed;
      });
      return;
    }

    // Exchange code for verification token via our server
    final result = await _faydaService.exchangeCode(
      authCode: callback.code!,
      state: _faydaState!,
      onRetry: _showFaydaRetry,
    );

    if (!mounted) return;
    setState(() => _faydaProgressMessage = null);

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
        // Not _localizeAuthError: a FAYDA_003 here was not retried (the code
        // is single use), so the "after several tries" wording does not fit.
        _faydaError = localizeAuthError(
          AppLocalizations.of(context)!,
          result.errorCode ?? AuthErrorCode.faydaVerificationFailed,
        );
      });
    }
  }

  Future<void> _openOtpScreen(AuthOtpVerificationRequired state) async {
    if (_otpScreenOpen) return;
    _otpScreenOpen = true;
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => OtpVerificationScreen(
          phoneNumber: state.phoneNumber,
          expiresAt: state.expiresAt,
        ),
      ),
    );
    _otpScreenOpen = false;
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
            // F-32: role-specific fields, previously collected but dropped.
            // Trucker driving-license is local-only (kept on device, not sent);
            // buyer type/company name are persisted server-side.
            drivingLicensePath:
                _selectedRole == UserRole.trucker ? _drivingLicensePath : null,
            buyerType: _selectedRole == UserRole.buyer ? _buyerType : null,
            companyName:
                _selectedRole == UserRole.buyer && _buyerType == 'company'
                    ? _companyName
                    : null,
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        if (state is AuthOtpVerificationRequired) {
          _openOtpScreen(state);
          return;
        }
        // The OTP screen shows its own errors while it is on top.
        if (state is AuthError && !_otpScreenOpen) {
          final msg = _localizeAuthError(
            state.errorCode,
            retryAfterSeconds: state.retryAfterSeconds,
          );
          if (state.errorCode == AuthErrorCode.invalidFaydaToken ||
              state.errorCode == AuthErrorCode.faydaVerificationFailed ||
              state.errorCode == AuthErrorCode.faydaTokenExpired) {
            final message =
                AppLocalizations.of(context)!.faydaRequiredToRegister;
            setState(() => _returnToFaydaStep(message));
            return;
          }
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
        body: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 480),
            child: Column(
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
                        verifyingMessage: _faydaProgressMessage,
                        isVerified: _faydaVerified,
                        verifiedName: _faydaVerifiedName,
                        errorMessage: _faydaError,
                      ),
                      // Step 2: Role-specific details
                      if (_selectedRole != null &&
                          _selectedRole != UserRole.farmer)
                        RoleDetailsScreen(
                          role: _selectedRole!,
                          drivingLicensePath: _drivingLicensePath,
                          buyerType: _buyerType,
                          companyName: _companyName,
                          onDrivingLicenseChanged: (v) =>
                              setState(() => _drivingLicensePath = v),
                          onBuyerTypeChanged: (v) =>
                              setState(() => _buyerType = v),
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

                // Bottom action bar. Hidden on the Fayda step until Fayda has
                // verified the user: the card's own button is the only way on.
                if (_currentStep != 1 || _faydaVerified)
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
                                    isLastStep
                                        ? l10n.signUp
                                        : l10n.continueButton,
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
