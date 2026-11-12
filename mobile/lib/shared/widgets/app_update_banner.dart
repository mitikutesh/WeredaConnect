import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';

import 'package:wereda_connect/core/config/app_update.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Opens the store or reloads the page; true when it went.
typedef AppUpdateAction = Future<bool> Function();

/// The "update the app" banner above the signed-in shell's screens (FUP-75).
///
/// Shown while [SecurityEvents.appUpdateRequired] is set: the server refused
/// an order from this build as too old (`APP_UPDATE_REQUIRED`,
/// sync-protocol.md 1.41.0). It never blocks: every screen and the data kept
/// on the device stay usable, only orders wait for the update. The button
/// opens the store page on the phone and reloads the latest app on web.
/// Mounting it reads back a flag kept by an earlier run on the same build
/// ([SecurityEvents.restoreAppUpdateRequired]).
///
/// The [child] keeps its place in the tree whether the banner shows or not,
/// so a screen keeps its state when the banner appears.
class AppUpdateBanner extends StatefulWidget {
  const AppUpdateBanner({
    required this.child,
    this.events,
    this.openStore = openAppStorePage,
    this.reloadWeb = reloadLatestWebApp,
    this.isWeb = kIsWeb,
    super.key,
  });

  final Widget child;

  /// Defaults to the app's [SecurityEvents.instance].
  final SecurityEvents? events;

  /// The phone's button (tests inject one).
  final AppUpdateAction openStore;

  /// The web button (tests inject one).
  final AppUpdateAction reloadWeb;

  /// Which button to offer; tests set it.
  final bool isWeb;

  @override
  State<AppUpdateBanner> createState() => _AppUpdateBannerState();
}

class _AppUpdateBannerState extends State<AppUpdateBanner> {
  StreamSubscription<void>? _sub;
  bool _required = false;

  SecurityEvents get _events => widget.events ?? SecurityEvents.instance;

  @override
  void initState() {
    super.initState();
    _required = _events.appUpdateRequired;
    _sub = _events.onAppUpdateRequired.listen((_) {
      if (mounted && !_required) setState(() => _required = true);
    });
    unawaited(_events.restoreAppUpdateRequired());
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  Future<void> _onPressed() async {
    final messenger = ScaffoldMessenger.maybeOf(context);
    final l10n = AppLocalizations.of(context)!;
    if (widget.isWeb) {
      await widget.reloadWeb();
      return;
    }
    final opened = await widget.openStore();
    if (!opened) {
      messenger?.showSnackBar(
        SnackBar(content: Text(l10n.appUpdateStoreNotOpened)),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final shown = _required;
    return Column(
      children: [
        if (shown)
          SafeArea(
            bottom: false,
            child: _UpdateNotice(isWeb: widget.isWeb, onPressed: _onPressed),
          ),
        Expanded(
          // The banner took the top inset; the screen under it must not pad
          // for the status bar again.
          child: MediaQuery.removePadding(
            context: context,
            removeTop: shown,
            child: widget.child,
          ),
        ),
      ],
    );
  }
}

class _UpdateNotice extends StatelessWidget {
  const _UpdateNotice({required this.isWeb, required this.onPressed});

  final bool isWeb;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Semantics(
      container: true,
      liveRegion: true,
      child: MaterialBanner(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        leading: const Icon(
          Icons.system_update,
          color: AppTheme.onStatusInfoContainer,
          size: 20,
        ),
        content: Text(
          l10n.appUpdateBannerMessage,
          style: const TextStyle(
            color: AppTheme.onStatusInfoContainer,
            fontWeight: FontWeight.w600,
          ),
        ),
        backgroundColor: AppTheme.statusInfoContainer,
        actions: [
          TextButton(
            onPressed: onPressed,
            style: TextButton.styleFrom(
              foregroundColor: AppTheme.onStatusInfoContainer,
            ),
            child: Text(
              isWeb ? l10n.appUpdateBannerReload : l10n.appUpdateBannerUpdate,
            ),
          ),
        ],
      ),
    );
  }
}
