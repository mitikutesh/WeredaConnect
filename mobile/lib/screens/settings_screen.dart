import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/screens/data_usage_screen.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/widgets/clock_selection_dialog.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/language_selection_dialog.dart';

/// Settings screen - app configuration and preferences
class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final languageProvider = Provider.of<LanguageProvider>(context);
    final settingsProvider = Provider.of<SettingsProvider>(context);
    final isMobile = Breakpoints.isMobile(context);

    Widget listContent = ListView(
      padding: isMobile
          ? EdgeInsets.zero
          : const EdgeInsets.symmetric(horizontal: 24, vertical: 8),
      children: [
        ListTile(
          leading: const Icon(Icons.language),
          title: Text(l10n.language),
          subtitle:
              Text(languageProvider.getLanguageName(languageProvider.locale)),
          trailing: const Icon(Icons.arrow_forward_ios, size: 16),
          onTap: () {
            showDialog<void>(
              context: context,
              builder: (context) => const LanguageSelectionDialog(),
            );
          },
        ),
        const Divider(),
        // LT-03: the clock times are shown on; Auto follows the language.
        ListTile(
          key: const Key('settings_clock_tile'),
          leading: const Icon(Icons.schedule),
          title: Text(l10n.clockSetting),
          subtitle: Text(
            '${clockPreferenceLabel(l10n, settingsProvider.clockPreference)}'
            ' \u00b7 '
            '${clockExample(l10n, ethiopian: clockFormatOf(context).ethiopian)}',
          ),
          trailing: const Icon(Icons.arrow_forward_ios, size: 16),
          onTap: () {
            showDialog<void>(
              context: context,
              builder: (context) => const ClockSelectionDialog(),
            );
          },
        ),
        const Divider(),
        ListTile(
          leading: const Icon(Icons.data_usage),
          title: Text(l10n.dataUsage),
          trailing: const Icon(Icons.arrow_forward_ios, size: 16),
          onTap: () {
            Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => const DataUsageScreen(),
              ),
            );
          },
        ),
        const Divider(),
        SwitchListTile(
          secondary: const Icon(Icons.save_alt),
          title: Text(l10n.dataSaverMode),
          subtitle: Text(l10n.dataSaverDescription),
          value: settingsProvider.dataSaverEnabled,
          onChanged: (value) {
            settingsProvider.setDataSaver(enabled: value);
          },
        ),
        const Divider(),
        ListTile(
          leading: const Icon(Icons.info_outline),
          title: Text(l10n.about),
          trailing: const Icon(Icons.arrow_forward_ios, size: 16),
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
      ],
    );

    if (!isMobile) {
      final isDesktop = Breakpoints.isDesktop(context);
      final hPad = isDesktop ? 32.0 : 24.0;
      listContent = Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: EdgeInsets.fromLTRB(hPad, 28, hPad, 20),
            child: Text(
              l10n.settings,
              style: const TextStyle(
                fontSize: 24,
                fontWeight: FontWeight.w700,
                color: AppTheme.textDark,
              ),
              textAlign: TextAlign.center,
            ),
          ),
          Expanded(
            child: Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 600),
                child: Card(
                  elevation: 0,
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(12),
                    side: const BorderSide(color: AppTheme.borderLight),
                  ),
                  color: AppTheme.surfaceWhite,
                  margin: EdgeInsets.zero,
                  child: listContent,
                ),
              ),
            ),
          ),
        ],
      );
      return Scaffold(body: listContent);
    }

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.settings),
      ),
      body: listContent,
    );
  }
}
