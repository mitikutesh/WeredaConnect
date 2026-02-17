import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Banner shown when the device is offline.
///
/// Uses connectivity_plus to monitor connectivity changes.
/// Shows an amber banner with offline indicator message.
/// Reuses the same pattern as crop_listing OfflineBanner.
class TripOfflineBanner extends StatefulWidget {
  const TripOfflineBanner({super.key});

  @override
  State<TripOfflineBanner> createState() => _TripOfflineBannerState();
}

class _TripOfflineBannerState extends State<TripOfflineBanner> {
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
          const Icon(Icons.cloud_off, color: Colors.white, size: 20),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              l10n.offlineModeIndicator,
              style: const TextStyle(color: Colors.white),
            ),
          ),
        ],
      ),
      backgroundColor: Colors.amber.shade800,
      actions: const [SizedBox.shrink()],
    );
  }
}
