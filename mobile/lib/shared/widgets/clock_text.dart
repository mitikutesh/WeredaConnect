import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';

/// The person's Clock setting, or Auto when no [SettingsProvider] is above
/// [context] (a bare widget-test harness). [listen]: rebuild when the setting
/// changes; pass false outside `build` (for example from `initState`).
ClockPreference clockPreferenceOf(BuildContext context, {bool listen = true}) {
  try {
    return Provider.of<SettingsProvider>(context, listen: listen)
        .clockPreference;
  } on ProviderNotFoundException {
    return ClockPreference.auto;
  }
}

/// How this viewer reads times (LT-03): the app language and the Clock
/// setting. Call it in `build` so the screen follows a change of either.
ClockFormat clockFormatOf(BuildContext context) => ClockFormat.resolve(
      AppLocalizations.of(context)!,
      clockPreferenceOf(context),
    );

/// A [ClockLabel] as text: on the Ethiopian clock a deadline's 24-hour time
/// shows small right after it (specs/features/live-trip.md section 9).
class ClockText extends StatelessWidget {
  const ClockText(
    this.label, {
    this.style,
    this.textAlign,
    this.maxLines,
    this.overflow,
    super.key,
  });

  final ClockLabel label;
  final TextStyle? style;
  final TextAlign? textAlign;
  final int? maxLines;
  final TextOverflow? overflow;

  /// The 24-hour companion's size relative to the text around it.
  static const double smallScale = 0.8;

  @override
  Widget build(BuildContext context) {
    final base = DefaultTextStyle.of(context).style.merge(style);
    return Text.rich(
      label.toSpan(
        smallStyle: TextStyle(fontSize: (base.fontSize ?? 14) * smallScale),
      ),
      style: style,
      textAlign: textAlign,
      maxLines: maxLines,
      overflow: overflow,
    );
  }
}
