import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The label of a Clock setting choice.
String clockPreferenceLabel(AppLocalizations l10n, ClockPreference p) =>
    switch (p) {
      ClockPreference.auto => l10n.clockAuto,
      ClockPreference.ethiopian => l10n.clockEthiopian,
      ClockPreference.international => l10n.clockInternational,
    };

/// 13:20 on the Ethiopian or the 24-hour clock, to show what a choice
/// looks like.
String clockExample(AppLocalizations l10n, {required bool ethiopian}) =>
    ethiopian
        ? EthiopianClock.format(13, 20, l10n)
        : EthiopianClock.international(13, 20);

/// LT-03: picks the clock times are shown on (Auto, Ethiopian,
/// International). The choice applies at once and is saved like the
/// language.
class ClockSelectionDialog extends StatelessWidget {
  const ClockSelectionDialog({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final settings = Provider.of<SettingsProvider>(context);
    final current = settings.clockPreference;

    String hint(ClockPreference p) => switch (p) {
          ClockPreference.auto => l10n.clockAutoHint,
          ClockPreference.ethiopian => clockExample(l10n, ethiopian: true),
          ClockPreference.international => clockExample(l10n, ethiopian: false),
        };

    // The boards' card: white, radius 18.
    return AlertDialog(
      backgroundColor: AppTheme.surfaceWhite,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
      title: Text(l10n.clockSetting),
      content: RadioGroup<ClockPreference>(
        groupValue: current,
        onChanged: (value) async {
          if (value == null) return;
          await settings.setClockPreference(value);
          if (context.mounted) Navigator.of(context).pop();
        },
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            for (final p in ClockPreference.values)
              RadioListTile<ClockPreference>(
                key: Key('clock_option_${p.name}'),
                value: p,
                title: Text(
                  clockPreferenceLabel(l10n, p),
                  style: TextStyle(
                    fontWeight:
                        p == current ? FontWeight.bold : FontWeight.normal,
                  ),
                ),
                subtitle: Text(hint(p)),
                secondary: p == current
                    ? const Icon(Icons.check, color: AppTheme.statusSuccess)
                    : null,
              ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: Text(l10n.cancel),
        ),
      ],
    );
  }
}
