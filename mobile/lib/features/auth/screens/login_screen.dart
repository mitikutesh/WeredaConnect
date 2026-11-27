import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/forgot_password_screen.dart';
import 'package:wereda_connect/features/auth/screens/otp_verification_screen.dart';
import 'package:wereda_connect/features/auth/screens/register_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/language_segmented_picker.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// The board "Welcome and login" (specs/design-canvas/WelcomeM): a pastel
/// header in the picked role's colour (brand tile, the Offline Ready pill,
/// the role's tagline, the welcome title and the role's sketch), the "Your
/// role" switch, then the language pills, the phone and password fields and
/// the orange "Login as ..." button; Register and the escrow line sit at
/// the foot.
///
/// The role switch only dresses this screen (colours, tagline, sketch, the
/// button's words). It never decides or restricts who signs in: nothing of
/// it is sent, the server tells the account's role, and after sign-in the
/// app wears that role. The switch opens on the role that last signed in
/// on this device ([SettingsProvider.lastSignInRole]), Farmer the first
/// time.
///
/// One composition at every width: on a wide window the phone column is
/// centred instead of a separate desktop layout.
///
/// Ethiopian context:
/// - Phone number formats: +251XXXXXXXXX, 00251XXXXXXXXX, 0XXXXXXXXX
/// - The language switch comes before the fields, so a person picks their
///   language before reading anything else
/// - Localized error messages (Amharic, Afaan Oromoo, English)
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _phoneController = TextEditingController();
  final _passwordController = TextEditingController();

  bool _obscurePassword = true;

  /// The look the screen wears; see the class comment. Never sent anywhere.
  late UserRole _role;
  late ThemeData _roleTheme;
  String? _errorMessage;

  /// Bumped on every [AuthError], see the error banner.
  int _errorSerial = 0;

  /// Per-field autovalidation, switched on by the first submit (see
  /// [_handleLogin]).
  AutovalidateMode _autovalidateMode = AutovalidateMode.disabled;

  /// Whether the "signed out because the account is suspended" text was
  /// already taken from the bloc's state when this screen opened.
  bool _suspensionChecked = false;

  @override
  void initState() {
    super.initState();
    _setRole(
      Provider.of<SettingsProvider?>(context, listen: false)?.lastSignInRole ??
          UserRole.farmer,
    );
  }

  void _setRole(UserRole role) {
    _role = role;
    _roleTheme = AppTheme.light(role: role);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    // The sign-out happens before this screen is built, so a listener
    // would miss it: read the state once, when the localizations exist.
    if (_suspensionChecked) return;
    _suspensionChecked = true;
    if (context.read<AuthBloc>().state is AuthSuspendedSignedOut) {
      _errorMessage = _localizeAuthError(AuthErrorCode.accountSuspended, '');
    }
  }

  @override
  void dispose() {
    _phoneController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  bool _otpScreenOpen = false;

  Future<void> _openOtpScreen(AuthOtpVerificationRequired state) async {
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

  String _localizeAuthError(
    String errorCode,
    String fallback, {
    int? retryAfterSeconds,
  }) {
    final l10n = AppLocalizations.of(context)!;
    return localizeAuthError(
      l10n,
      errorCode,
      retryAfterSeconds: retryAfterSeconds,
    );
  }

  void _handleLogin() {
    // Clear previous errors. No live validation while the user types the
    // first time; after the first submit every field the user edits is
    // re-checked, so a corrected field clears its error right away.
    setState(() {
      _errorMessage = null;
      _autovalidateMode = AutovalidateMode.onUserInteraction;
    });

    // Validate form
    if (!_formKey.currentState!.validate()) {
      return;
    }

    // Normalize phone to canonical E.164 before dispatching
    final normalizedPhone =
        normalizeEthiopianPhone(_phoneController.text.trim());
    if (normalizedPhone == null) {
      // Unreachable when the validator passed; guard instead of asserting
      // so a refactor can never dispatch an unnormalized phone.
      return;
    }
    context.read<AuthBloc>().add(
          AuthLoginRequested(
            phoneNumber: normalizedPhone,
            password: _passwordController.text,
          ),
        );
  }

  void _openForgotPassword() {
    final phone = _phoneController.text.trim();
    if (kIsWeb) {
      final query =
          phone.isNotEmpty ? '?phone=${Uri.encodeComponent(phone)}' : '';
      GoRouter.of(context).go('/forgot-password$query');
    } else {
      Navigator.of(context).push(
        MaterialPageRoute<void>(
          builder: (_) => ForgotPasswordScreen(initialPhoneNumber: phone),
        ),
      );
    }
  }

  void _openRegister() {
    Navigator.of(context).push(
      MaterialPageRoute<void>(builder: (_) => const RegisterScreen()),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        if (state is AuthAuthenticated) {
          // The account's real role, whatever the switch showed: the next
          // visit to this screen opens in it.
          unawaited(
            Provider.of<SettingsProvider?>(context, listen: false)
                ?.rememberSignInRole(state.role),
          );
          // Close the OTP screen this login opened, if any.
          if (_otpScreenOpen && Navigator.of(context).canPop()) {
            _otpScreenOpen = false;
            Navigator.of(context).pop();
          }
          // Pop self if pushed on top of AuthGate. AuthGate will then show
          // MainNavigation.
          if (Navigator.of(context).canPop()) {
            Navigator.of(context).pop();
          }
        } else if (state is AuthOtpVerificationRequired) {
          // Login for an account whose phone was never confirmed: the bloc
          // already sent a fresh code. Only the topmost screen opens the OTP
          // screen (registration handles its own).
          if (!_otpScreenOpen && (ModalRoute.of(context)?.isCurrent ?? true)) {
            _openOtpScreen(state);
          }
        } else if (state is AuthSuspendedSignedOut) {
          // Signed out while this screen was already open (a stale tab).
          setState(() {
            _errorSerial++;
            _errorMessage =
                _localizeAuthError(AuthErrorCode.accountSuspended, '');
          });
        } else if (state is AuthError) {
          setState(() {
            _errorSerial++;
            _errorMessage = _localizeAuthError(
              state.errorCode,
              state.errorMessage,
              retryAfterSeconds: state.retryAfterSeconds,
            );
          });
        }
      },
      // The whole screen wears the picked role: links, the header, the
      // focused field's edge.
      child: Theme(
        data: _roleTheme,
        child: Scaffold(
          backgroundColor: AppTheme.ground,
          body: FwContentWidth(
            maxWidth: 480,
            // The column's width, measured here: the page below sizes
            // itself by intrinsic height, where a LayoutBuilder cannot sit.
            child: LayoutBuilder(
              builder: (context, constraints) => _page(
                context,
                l10n,
                contentWidth: constraints.maxWidth - 40,
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _page(
    BuildContext context,
    AppLocalizations l10n, {
    required double contentWidth,
  }) {
    return JoinPage(
      padding: EdgeInsets.only(
        bottom: 24 + MediaQuery.paddingOf(context).bottom,
      ),
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _LoginHeader(role: _role, width: contentWidth),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 14, 20, 0),
            child: _RoleSwitch(
              width: contentWidth,
              selected: _role,
              onChanged: (role) => setState(() => _setRole(role)),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 18, 20, 0),
            child: _buildLoginForm(context, l10n),
          ),
        ],
      ),
      footer: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            OutlinedButton(
              onPressed: _openRegister,
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(54),
                side: const BorderSide(color: AppTheme.fieldBorder),
              ),
              child: Text(
                l10n.loginNoAccountRegister,
                textAlign: TextAlign.center,
              ),
            ),
            const SizedBox(height: 12),
            Text(
              '${l10n.loginEscrowFooter} · ${l10n.telebirrPayment}',
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 13,
                color: AppTheme.slate,
              ),
            ),
          ],
        ),
      ),
    );
  }

  /// The board's input: a bold label above a 52 tall paper field.
  InputDecoration _fieldDecoration() =>
      const InputDecoration(errorMaxLines: 10);

  String _loginLabel(AppLocalizations l10n) => switch (_role) {
        UserRole.farmer => l10n.loginAsFarmer,
        UserRole.trucker => l10n.loginAsTrucker,
        UserRole.buyer => l10n.loginAsBuyer,
      };

  Widget _buildLoginForm(BuildContext context, AppLocalizations l10n) {
    return Form(
      key: _formKey,
      child: Builder(
        builder: (context) {
          final isLoading = context.watch<AuthBloc>().state is AuthLoading;

          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Language first: the user may not read English yet.
              const LanguageSegmentedPicker(),
              const SizedBox(height: 18),

              LabeledField(
                label: l10n.phoneNumber,
                field: TextFormField(
                  controller: _phoneController,
                  autovalidateMode: _autovalidateMode,
                  decoration: _fieldDecoration(),
                  keyboardType: TextInputType.phone,
                  inputFormatters: [EthiopianPhoneInputFormatter()],
                  validator: (value) {
                    if (value == null || value.isEmpty) {
                      return l10n.phoneNumberRequired;
                    }
                    if (!isValidEthiopianPhone(value)) {
                      return l10n.invalidPhoneFormat;
                    }
                    return null;
                  },
                ),
              ),
              // The board's example line under the field: its own text,
              // outside the field's semantics and not a helper, so a
              // corrected field drops its error at once.
              const SizedBox(height: 6),
              Text(
                l10n.ethiopianPhoneExample,
                style: const TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w500,
                  color: AppTheme.slate,
                ),
              ),
              const SizedBox(height: 14),

              LabeledField(
                label: l10n.password,
                field: TextFormField(
                  controller: _passwordController,
                  autovalidateMode: _autovalidateMode,
                  decoration: _fieldDecoration().copyWith(
                    counterText: '',
                    suffixIcon: IconButton(
                      tooltip: _obscurePassword
                          ? l10n.showPassword
                          : l10n.hidePassword,
                      icon: Icon(
                        _obscurePassword
                            ? Icons.visibility_off
                            : Icons.visibility,
                      ),
                      onPressed: () {
                        setState(() {
                          _obscurePassword = !_obscurePassword;
                        });
                      },
                      iconSize: 22,
                    ),
                  ),
                  obscureText: _obscurePassword,
                  maxLength: 128,
                  validator: (value) {
                    if (value == null || value.isEmpty) {
                      return l10n.fieldRequired;
                    }
                    return null;
                  },
                ),
              ),

              // Forgot password: a link in the role's colour, at the end
              // of the line as on the board.
              Align(
                alignment: AlignmentDirectional.centerEnd,
                child: TextButton(
                  onPressed: _openForgotPassword,
                  style: TextButton.styleFrom(
                    minimumSize: const Size(48, 48),
                    padding: const EdgeInsets.symmetric(horizontal: 4),
                  ),
                  child: Text(
                    l10n.forgotPassword,
                    style: TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w700,
                      color: context.roleColors.primary,
                      decoration: TextDecoration.underline,
                      decorationColor: context.roleColors.primary,
                    ),
                  ),
                ),
              ),
              const SizedBox(height: 4),

              // Error banner if any
              if (_errorMessage != null)
                KeyedSubtree(
                  key: ValueKey<int>(_errorSerial),
                  child: Padding(
                    padding: const EdgeInsets.only(bottom: 16),
                    child: JoinErrorBanner(
                      key: const Key('login_error_banner'),
                      message: _errorMessage!,
                    ),
                  ),
                ),

              // The one orange action. Its words follow the role switch;
              // what it sends does not.
              ElevatedButton(
                key: const Key('login_submit'),
                onPressed: isLoading ? null : _handleLogin,
                child: isLoading
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          valueColor: AlwaysStoppedAnimation<Color>(
                            AppTheme.ink,
                          ),
                        ),
                      )
                    : Text(_loginLabel(l10n), textAlign: TextAlign.center),
              ),
            ],
          );
        },
      ),
    );
  }
}

