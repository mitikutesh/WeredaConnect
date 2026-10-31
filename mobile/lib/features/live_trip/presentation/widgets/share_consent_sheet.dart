import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import 'package:wereda_connect/features/live_trip/presentation/widgets/pause_sheet.dart';
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
          padding: const EdgeInsets.fromLTRB(22, 12, 22, 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const SheetHandle(),
              const SizedBox(height: 16),
              // The board: a cream pin tile, the title, the lede, four
              // rows with chalk icon tiles, a dark primary button and a
              // plain "Not now".
              Align(
                alignment: AlignmentDirectional.centerStart,
                child: Container(
                  width: 48,
                  height: 48,
                  decoration: BoxDecoration(
                    color: AppTheme.actionCream,
                    borderRadius: BorderRadius.circular(14),
                  ),
                  child: const Icon(
                    Icons.place_outlined,
                    color: AppTheme.money,
                    size: 26,
                  ),
                ),
              ),
              const SizedBox(height: 8),
              Text(
                l10n.liveTripConsentTitle,
                style: const TextStyle(
                  fontSize: 24,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -0.5,
                  height: 1.2,
                  color: AppTheme.soilDark,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                l10n.liveTripConsentLede,
                style: const TextStyle(
                  fontSize: 14,
                  height: 1.5,
                  color: AppTheme.unselectedNav,
                ),
              ),
              const SizedBox(height: 16),
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
              const SizedBox(height: 16),
              Text(
                web
                    ? l10n.liveTripConsentWebNote
                    : l10n.liveTripConsentAndroidNote,
                style:
                    const TextStyle(fontSize: 12, color: AppTheme.mutedSteel),
              ),
              const SizedBox(height: 16),
              FilledButton(
                key: const Key('consent_share'),
                onPressed: () =>
                    Navigator.of(context).pop(ShareConsentChoice.share),
                style: FilledButton.styleFrom(
                  backgroundColor: AppTheme.soilDark,
                  foregroundColor: Colors.white,
                  minimumSize: const Size.fromHeight(56),
                  textStyle: const TextStyle(
                    fontFamily: AppTheme.fontFamily,
                    fontFamilyFallback: AppTheme.ethiopicFontFallback,
                    fontSize: 16,
                    fontWeight: FontWeight.w800,
                  ),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(16),
                  ),
                ),
                child: Text(l10n.liveTripConsentShare),
              ),
              const SizedBox(height: 6),
              TextButton(
                key: const Key('consent_scans_only'),
                onPressed: () =>
                    Navigator.of(context).pop(ShareConsentChoice.scansOnly),
                style: TextButton.styleFrom(
                  foregroundColor: AppTheme.unselectedNav,
                  minimumSize: const Size.fromHeight(44),
                  textStyle: const TextStyle(
                    fontFamily: AppTheme.fontFamily,
                    fontFamilyFallback: AppTheme.ethiopicFontFallback,
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                  ),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(14),
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
      padding: EdgeInsets.only(bottom: last ? 0 : 14),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Container(
            width: 36,
            height: 36,
            decoration: BoxDecoration(
              color: AppTheme.chalkBackground,
              borderRadius: BorderRadius.circular(11),
            ),
            child: Icon(icon, size: 20, color: AppTheme.soilDark),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: const TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.soilDark,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  text,
                  style: const TextStyle(
                    fontSize: 13,
                    height: 1.4,
                    color: AppTheme.mutedSteel,
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
