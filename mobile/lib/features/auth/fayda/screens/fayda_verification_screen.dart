import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:url_launcher/url_launcher.dart';

import 'package:wereda_connect/core/auth/fayda_callback_links.dart';
import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart'
    if (dart.library.html) 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/fayda/bloc/fayda_verification_bloc.dart';
import 'package:wereda_connect/features/auth/screens/registration/fayida_id_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Opens [url] in the system browser; true when it launched.
typedef FaydaUrlLauncher = Future<bool> Function(Uri url);

/// Verify an existing account with Fayda ("link later").
///
/// Reached from the Profile screen (when not verified) and from the KYC
/// payment prompt. On success the local user is marked verified so the
/// profile badge shows immediately, offline too. Pops with `true` when the
/// account ended up verified.
///
/// The server only accepts the Fayda identity that belongs to this account
/// (the Fayda ID already on file, else one whose phone matches the account);
/// any other identity fails with FAYDA_005/006/007 and nothing changes.
///
/// [forPayment]: opened by the KYC payment prompt. Says the payment continues
/// after verifying and closes itself with `true` on success, so the payment
/// is sent again without the user having to press back.
///
/// [requirePhoneProof]: the payment was blocked because the phone is not
/// verified. Linking the Fayda ID alone does not fix that, so success then
/// also needs the link to prove the phone; otherwise the screen stays open,
/// says so, and pops `false`.
///
/// Web runs the same flow: Fayda redirects to `web/fayda-callback.html`,
/// which relays the callback to this tab (see [faydaCallbackLinks]). When
/// the server refuses the web redirect (real provider) the start fails with
/// [AuthErrorCode.faydaUnavailableOnWeb] and that message is shown.
class FaydaVerificationScreen extends StatelessWidget {
  final FaydaOidcService? service;
  final Stream<Uri>? deepLinks;
  final FaydaUrlLauncher? launcher;
  final FaydaVerifiedRecorder? recordVerified;
  final bool forPayment;
  final bool requirePhoneProof;

  const FaydaVerificationScreen({
    this.service,
    this.deepLinks,
    this.launcher,
    this.recordVerified,
    this.forPayment = false,
    this.requirePhoneProof = false,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return BlocProvider(
      create: (context) => FaydaVerificationBloc(
        service: service ?? FaydaOidcService(),
        recordVerified: recordVerified ?? _defaultRecorder(context),
      ),
      child: _FaydaVerificationView(
        deepLinks: deepLinks,
        launcher: launcher,
        forPayment: forPayment,
        requirePhoneProof: requirePhoneProof,
      ),
    );
  }

  /// Marks the signed-in user verified in the local cache.
  static FaydaVerifiedRecorder _defaultRecorder(BuildContext context) {
    final authState = context.read<AuthBloc>().state;
    return (FaydaLinkResult result) async {
      if (authState is! AuthAuthenticated) return;
      final phone = authState.phoneNumber;
      if (phone == null || phone.isEmpty) return;
      await OfflineAuthCache().updateVerification(
        userId: authState.userId,
        phoneNumber: phone,
        faydaVerified: result.faydaVerified,
        faydaVerifiedAt: result.faydaVerifiedAt ?? DateTime.now().toUtc(),
      );
    };
  }
}

class _FaydaVerificationView extends StatefulWidget {
  final Stream<Uri>? deepLinks;
  final FaydaUrlLauncher? launcher;
  final bool forPayment;
  final bool requirePhoneProof;

  const _FaydaVerificationView({
    required this.forPayment,
    required this.requirePhoneProof,
    this.deepLinks,
    this.launcher,
  });

  @override
  State<_FaydaVerificationView> createState() => _FaydaVerificationViewState();
}

class _FaydaVerificationViewState extends State<_FaydaVerificationView>
    with WidgetsBindingObserver {
  StreamSubscription<Uri>? _deepLinkSub;
  Timer? _browserReturnTimer;
  Timer? _expiryTimer;

  /// How long to wait for the deep link when fayda-start sent no expiry.
  static const _defaultCallbackWindow = Duration(minutes: 10);
  bool _awaitingBrowserReturn = false;
  bool _inExternalFlow = false;

  /// While the Fayda browser is open the app lock must not fire on return.
  void _setExternalFlow({required bool active}) {
    if (active == _inExternalFlow) return;
    _inExternalFlow = active;
    if (active) {
      AppLockService.beginExternalFlow();
    } else {
      AppLockService.endExternalFlow();
    }
  }

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    final links = widget.deepLinks ?? faydaCallbackLinks();
    _deepLinkSub = links.listen((uri) {
      // Anything that is not an answer to an attempt leaves the wait (and its
      // expiry timer) untouched.
      if (!FaydaOidcService.isCallbackResponse(uri)) return;
      _browserReturnTimer?.cancel();
      _expiryTimer?.cancel();
      _awaitingBrowserReturn = false;
      _setExternalFlow(active: false);
      if (mounted) {
        context.read<FaydaVerificationBloc>().add(FaydaCallbackReceived(uri));
      }
    });
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _browserReturnTimer?.cancel();
    _expiryTimer?.cancel();
    _deepLinkSub?.cancel();
    _setExternalFlow(active: false);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // Back from the browser: give the deep link a moment, then say we are
    // still waiting. The wait itself only ends on the link, the state
    // expiry or the user's cancel, so a late link still completes. The
    // external flow stays open meanwhile: the user may go back to the
    // browser to finish.
    if (state == AppLifecycleState.resumed && _awaitingBrowserReturn) {
      _browserReturnTimer?.cancel();
      _browserReturnTimer = Timer(const Duration(seconds: 2), () {
        if (!mounted) return;
        context
            .read<FaydaVerificationBloc>()
            .add(const FaydaBrowserReturnedWithoutCallback());
      });
    }
  }

