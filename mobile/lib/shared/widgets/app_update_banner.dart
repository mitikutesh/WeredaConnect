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
/// sync-protocol.md 1.41.0). It goes when the server accepts an order from
/// this build again. It never blocks: every screen and the data kept on the
/// device stay usable, only orders wait for the update. Mounting it reads
/// back a flag kept by an earlier run on the same build
/// ([SecurityEvents.restoreAppUpdateRequired]).
///
/// On the phone the button opens the store page, and a second line says
/// where else to get the app (the Play listing is not live yet, FUP-242, so
/// the page may not show it whatever the launch returns). On web the button
/// reloads the latest app ([reloadLatestWebApp]); when that cannot happen
/// (offline) nothing changes and a snackbar says so.
///
/// The [child] keeps its place in the tree whether the banner shows or not,
/// so a screen keeps its state when the banner comes or goes.
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
  StreamSubscription<bool>? _sub;
  bool _required = false;

  /// True while the web reload fetches the newer app.
  bool _busy = false;

  SecurityEvents get _events => widget.events ?? SecurityEvents.instance;

  @override
  void initState() {
    super.initState();
    _required = _events.appUpdateRequired;
    _sub = _events.onAppUpdateRequired.listen((required) {
      if (mounted && required != _required) {
        setState(() => _required = required);
      }
    });
    unawaited(_events.restoreAppUpdateRequired());
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  Future<void> _onPressed() async {
    if (!widget.isWeb) {
      // The second line covers a store page that does not show the app.
      await widget.openStore();
      return;
    }
    final messenger = ScaffoldMessenger.maybeOf(context);
    final l10n = AppLocalizations.of(context)!;
    setState(() => _busy = true);
    final reloaded = await widget.reloadWeb();
    if (!mounted) return;
    setState(() => _busy = false);
    if (!reloaded) {
      messenger?.showSnackBar(
        SnackBar(content: Text(l10n.appUpdateReloadFailed)),
      );
    }
  }

  /// The most of the height the banner may take: long Amharic or Afan Oromo
  /// text on a small phone, or a large text size, scrolls inside it instead
  /// of pushing the screen under it out of view.
  static const _maxHeightShare = 0.4;

  @override
  Widget build(BuildContext context) {
    final shown = _required;
    return LayoutBuilder(
      builder: (context, constraints) => Column(
        children: [
          if (shown)
            // Outside the scroll view, so scrolled text never slides under
            // the status bar.
            SafeArea(
              bottom: false,
              child: ConstrainedBox(
                constraints: BoxConstraints(
                  maxHeight: constraints.maxHeight * _maxHeightShare,
                ),
                child: SingleChildScrollView(
                  // The screen under it keeps the primary scroll controller.
                  primary: false,
                  child: _UpdateNotice(
                    isWeb: widget.isWeb,
                    busy: _busy,
                    onPressed: _onPressed,
                  ),
                ),
              ),
            ),
          Expanded(
            // The banner took the top inset; the screen under it must not
            // pad for the status bar again.
            child: MediaQuery.removePadding(
              context: context,
              removeTop: shown,
              child: widget.child,
            ),
          ),
        ],
      ),
    );
  }
}

class _UpdateNotice extends StatelessWidget {
  const _UpdateNotice({
    required this.isWeb,
    required this.busy,
    required this.onPressed,
  });

  final bool isWeb;
  final bool busy;
  final VoidCallback onPressed;

  /// Narrower than this, the button goes under the text: beside it, a long
  /// label ("Fuula irra deebi'aa fe'aa") at a large text size leaves the
  /// text no room on a phone.
  static const _actionsBelowWidth = 600.0;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Semantics(
      container: true,
      liveRegion: true,
      child: LayoutBuilder(
        builder: (context, constraints) => _banner(
          l10n,
          actionsBelow: constraints.maxWidth < _actionsBelowWidth,
        ),
      ),
    );
  }

  Widget _banner(AppLocalizations l10n, {required bool actionsBelow}) {
    const ink = AppTheme.onStatusInfoContainer;
    final label =
        Text(isWeb ? l10n.appUpdateBannerReload : l10n.appUpdateBannerUpdate);
    return MaterialBanner(
      forceActionsBelow: actionsBelow,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      leading: const Icon(Icons.system_update, color: ink, size: 20),
      content: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            l10n.appUpdateBannerMessage,
            style: const TextStyle(color: ink, fontWeight: FontWeight.w600),
          ),
          if (!isWeb) ...[
            const SizedBox(height: 4),
            Text(
              l10n.appUpdateBannerStoreHint,
              style: const TextStyle(color: ink),
            ),
          ],
        ],
      ),
      backgroundColor: AppTheme.statusInfoContainer,
      actions: [
        TextButton(
          onPressed: busy ? null : onPressed,
          style: TextButton.styleFrom(foregroundColor: ink),
          child: busy
              ? Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: ink,
                      ),
                    ),
                    const SizedBox(width: 8),
                    // A long label wraps beside the spinner.
                    Flexible(child: label),
                  ],
                )
              : label,
        ),
      ],
    );
  }
}