/// The board's header: a block in the role's pastel with rounded bottom
/// corners. It runs under the status bar, so the colour reaches the top
/// edge of the screen.
class _LoginHeader extends StatelessWidget {
  final UserRole role;

  /// The width its text has (the column less the side padding).
  final double width;

  const _LoginHeader({required this.role, required this.width});

  static const TextStyle _titleStyle = TextStyle(
    fontSize: 32,
    fontWeight: FontWeight.w800,
    letterSpacing: -1.1,
    height: 1.05,
    color: AppTheme.ink,
  );

  static const TextStyle _taglineStyle = TextStyle(
    fontSize: 13,
    fontWeight: FontWeight.w500,
    color: AppTheme.inkSoft,
  );

  /// The sketch's drawn width per role, as on the board.
  static double _sketchWidth(UserRole role) => switch (role) {
        UserRole.farmer => 100,
        UserRole.trucker => 118,
        UserRole.buyer => 100,
      };

  /// The narrowest the sketch is drawn; with less room it is left out.
  static const double _sketchMinWidth = 64;

  /// How wide the sketch may be at the header's lower right, or null when
  /// the words leave it no room. The lines of the title beside it (and the
  /// tagline, when the sketch reaches up to it) must end before it starts:
  /// no text on top of a sketch.
  static double? _sketchWidthFor(
    BuildContext context, {
    required String tagline,
    required String title,
    required double width,
    required Size sketch,
  }) {
    if (!RoleSketch.sketchesShown(context)) return null;
    final base = DefaultTextStyle.of(context).style;
    final direction = Directionality.of(context);
    final scaler = MediaQuery.textScalerOf(context);
    final titlePainter = TextPainter(
      text: TextSpan(text: title, style: base.merge(_titleStyle)),
      textDirection: direction,
      textScaler: scaler,
    )..layout(maxWidth: width);
    var textWidth = 0.0;
    var top = 0.0;
    for (final line in titlePainter.computeLineMetrics()) {
      final bottom = top + line.height;
      // Lines inside the sketch's height, measured up from the bottom.
      if (bottom > titlePainter.height - sketch.height &&
          line.width > textWidth) {
        textWidth = line.width;
      }
      top = bottom;
    }
    final titleHeight = titlePainter.height;
    titlePainter.dispose();
    if (sketch.height > titleHeight + 12) {
      final taglinePainter = TextPainter(
        text: TextSpan(text: tagline, style: base.merge(_taglineStyle)),
        textDirection: direction,
        textScaler: scaler,
      )..layout(maxWidth: width);
      final last = taglinePainter.computeLineMetrics().last.width;
      taglinePainter.dispose();
      if (last > textWidth) textWidth = last;
    }
    final room = width - textWidth - 6;
    if (room < _sketchMinWidth) return null;
    return room < sketch.width ? room : sketch.width;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final colors = context.roleColors;
    final tagline = switch (role) {
      UserRole.farmer => l10n.loginTaglineFarmer,
      UserRole.trucker => l10n.loginTaglineTrucker,
      UserRole.buyer => l10n.loginTaglineBuyer,
    };
    final boardWidth = _sketchWidth(role);
    final boardSize =
        Size(boardWidth, boardWidth / RoleSketch.aspectRatio(role));

    return Container(
      padding: EdgeInsets.fromLTRB(
        20,
        24 + MediaQuery.paddingOf(context).top,
        20,
        24,
      ),
      decoration: BoxDecoration(
        color: colors.soft,
        borderRadius: const BorderRadius.vertical(bottom: Radius.circular(28)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // The brand and the Offline Ready pill; with large text the pill
          // takes the next line instead of pushing out.
          Wrap(
            alignment: WrapAlignment.spaceBetween,
            crossAxisAlignment: WrapCrossAlignment.center,
            spacing: 8,
            runSpacing: 8,
            children: [
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Container(
                    width: 40,
                    height: 40,
                    decoration: BoxDecoration(
                      color: Colors.white,
                      borderRadius: BorderRadius.circular(14),
                    ),
                    child: Icon(
                      Icons.agriculture,
                      color: colors.primary,
                      size: 25,
                    ),
                  ),
                  const SizedBox(width: 10),
                  Flexible(
                    child: Text(
                      l10n.appName,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        fontSize: 17,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.3,
                        color: AppTheme.ink,
                      ),
                    ),
                  ),
                ],
              ),
              JoinStatusPill(
                label: l10n.offlineReady,
                background: Colors.white,
                foreground: AppTheme.ink,
                iconColor: colors.primary,
              ),
            ],
          ),
          const SizedBox(height: 22),
          Builder(
            builder: (context) {
              final sketchWidth = _sketchWidthFor(
                context,
                tagline: tagline,
                title: l10n.welcomeMessage,
                width: width,
                sketch: boardSize,
              );
              final text = Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(tagline, style: _taglineStyle),
                  const SizedBox(height: 4),
                  Semantics(
                    header: true,
                    child: Text(l10n.welcomeMessage, style: _titleStyle),
                  ),
                ],
              );
              if (sketchWidth == null) return text;
              return Stack(
                clipBehavior: Clip.none,
                children: [
                  text,
                  // The board's sketch: the lower right of the header, a
                  // faint black line.
                  PositionedDirectional(
                    end: -2,
                    bottom: -2,
                    child: RoleSketch(
                      key: ValueKey('login_sketch_${role.name}'),
                      role: role,
                      width: sketchWidth,
                      color: AppTheme.ink.withValues(alpha: 0.3),
                    ),
                  ),
                ],
              );
            },
          ),
        ],
      ),
    );
  }
}

