import 'package:flutter/material.dart';

import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';

/// How long the trucker pauses sharing (live-trip.md section 4).
enum PauseChoice {
  minutes15,
  minutes30,
  hour1,

  /// Until 06:00 in Ethiopia, offered only while that is at most
  /// [CheckInCadence.maxPause] away (the engine refuses a longer pause).
  overnight;

  /// When sharing comes back for this choice made at [now], or null when the
  /// choice is not offered at [now].
  DateTime? untilFrom(DateTime now) => switch (this) {
        PauseChoice.minutes15 => now.add(const Duration(minutes: 15)),
        PauseChoice.minutes30 => now.add(const Duration(minutes: 30)),
        PauseChoice.hour1 => now.add(const Duration(hours: 1)),
        PauseChoice.overnight =>
          overnightOffered(now) ? nextMorning(now) : null,
      };

  /// The next 06:00 in Ethiopia (UTC+3) after [now].
  static DateTime nextMorning(DateTime now) {
    var at = startOfDayInEthiopia(now).add(const Duration(hours: 6));
    if (!at.isAfter(now)) at = at.add(const Duration(days: 1));
    return at;
  }

  /// Whether "overnight, until 06:00" fits in the longest pause the engine
  /// sends: from 12:00 in the day until 06:00.
  static bool overnightOffered(DateTime now) =>
      nextMorning(now).difference(now) <= CheckInCadence.maxPause;

  /// The choices offered at [now].
  static List<PauseChoice> offeredAt(DateTime now) => [
        for (final c in values)
          if (c.untilFrom(now) != null) c,
      ];
}

/// Shows the pause sheet: 15 minutes, 30 minutes, 1 hour and, while it fits,
/// overnight until 06:00, each with the time sharing comes back on the
/// viewer's clock. Returns the choice, or null for "Keep sharing".
Future<PauseChoice?> showPauseSheet(
  BuildContext context, {
  required DateTime now,
}) =>
    showModalBottomSheet<PauseChoice>(
      context: context,
      isScrollControlled: true,
      backgroundColor: AppTheme.surfaceWhite,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
      ),
      builder: (_) => PauseSheet(now: now),
    );

/// The pause sheet's content (LT-09).
class PauseSheet extends StatefulWidget {
  const PauseSheet({required this.now, super.key});

  /// When the sheet opened: the times shown are counted from it.
  final DateTime now;

  @override
  State<PauseSheet> createState() => _PauseSheetState();
}

class _PauseSheetState extends State<PauseSheet> {
  PauseChoice _choice = PauseChoice.minutes30;

  String _label(PauseChoice c, AppLocalizations l10n, ClockFormat clock) =>
      switch (c) {
        PauseChoice.minutes15 => l10n.pauseSheet15,
        PauseChoice.minutes30 => l10n.pauseSheet30,
        PauseChoice.hour1 => l10n.pauseSheet60,
        PauseChoice.overnight => l10n.pauseSheetOvernight(
            clock.time(PauseChoice.nextMorning(widget.now)),
          ),
      };

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final clock = clockFormatOf(context);
    final offered = PauseChoice.offeredAt(widget.now);

    return SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(20, 24, 20, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              l10n.pauseSheetTitle,
              style: theme.textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.w800,
                color: AppTheme.textDark,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              l10n.pauseSheetText,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: AppTheme.textMuted,
              ),
            ),
            const SizedBox(height: 16),
            Text(
              l10n.pauseSheetFor,
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            RadioGroup<PauseChoice>(
              groupValue: _choice,
              onChanged: (value) {
                if (value != null) setState(() => _choice = value);
              },
              child: Column(
                children: [
                  for (final c in offered)
                    RadioListTile<PauseChoice>(
                      key: Key('pause_${c.name}'),
                      value: c,
                      contentPadding: EdgeInsets.zero,
                      title: Text(_label(c, l10n, clock)),
                      subtitle: c == PauseChoice.overnight
                          ? null
                          : Text(
                              l10n.pauseSheetBackAt(
                                clock.time(c.untilFrom(widget.now)!),
                              ),
                            ),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 4),
            Text(
              l10n.pauseSheetNote,
              style: theme.textTheme.bodySmall?.copyWith(
                color: AppTheme.textMuted,
              ),
            ),
            const SizedBox(height: 16),
            FilledButton.icon(
              key: const Key('pause_confirm'),
              onPressed: () => Navigator.of(context).pop(_choice),
              icon: const Icon(Icons.pause),
              label: Text(l10n.pauseSheetConfirm),
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
            ),
            const SizedBox(height: 8),
            OutlinedButton(
              key: const Key('pause_keep'),
              onPressed: () => Navigator.of(context).pop(),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
              child: Text(l10n.pauseSheetKeep),
            ),
          ],
        ),
      ),
    );
  }
}
