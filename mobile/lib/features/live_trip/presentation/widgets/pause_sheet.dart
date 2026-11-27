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
        borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
      ),
      builder: (_) => PauseSheet(now: now),
    );

/// The pause sheet's content (LT-09; the Trucker · Offline and pause board,
/// TruckerPausedM): a handle, the title and lede, one row per choice with
/// the time it comes back on the same line (the chosen one black), the
/// note, the black Pause sharing and the outlined Keep sharing.
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
    final clock = clockFormatOf(context);
    final offered = PauseChoice.offeredAt(widget.now);

    return SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const SheetHandle(),
            const SizedBox(height: 16),
            Text(
              l10n.pauseSheetTitle,
              style: const TextStyle(
                fontSize: 30,
                fontWeight: FontWeight.w800,
                letterSpacing: -1.05,
                height: 1.05,
                color: AppTheme.ink,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              l10n.pauseSheetText,
              style: const TextStyle(
                fontSize: 14,
                height: 1.45,
                color: AppTheme.inkSoft,
              ),
            ),
            const SizedBox(height: 16),
            Semantics(
              label: l10n.pauseSheetFor,
              child: RadioGroup<PauseChoice>(
                groupValue: _choice,
                onChanged: (value) {
                  if (value != null) setState(() => _choice = value);
                },
                child: Column(
                  children: [
                    // The board: each choice a 52 px row, the chosen one
                    // black with white ink, the time it comes back on the
                    // same line.
                    for (final (i, c) in offered.indexed)
                      Padding(
                        padding: EdgeInsets.only(top: i == 0 ? 0 : 8),
                        child: _ChoiceRow(
                          key: Key('pause_${c.name}'),
                          choice: c,
                          selected: c == _choice,
                          label: c == PauseChoice.overnight
                              ? _label(c, l10n, clock)
                              : '${_label(c, l10n, clock)} · '
                                  '${l10n.pauseSheetBackAt(clock.time(c.untilFrom(widget.now)!))}',
                          onTap: () => setState(() => _choice = c),
                        ),
                      ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 16),
            Text(
              l10n.pauseSheetNote,
              style: const TextStyle(fontSize: 13, color: AppTheme.slate),
            ),
            const SizedBox(height: 16),
            FilledButton(
              key: const Key('pause_confirm'),
              onPressed: () => Navigator.of(context).pop(_choice),
              // Black, not orange: pausing is not what the page asks for.
              style: FilledButton.styleFrom(
                backgroundColor: AppTheme.ink,
                foregroundColor: Colors.white,
              ),
              child: Text(l10n.pauseSheetConfirm),
            ),
            const SizedBox(height: 12),
            OutlinedButton(
              key: const Key('pause_keep'),
              onPressed: () => Navigator.of(context).pop(),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
                textStyle: const TextStyle(
                  fontFamily: AppTheme.fontFamily,
                  fontFamilyFallback: AppTheme.ethiopicFontFallback,
                  fontSize: 14,
                  fontWeight: FontWeight.w700,
                ),
              ),
              child: Text(l10n.pauseSheetKeep),
            ),
          ],
        ),
      ),
    );
  }
}

/// One pause choice: a radio and its label in a bordered row; the chosen
/// one is black with white ink (and its radio filled), so colour is not the
/// only sign.
class _ChoiceRow extends StatelessWidget {
  const _ChoiceRow({
    required this.choice,
    required this.selected,
    required this.label,
    required this.onTap,
    super.key,
  });

  final PauseChoice choice;
  final bool selected;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: selected ? AppTheme.ink : Colors.transparent,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(16),
        side: BorderSide(
          color: selected ? AppTheme.ink : AppTheme.fieldBorder,
        ),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Container(
          constraints: const BoxConstraints(minHeight: 52),
          padding: const EdgeInsets.symmetric(horizontal: 16),
          child: Row(
            children: [
              SizedBox(
                width: 20,
                height: 20,
                child: Radio<PauseChoice>(
                  value: choice,
                  fillColor: WidgetStateProperty.all(
                    selected ? Colors.white : AppTheme.inkSoft,
                  ),
                  materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
                  visualDensity: VisualDensity.compact,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  label,
                  style: TextStyle(
                    fontSize: 15,
                    fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
                    color: selected ? Colors.white : AppTheme.inkSoft,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The boards' sheet handle: 40 by 4, centred.
class SheetHandle extends StatelessWidget {
  const SheetHandle({super.key});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Container(
        width: 40,
        height: 4,
        decoration: BoxDecoration(
          color: AppTheme.sketch,
          borderRadius: BorderRadius.circular(999),
        ),
      ),
    );
  }
}