  /// Fail the wait once the Fayda state has expired.
  void _startExpiryTimer(DateTime? expiresAt) {
    _expiryTimer?.cancel();
    final window = expiresAt == null
        ? _defaultCallbackWindow
        : expiresAt.difference(DateTime.now().toUtc());
    _expiryTimer = Timer(window.isNegative ? Duration.zero : window, () {
      _browserReturnTimer?.cancel();
      _awaitingBrowserReturn = false;
      _setExternalFlow(active: false);
      if (!mounted) return;
      context.read<FaydaVerificationBloc>().add(const FaydaCallbackTimedOut());
    });
  }

  Future<void> _launch(Uri url) async {
    final bloc = context.read<FaydaVerificationBloc>();
    _setExternalFlow(active: true);
    bool launched;
    try {
      launched = await (widget.launcher ??
          (u) => launchUrl(u, mode: LaunchMode.externalApplication))(url);
    } on Object {
      launched = false;
    }
    if (launched) {
      _awaitingBrowserReturn = true;
    } else {
      _setExternalFlow(active: false);
      bloc.add(const FaydaBrowserLaunchFailed());
    }
  }

  bool _phoneStillUnproven(FaydaVerificationSucceeded state) =>
      widget.requirePhoneProof && !state.phoneVerified;

  String _errorText(AppLocalizations l10n, FaydaVerificationFailure failure) {
    return switch (failure.errorCode) {
      AuthErrorCode.faydaServiceUnavailable => l10n.faydaUnavailable,
      AuthErrorCode.faydaTokenExpired => l10n.faydaStateMismatch,
      _ => localizeAuthError(
          l10n,
          failure.errorCode,
          retryAfterSeconds: failure.retryAfterSeconds,
        ),
    };
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return BlocConsumer<FaydaVerificationBloc, FaydaVerificationState>(
      listener: (context, state) {
        if (state is FaydaAwaitingCallback) {
          // The "still waiting" update must not reopen the browser.
          if (!state.browserReturned) {
            _startExpiryTimer(state.expiresAt);
            _launch(state.authorizeUrl);
          }
        } else if (state is FaydaVerificationSucceeded) {
          // Linked, but the phone the payment needs is still unproven: the
          // builder explains, and the payment is not sent again.
          if (_phoneStillUnproven(state)) return;
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                widget.forPayment
                    ? l10n.faydaPaymentContinues
                    : l10n.faydaLinkSuccess,
              ),
            ),
          );
          // Payment mode: hand straight back so the payment is sent again.
          if (widget.forPayment) Navigator.of(context).pop(true);
        }
      },
      builder: (context, state) {
        final phoneStillUnproven =
            state is FaydaVerificationSucceeded && _phoneStillUnproven(state);
        final verified =
            state is FaydaVerificationSucceeded && !phoneStillUnproven;
        final verifying = state is FaydaVerificationStarting ||
            state is FaydaAwaitingCallback ||
            state is FaydaLinking;
        return PopScope<bool>(
          canPop: false,
          onPopInvokedWithResult: (didPop, _) {
            if (!didPop) Navigator.of(context).pop(verified);
          },
          child: Scaffold(
            backgroundColor: AppTheme.backgroundCream,
            appBar: AppBar(title: Text(l10n.faydaVerifyTitle)),
            body: FayidaIdScreen(
              promptMessage: widget.forPayment
                  ? l10n.faydaPaymentPrompt
                  : l10n.faydaLinkPrompt,
              onFaydaVerifyRequested: () => context
                  .read<FaydaVerificationBloc>()
                  .add(const FaydaVerificationStarted()),
              onCancelVerification: state is FaydaAwaitingCallback
                  ? () {
                      _browserReturnTimer?.cancel();
                      _expiryTimer?.cancel();
                      _awaitingBrowserReturn = false;
                      _setExternalFlow(active: false);
                      context
                          .read<FaydaVerificationBloc>()
                          .add(const FaydaVerificationCancelled());
                    }
                  : null,
              isVerifying: verifying,
              verifyingMessage:
                  state is FaydaAwaitingCallback && state.browserReturned
                      ? l10n.faydaStillWaiting
                      : null,
              isVerified: verified,
              verifiedName: state is FaydaVerificationSucceeded
                  ? state.verifiedName
                  : null,
              errorMessage: state is FaydaVerificationFailure
                  ? _errorText(l10n, state)
                  : phoneStillUnproven
                      ? l10n.kycPhoneNotProven
                      : null,
            ),
          ),
        );
      },
    );
  }
}