/// "Your role": Farmer, Trucker, Buyer as three pills, the picked one
/// black. Equal thirds while the labels fit; a long label (Afaan Oromoo,
/// large text) widens its pill and the row wraps instead of overflowing.
class _RoleSwitch extends StatelessWidget {
  final UserRole selected;
  final ValueChanged<UserRole> onChanged;

  /// The width the row has.
  final double width;

  const _RoleSwitch({
    required this.selected,
    required this.onChanged,
    required this.width,
  });

  static const double _gap = 8;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    String label(UserRole role) => switch (role) {
          UserRole.farmer => l10n.farmer,
          UserRole.trucker => l10n.trucker,
          UserRole.buyer => l10n.buyer,
        };
    return Semantics(
      container: true,
      label: l10n.loginYourRole,
      child: Builder(
        builder: (context) {
          final third = ((width - 2 * _gap) / 3).floorToDouble();
          return Wrap(
            spacing: _gap,
            children: [
              for (final role in UserRole.values)
                _RolePill(
                  key: ValueKey('login_role_${role.name}'),
                  role: role,
                  label: label(role),
                  selected: role == selected,
                  minWidth: third,
                  maxWidth: width,
                  onTap: () => onChanged(role),
                ),
            ],
          );
        },
      ),
    );
  }
}

