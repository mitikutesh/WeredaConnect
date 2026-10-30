import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/features/live_trip/presentation/widgets/sharing_default_tile.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/screens/data_usage_screen.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/widgets/clock_selection_dialog.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/language_selection_dialog.dart';

/// Settings screen in the Fieldway boards' language (no board of its own):
/// the page header, then the rows as white card groups under uppercase
/// section labels (Language and time, Data, then About). One composition
/// at every width, centred by [FwContentWidth] on wide windows.
class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final languageProvider = Provider.of<LanguageProvider>(context);
    final settingsProvider = Provider.of<SettingsProvider>(context);

    return Scaffold(
      body: SafeArea(
        child: FwContentWidth(
          child: ListView(
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
            children: [
              // Pushed from Profile on the phone; a route of its own on web.
              if (Navigator.of(context).canPop()) ...[
                const Align(
                  alignment: AlignmentDirectional.centerStart,
                  child: JoinBackButton(),
                ),
                const SizedBox(height: 12),
              ],
              FwPageHeader(
                padding: EdgeInsets.zero,
                eyebrow: l10n.languageAndAlerts,
                title: l10n.settings,
              ),
              const SizedBox(height: 16),

              FwSectionLabel(l10n.profileSettingsLanguageTime),
              const SizedBox(height: 8),
              FwCard(
                padding: EdgeInsets.zero,
                child: Column(
                  children: [
                    _settingsRow(
                      icon: Icons.language,
                      title: l10n.language,
                      subtitle: languageProvider
                          .getLanguageName(languageProvider.locale),
                      onTap: () {
                        showDialog<void>(
                          context: context,
                          builder: (context) => const LanguageSelectionDialog(),
                        );
                      },
                    ),
                    const Divider(indent: 16, endIndent: 16),
                    // LT-03: the clock times are shown on; Auto follows the
                    // language.
                    _settingsRow(
                      key: const Key('settings_clock_tile'),
                      icon: Icons.schedule,
                      title: l10n.clockSetting,
                      subtitle: '${clockPreferenceLabel(
                        l10n,
                        settingsProvider.clockPreference,
                      )}'
                          ' · '
                          '${clockExample(
                        l10n,
                        ethiopian: clockFormatOf(context).ethiopian,
                      )}',
                      onTap: () {
                        showDialog<void>(
                          context: context,
                          builder: (context) => const ClockSelectionDialog(),
                        );
                      },
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 20),

              FwSectionLabel(l10n.profileSettingsData),
              const SizedBox(height: 8),
              FwCard(
                padding: EdgeInsets.zero,
                child: Column(
                  children: [
                    // LT-09: a trucker's default for sharing the trip's
                    // location (the row and its divider show only for a
                    // signed-in trucker).
                    const SharingDefaultTile(),
                    _settingsRow(
                      icon: Icons.data_usage,
                      title: l10n.dataUsage,
                      onTap: () {
                        Navigator.of(context).push(
                          MaterialPageRoute<void>(
                            builder: (_) => const DataUsageScreen(),
                          ),
                        );
                      },
                    ),
                    const Divider(indent: 16, endIndent: 16),
                    SwitchListTile(
                      contentPadding: _rowPadding,
                      secondary: const _SettingsIcon(Icons.save_alt),
                      title: Text(l10n.dataSaverMode, style: _rowTitleStyle),
                      subtitle: Text(
                        l10n.dataSaverDescription,
                        style: _rowSubtitleStyle,
                      ),
                      value: settingsProvider.dataSaverEnabled,
                      onChanged: (value) {
                        settingsProvider.setDataSaver(enabled: value);
                      },
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 20),

              // No label over the last card: its one row is About itself.
              FwCard(
                padding: EdgeInsets.zero,
                child: _settingsRow(
                  icon: Icons.info_outline,
                  title: l10n.about,
                  subtitle: l10n.appName,
                  onTap: () {
                    showAboutDialog(
                      context: context,
                      applicationName: l10n.appName,
                      applicationVersion: '1.0.0',
                      applicationIcon: const Icon(Icons.agriculture, size: 48),
                      children: [
                        Text(l10n.aboutDescription),
                        const SizedBox(height: 8),
                        Text(l10n.aboutTagline),
                        const SizedBox(height: 8),
                        Text(
                          l10n.aboutMapDataCredit(PlaceRepository.attribution),
                          key: const Key('about_map_data_credit'),
                        ),
                      ],
                    );
                  },
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

const _rowPadding = EdgeInsets.fromLTRB(16, 4, 12, 4);

const _rowTitleStyle = TextStyle(
  fontSize: 14,
  fontWeight: FontWeight.w700,
  color: AppTheme.soilDark,
);

const _rowSubtitleStyle = TextStyle(
  fontSize: 12,
  fontWeight: FontWeight.w500,
  color: AppTheme.mutedSteel,
);

/// A settings row: icon tile, title, optional subtitle, chevron. A
/// [ListTile], so its key, title and subtitle read as one.
Widget _settingsRow({
  required IconData icon,
  required String title,
  required VoidCallback onTap,
  String? subtitle,
  Key? key,
}) {
  return ListTile(
    key: key,
    contentPadding: _rowPadding,
    minTileHeight: 56,
    leading: _SettingsIcon(icon),
    title: Text(title, style: _rowTitleStyle),
    subtitle:
        subtitle == null ? null : Text(subtitle, style: _rowSubtitleStyle),
    trailing: const Icon(
      Icons.chevron_right,
      size: 20,
      color: AppTheme.mutedSteel,
    ),
    onTap: onTap,
  );
}

class _SettingsIcon extends StatelessWidget {
  final IconData icon;

  const _SettingsIcon(this.icon);

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 36,
      height: 36,
      decoration: BoxDecoration(
        color: AppTheme.fieldGreenTint,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Icon(icon, size: 20, color: AppTheme.fieldGreen),
    );
  }
}
