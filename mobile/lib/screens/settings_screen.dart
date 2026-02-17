import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/widgets/language_selection_dialog.dart';

/// Settings screen - app configuration and preferences
class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final languageProvider = Provider.of<LanguageProvider>(context);

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.settings),
      ),
      body: ListView(
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
          ListTile(
            leading: const Icon(Icons.data_usage),
            title: Text(l10n.dataUsage),
            trailing: const Icon(Icons.arrow_forward_ios, size: 16),
            onTap: () {
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(content: Text(l10n.comingSoon)),
              );
            },
          ),
          const Divider(),
          SwitchListTile(
            secondary: const Icon(Icons.save_alt),
            title: Text(l10n.dataSaverMode),
            subtitle: Text(l10n.dataSaverDescription),
            value: true,
            onChanged: (value) {
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(content: Text(l10n.comingSoon)),
              );
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
                ],
              );
            },
          ),
        ],
      ),
    );
  }
}