class _RolePill extends StatelessWidget {
  final UserRole role;
  final String label;
  final bool selected;
  final double minWidth;
  final double maxWidth;
  final VoidCallback onTap;

  const _RolePill({
    required this.role,
    required this.label,
    required this.selected,
    required this.minWidth,
    required this.maxWidth,
    required this.onTap,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final foreground = selected ? Colors.white : AppTheme.inkSoft;
    final icon = switch (role) {
      UserRole.farmer => FwSprout(size: 15, color: foreground),
      UserRole.trucker =>
        Icon(Icons.local_shipping_outlined, size: 16, color: foreground),
      UserRole.buyer =>
        Icon(Icons.shopping_bag_outlined, size: 16, color: foreground),
    };
    return Semantics(
      button: true,
      selected: selected,
      inMutuallyExclusiveGroup: true,
      label: label,
      excludeSemantics: true,
      onTap: onTap,
      // A 44 tall pill in a 48 tall hit area.
      child: FwTapTarget(
        onTap: onTap,
        child: Material(
          color: selected ? AppTheme.ink : Colors.transparent,
          shape: StadiumBorder(
            side: BorderSide(
              color: selected ? AppTheme.ink : AppTheme.fieldBorder,
            ),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: ConstrainedBox(
              constraints: BoxConstraints(
                minHeight: 44,
                minWidth: minWidth,
                maxWidth: maxWidth,
              ),
              child: Padding(
                padding:
                    const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    icon,
                    const SizedBox(width: 6),
                    Flexible(
                      child: Text(
                        label,
                        textAlign: TextAlign.center,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight:
                              selected ? FontWeight.w700 : FontWeight.w500,
                          color: foreground,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
