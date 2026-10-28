import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The trucker's answer on the consent sheet.
enum ShareConsentChoice { share, scansOnly }

/// Asks "Share where the truck is?" once per trip (LT-09, live-trip.md
/// section 4), above every route. The sheet cannot be dismissed: the trucker
/// picks one of its two buttons. Null only when the sheet was closed from
/// outside (a sign out), and then nothing is recorded.
Future<ShareConsentChoice?> showShareConsentSheet(BuildContext context) =>
    showModalBottomSheet<ShareConsentChoice>(
      context: context,
      useRootNavigator: true,
      isDismissible: false,
      enableDrag: false,
      isScrollControlled: true,
      backgroundColor: AppTheme.surfaceWhite,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
      ),
      builder: (_) => const ShareConsentSheet(),
    );

/// The consent sheet: who sees the truck, how often, when it stops and the
/// data it costs (the wire figure of section 5.3), then "Share trip
/// location" or "Not now, scans only". It is also the in-app disclosure for
/// the Android foreground service.
class ShareConsentSheet extends StatelessWidget {
  const ShareConsentSheet({super.key, this.web = kIsWeb});

  /// On web there is no foreground notification: check-ins go only while
  /// the page is open, and the note says so.
  final bool web;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return PopScope(
      canPop: false,
      child: SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.fromLTRB(20, 24, 20, 16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Align(
                alignment: Alignment.centerLeft,
                child: CircleAvatar(
                  radius: 24,
                  backgroundColor: AppTheme.statusSuccessContainer,
                  child: Icon(
                    Icons.share_location,
                    color: AppTheme.primary,
                    size: 26,
                  ),
                ),
              ),
              const SizedBox(height: 16),
              Text(
                l10n.liveTripConsentTitle,
                style: theme.textTheme.titleLarge?.copyWith(
                  fontWeight: FontWeight.w800,
                  color: AppTheme.textDark,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                l10n.liveTripConsentLede,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(height: 16),
              _ConsentRow(
                icon: Icons.group_outlined,
                title: l10n.liveTripConsentWhoTitle,
                text: l10n.liveTripConsentWhoText,
              ),
              _ConsentRow(
                icon: Icons.schedule,
                title: l10n.liveTripConsentOftenTitle,
                text: l10n.liveTripConsentOftenText,
              ),
              _ConsentRow(
                icon: Icons.flag_outlined,
                title: l10n.liveTripConsentStopsTitle,
                text: l10n.liveTripConsentStopsText,
              ),
              _ConsentRow(
                icon: Icons.signal_cellular_alt,
                title: l10n.liveTripConsentDataTitle,
                text: l10n.liveTripConsentDataText,
              ),
              const SizedBox(height: 4),
              Text(
                web
                    ? l10n.liveTripConsentWebNote
                    : l10n.liveTripConsentAndroidNote,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(height: 16),
              FilledButton.icon(
                key: const Key('consent_share'),
                onPressed: () =>
                    Navigator.of(context).pop(ShareConsentChoice.share),
                icon: const Icon(Icons.share_location),
                label: Text(l10n.liveTripConsentShare),
                style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                ),
              ),
              const SizedBox(height: 8),
              OutlinedButton(
                key: const Key('consent_scans_only'),
                onPressed: () =>
                    Navigator.of(context).pop(ShareConsentChoice.scansOnly),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                ),
                child: Text(l10n.liveTripConsentDecline),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _ConsentRow extends StatelessWidget {
  const _ConsentRow({
    required this.icon,
    required this.title,
    required this.text,
  });

  final IconData icon;
  final String title;
  final String text;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: 22, color: AppTheme.primary),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                    color: AppTheme.textDark,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  text,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: AppTheme.textMuted,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
