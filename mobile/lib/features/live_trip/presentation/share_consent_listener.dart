import 'dart:async';

import 'package:flutter/material.dart';

import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/presentation/check_in_engine_of.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_mode_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/share_consent_sheet.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Shows the consent sheet when the check-in engine asks (LT-09): the first
/// pickup scan of a trip the trucker has not answered for, while the Trip
/// location setting is "Ask on each trip". "Share trip location" starts
/// sharing ([CheckInEngine.start]), "Not now, scans only" records `declined`
/// ([CheckInEngine.decline]); either way the trip is not asked again.
///
/// Sits around the signed-in app on mobile and web. Only a signed-in
/// trucker's engine asks, so a farmer or a buyer never sees the sheet.
class ShareConsentListener extends StatefulWidget {
  const ShareConsentListener({
    required this.child,
    this.engine,
    this.settle = defaultSettle,
    super.key,
  });

  final Widget child;

  /// Defaults to the app's engine.
  final CheckInEngine? engine;

  /// How long to wait before the sheet opens. A confirmed pickup closes the
  /// scan screens (their listeners pop the top route), so a sheet opened at
  /// once would be closed with them.
  final Duration settle;

  static const defaultSettle = Duration(milliseconds: 600);

  @override
  State<ShareConsentListener> createState() => _ShareConsentListenerState();
}

class _ShareConsentListenerState extends State<ShareConsentListener> {
  CheckInEngine? _engine;
  StreamSubscription<String>? _sub;
  final List<String> _waiting = [];
  bool _asking = false;

  @override
  void initState() {
    super.initState();
    _engine = widget.engine ?? checkInEngineOf(context);
    _sub = _engine?.consentRequests.listen(_onRequest);
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  void _onRequest(String tripId) {
    if (!_waiting.contains(tripId)) _waiting.add(tripId);
    unawaited(_askAll());
  }

  Future<void> _askAll() async {
    if (_asking) return;
    _asking = true;
    try {
      while (_waiting.isNotEmpty) {
        final tripId = _waiting.removeAt(0);
        await Future<void>.delayed(widget.settle);
        if (!mounted) return;
        final engine = _engine;
        // Answered meanwhile (another pickup of the trip), or signed out.
        if (engine == null ||
            engine.userId == null ||
            engine.sharingFor(tripId) != null) {
          continue;
        }
        final choice = await showShareConsentSheet(context);
        if (!mounted || choice == null) continue;
        await _apply(engine, tripId, choice);
      }
    } finally {
      _asking = false;
    }
  }

  Future<void> _apply(
    CheckInEngine engine,
    String tripId,
    ShareConsentChoice choice,
  ) async {
    final l10n = AppLocalizations.of(context)!;
    final messenger = ScaffoldMessenger.maybeOf(context);
    final navigator = Navigator.of(context, rootNavigator: true);
    switch (choice) {
      case ShareConsentChoice.share:
        final result = await engine.start(tripId);
        messenger?.showSnackBar(
          SnackBar(
            content: Text(
              switch (result) {
                CheckInResult.recorded => l10n.liveTripSharingStarted,
                CheckInResult.noFix => l10n.tripModeNoFix,
                CheckInResult.notSharing => l10n.tripModeNotSharing,
              },
            ),
            action: result == CheckInResult.notSharing
                ? null
                : SnackBarAction(
                    label: l10n.tripModeOpen,
                    onPressed: () => navigator.push<void>(
                      MaterialPageRoute(
                        builder: (_) => const TripModeScreen(),
                      ),
                    ),
                  ),
          ),
        );
      case ShareConsentChoice.scansOnly:
        await engine.decline(tripId);
        messenger?.showSnackBar(
          SnackBar(content: Text(l10n.liveTripScansOnlyChosen)),
        );
    }
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
