import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

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
