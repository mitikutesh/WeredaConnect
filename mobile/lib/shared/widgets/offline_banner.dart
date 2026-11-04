import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Whether a write just saved waits on this phone for a network (E-5): the
/// device reports none, read from the app's connectivity monitor without a
/// request, so the answer never waits on the network. "Saved on your
/// phone" then, else "sent". Always false on web, where a write goes
/// straight to the API: it was sent, or it failed with its own error.
Future<bool> writeWaitsForNetwork(BuildContext context) async {
  if (kIsWeb) return false;
  final ConnectivityMonitor monitor;
  try {
    monitor = context.read<ConnectivityMonitor>();
  } on Object {
    return false;
  }
  try {
    return !await monitor.isConnected();
  } on Object {
    return false;
  }
}

/// Says how a write just saved went (E-5): [sent] when it went out, else
/// that it waits on this phone ([writeWaitsForNetwork]). With no [sent],
/// only a waiting write is said. The messenger is read first, so the
/// snackbar still shows when the screen that saved closes meanwhile.
Future<void> showSavedOrSent(BuildContext context, {String? sent}) async {
  final messenger = ScaffoldMessenger.of(context);
  final l10n = AppLocalizations.of(context)!;
  final waits = await writeWaitsForNetwork(context);
  final text = waits ? l10n.savedOnPhoneWillSend : sent;
  if (text != null) messenger.showSnackBar(SnackBar(content: Text(text)));
}

/// The one offline banner — offline is a status, never a surprise.
///
/// Self-contained (monitors connectivity itself) so any screen can drop
/// it into its column. Appears in the same slot (under the header) on
/// every tab so users learn a single place to look. Replaces the two
/// identical per-feature copies that used to live in crop_listing and
/// trip_listing.
class OfflineBanner extends StatefulWidget {
  const OfflineBanner({super.key});

  @override
  State<OfflineBanner> createState() => _OfflineBannerState();
}

class _OfflineBannerState extends State<OfflineBanner> {
  bool _isOffline = false;
  StreamSubscription<List<ConnectivityResult>>? _subscription;

  @override
  void initState() {
    super.initState();
    _checkConnectivity();
    _subscription = Connectivity().onConnectivityChanged.listen((results) {
      final offline = results.every(
        (r) => r == ConnectivityResult.none,
      );
      if (mounted && offline != _isOffline) {
        setState(() {
          _isOffline = offline;
        });
      }
    });
  }

  Future<void> _checkConnectivity() async {
    final results = await Connectivity().checkConnectivity();
    if (mounted) {
      setState(() {
        _isOffline = results.every(
          (r) => r == ConnectivityResult.none,
        );
      });
    }
  }

  @override
  void dispose() {
    _subscription?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (!_isOffline) return const SizedBox.shrink();

    final l10n = AppLocalizations.of(context)!;

    return MaterialBanner(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      content: Row(
        children: [
          const Icon(
            Icons.cloud_off,
            color: AppTheme.onStatusWarningContainer,
            size: 20,
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              l10n.offlineModeIndicator,
              style: const TextStyle(
                color: AppTheme.onStatusWarningContainer,
                fontWeight: FontWeight.w600,
              ),
            ),
          ),
        ],
      ),
      backgroundColor: AppTheme.statusWarningContainer,
      actions: const [SizedBox.shrink()],
    );
  }
}
