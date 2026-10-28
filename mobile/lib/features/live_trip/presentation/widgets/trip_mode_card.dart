import 'dart:async';

import 'package:flutter/material.dart';

import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/presentation/check_in_engine_of.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_mode_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/wc_card.dart';

/// The trucker home's way into trip mode (LT-09): shown while the phone has
/// a trip on the road (sharing, paused or scans only), with the sharing
/// state. Nothing for a farmer or a buyer (their engine has no trip), before
/// the first pickup scan, or once sharing ended.
class TripModeCard extends StatefulWidget {
  const TripModeCard({super.key, this.engine});

  /// Defaults to the app's engine.
  final CheckInEngine? engine;

  @override
  State<TripModeCard> createState() => _TripModeCardState();
}

class _TripModeCardState extends State<TripModeCard> {
  CheckInEngine? _engine;
  CheckInStatus _status = const CheckInStatus();
  StreamSubscription<CheckInStatus>? _sub;

  @override
  void initState() {
    super.initState();
    _engine = widget.engine ?? checkInEngineOf(context);
    _status = _engine?.status ?? const CheckInStatus();
    _sub = _engine?.statusStream.listen((status) {
      if (mounted) setState(() => _status = status);
    });
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final status = _status;
    final String state;
    switch (status.mode) {
      case SharingMode.live:
        state = l10n.tripModeSharingLive;
      case SharingMode.paused:
        state = l10n.tripModePausedUntil(clock.time(status.pausedUntil!));
      case SharingMode.declined:
        state = l10n.tripModeScansOnlyTitle;
      case SharingMode.off:
      case SharingMode.ended:
        return const SizedBox.shrink();
    }
    if (status.tripId == null) return const SizedBox.shrink();

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: WcCard(
        key: const Key('trip_mode_card'),
        onTap: () => Navigator.of(context).push<void>(
          MaterialPageRoute(builder: (_) => const TripModeScreen()),
        ),
        child: Row(
          children: [
            Icon(
              status.mode == SharingMode.live
                  ? Icons.share_location
                  : status.mode == SharingMode.paused
                      ? Icons.pause_circle_outline
                      : Icons.qr_code_2,
              color: AppTheme.primary,
              size: 28,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    l10n.tripModeCardTitle,
                    style: const TextStyle(
                      fontSize: 16,
                      fontWeight: FontWeight.w700,
                      color: AppTheme.textDark,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    state,
                    style: const TextStyle(
                      fontSize: 13,
                      color: AppTheme.textMuted,
                    ),
                  ),
                ],
              ),
            ),
            const Icon(Icons.chevron_right, color: AppTheme.textMuted),
          ],
        ),
      ),
    );
  }
}
