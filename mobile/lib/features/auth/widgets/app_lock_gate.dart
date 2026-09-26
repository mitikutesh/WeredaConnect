import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Covers the authenticated app with a lock screen until the device confirms
/// the user (biometric or phone PIN), when the app lock is on.
///
/// Mounted from `MaterialApp.builder`, above the Navigator, so the lock covers
/// every pushed route (order detail, payment, Fayda, QR) and not only the
/// home screen. It only does anything while [active] is true (the user is
/// signed in): the login and registration screens are never locked.
///
/// Locks:
/// - when it becomes active with [lockOnStart] set (the session was restored
///   by auto-login, not by typing the password). It starts covered and
///   uncovers itself if the lock turns out to be off, so the app never
///   flashes before the prompt, and
/// - when the app comes back after more than
///   [AppLockService.backgroundTimeout] in the background.
///
/// A short trip out of the app (the camera, the device PIN screen itself)
/// stays under the timeout and never locks. The Fayda browser flow never
/// locks however long it takes ([AppLockService.beginExternalFlow]). The child
/// stays mounted underneath, so no screen state is lost. No-op on web.
class AppLockGate extends StatefulWidget {
  final Widget child;
  final bool lockOnStart;

  /// Whether the lock applies right now. False while signed out.
  final bool active;
  final AppLockService? service;

  /// Clock override for tests.
  final DateTime Function()? now;

  const AppLockGate({
    required this.child,
    this.lockOnStart = false,
    this.active = true,
    this.service,
    this.now,
    super.key,
  });

  @override
  State<AppLockGate> createState() => _AppLockGateState();
}

class _AppLockGateState extends State<AppLockGate> with WidgetsBindingObserver {
  late final AppLockService _service = widget.service ?? AppLockService();
  bool _locked = false;
  bool _prompting = false;
  DateTime? _backgroundedAt;
  bool _leftForExternalFlow = false;

  DateTime _now() => (widget.now ?? DateTime.now)();

  @override
  void initState() {
    super.initState();
    if (kIsWeb) return;
    WidgetsBinding.instance.addObserver(this);
    if (widget.active && widget.lockOnStart) _lockOnStart();
  }

  @override
  void didUpdateWidget(AppLockGate oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (kIsWeb || oldWidget.active == widget.active) return;
    _backgroundedAt = null;
    _leftForExternalFlow = false;
    if (!widget.active) {
      // Signed out: never leave the lock over the login screen.
      _locked = false;
    } else if (widget.lockOnStart) {
      _lockOnStart();
    }
  }

  @override
  void dispose() {
    if (!kIsWeb) WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (!widget.active) return;
    switch (state) {
      case AppLifecycleState.paused:
      case AppLifecycleState.hidden:
        _backgroundedAt ??= _now();
        _leftForExternalFlow |= AppLockService.externalFlowActive;
      case AppLifecycleState.resumed:
        final since = _backgroundedAt;
        final external =
            _leftForExternalFlow || AppLockService.externalFlowActive;
        _backgroundedAt = null;
        _leftForExternalFlow = false;
        // Back from the Fayda browser flow the user started: never lock. The
        // flag is read at pause time too because the deep link can end the
        // flow before the resume event arrives.
        if (external) return;
        if (since != null &&
            !_locked &&
            AppLockService.shouldLockAfter(_now().difference(since))) {
          _lockIfEnabled();
        }
      case AppLifecycleState.inactive:
      case AppLifecycleState.detached:
        break;
    }
  }

  /// Cover the app straight away, then check whether the lock is on.
  void _lockOnStart() {
    _locked = true;
    _lockIfEnabled();
  }

  Future<void> _lockIfEnabled() async {
    final enabled = await _service.isEnabled();
    if (!mounted) return;
    if (!enabled || !widget.active) {
      if (_locked) setState(() => _locked = false);
      return;
    }
    setState(() => _locked = true);
    await _unlock();
  }

  Future<void> _unlock() async {
    if (_prompting || !mounted) return;
    _prompting = true;
    try {
      final reason =
          AppLocalizations.of(context)?.appLockReason ?? 'Unlock WeredaConnect';
      final ok = await _service.unlock(reason);
      if (ok && mounted) setState(() => _locked = false);
    } finally {
      _prompting = false;
    }
  }

  @override
  Widget build(BuildContext context) {
    if (kIsWeb) return widget.child;
    // The tree shape stays the same locked or not, so the Navigator below is
    // never remounted and no route state is lost.
    return Stack(
      children: [
        TickerMode(
          enabled: !_locked,
          child: ExcludeFocus(
            excluding: _locked,
            child: ExcludeSemantics(excluding: _locked, child: widget.child),
          ),
        ),
        if (_locked) Positioned.fill(child: _LockScreen(onUnlock: _unlock)),
      ],
    );
  }
}

class _LockScreen extends StatelessWidget {
  final VoidCallback onUnlock;

  const _LockScreen({required this.onUnlock});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Material(
      key: const Key('app_lock_screen'),
      color: AppTheme.primaryGreen,
      child: SafeArea(
        child: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(Icons.lock_outline, size: 72, color: Colors.white),
              const SizedBox(height: 20),
              Text(
                l10n?.appLockLockedTitle ?? 'WeredaConnect is locked',
                textAlign: TextAlign.center,
                style: const TextStyle(
                  fontSize: 20,
                  fontWeight: FontWeight.w700,
                  color: Colors.white,
                ),
              ),
              const SizedBox(height: 28),
              FilledButton.icon(
                key: const Key('app_lock_unlock_button'),
                style: FilledButton.styleFrom(
                  backgroundColor: AppTheme.accentGold,
                  minimumSize: const Size(200, 48),
                ),
                onPressed: onUnlock,
                icon: const Icon(Icons.fingerprint),
                label: Text(l10n?.appLockUnlock ?? 'Unlock'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
