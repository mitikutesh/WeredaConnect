import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import 'package:wereda_connect/features/live_trip/presentation/widgets/pause_sheet.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

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
        borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
      ),
      builder: (_) => const ShareConsentSheet(),
    );

/// The consent sheet (the Trucker · Share location board): who sees the
/// truck, how often, when it stops and the data it costs (the wire figure of
/// section 5.3), then "Share trip location" or "Not now, scans only". It is
/// also the in-app disclosure for the Android foreground service.
class ShareConsentSheet extends StatelessWidget {
  const ShareConsentSheet({super.key, this.web = kIsWeb});

  /// On web there is no foreground notification: check-ins go only while
  /// the page is open, and the note says so.
  final bool web;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return PopScope(
      canPop: false,
      child: SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.fromLTRB(20, 12, 20, 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const SheetHandle(),
              const SizedBox(height: 16),
              // The board (TruckerShareConsentM): a black pin tile with the
              // orange glyph, the title, the lede, four rows on one sand
              // tile, the orange primary and the outlined "Not now".
              Align(
                alignment: AlignmentDirectional.centerStart,
                child: Container(
                  width: 48,
                  height: 48,
                  decoration: BoxDecoration(
                    color: AppTheme.ink,
                    borderRadius: BorderRadius.circular(14),
                  ),
                  child: const Icon(
                    Icons.place_outlined,
                    color: AppTheme.orange,
                    size: 24,
                  ),
                ),
              ),
              const SizedBox(height: 8),
              Text(
                l10n.liveTripConsentTitle,
                style: const TextStyle(
                  fontSize: 30,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -1.05,
                  height: 1.05,
                  color: AppTheme.ink,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                l10n.liveTripConsentLede,
                style: const TextStyle(
                  fontSize: 14,
                  height: 1.45,
                  color: AppTheme.inkSoft,
                ),
              ),
              const SizedBox(height: 16),
              FwCard.sand(
                child: Column(
                  children: [
                    _ConsentRow(
                      icon: Icons.visibility_outlined,
                      title: l10n.liveTripConsentWhoTitle,
                      text: l10n.liveTripConsentWhoText,
                    ),
                    _ConsentRow(
                      icon: Icons.schedule,
                      title: l10n.liveTripConsentOftenTitle,
                      text: l10n.liveTripConsentOftenText,
                    ),
                    _ConsentRow(
                      icon: Icons.check,
                      title: l10n.liveTripConsentStopsTitle,
                      text: l10n.liveTripConsentStopsText,
                    ),
                    _ConsentRow(
                      icon: Icons.signal_cellular_alt,
                      title: l10n.liveTripConsentDataTitle,
                      text: l10n.liveTripConsentDataText,
                      last: true,
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 16),
              Text(
                web
                    ? l10n.liveTripConsentWebNote
                    : l10n.liveTripConsentAndroidNote,
                style: const TextStyle(fontSize: 13, color: AppTheme.slate),
              ),
              const SizedBox(height: 16),
              // The orange pill: the one action.
              FilledButton(
                key: const Key('consent_share'),
                onPressed: () =>
                    Navigator.of(context).pop(ShareConsentChoice.share),
                child: Text(l10n.liveTripConsentShare),
              ),
              const SizedBox(height: 8),
              OutlinedButton(
                key: const Key('consent_scans_only'),
                onPressed: () =>
                    Navigator.of(context).pop(ShareConsentChoice.scansOnly),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                  textStyle: const TextStyle(
                    fontFamily: AppTheme.fontFamily,
                    fontFamilyFallback: AppTheme.ethiopicFontFallback,
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                  ),
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
    this.last = false,
  });

  final IconData icon;
  final String title;
  final String text;
  final bool last;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: EdgeInsets.only(bottom: last ? 0 : 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Container(
            width: 36,
            height: 36,
            decoration: BoxDecoration(
              color: AppTheme.paper,
              borderRadius: BorderRadius.circular(14),
            ),
            child: Icon(icon, size: 18, color: AppTheme.ink),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.15,
                    color: AppTheme.ink,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  text,
                  style: const TextStyle(
                    fontSize: 14,
                    height: 1.35,
                    color: AppTheme.inkSoft,
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
