import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/core/utils/date_locale.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// Screen that shows how much data the app has sent and received,
/// with an option to reset the counters. Drawn in the boards' language: the
/// round back button, a 32 px detail title, the total as the raised card
/// with its amount in the role's colour, the two counters as sand tiles,
/// the notes, and the reset button.
class DataUsageScreen extends StatelessWidget {
  const DataUsageScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final tracker = Provider.of<DataUsageTracker>(context);

    return Scaffold(
      body: SafeArea(
        child: FwContentWidth(
          child: ListView(
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
            children: [
              FwStackedHeader(
                eyebrow: l10n.settings,
                title: l10n.dataUsage,
              ),
              const SizedBox(height: 16),
              FwCard(
                raised: true,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    FwSectionLabel(l10n.dataUsageTotal),
                    const SizedBox(height: 6),
                    Text(
                      DataUsageTracker.formatBytes(tracker.totalBytes),
                      key: const Key('data_usage_total'),
                      style: TextStyle(
                        fontSize: 32,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.9,
                        height: 1.1,
                        color: context.roleColors.primary,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 12),
              IntrinsicHeight(
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Expanded(
                      child: _StatTile(
                        icon: Icons.download_rounded,
                        label: l10n.dataUsageDownloaded,
                        value: DataUsageTracker.formatBytes(
                          tracker.bytesDownloaded,
                        ),
                      ),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: _StatTile(
                        icon: Icons.upload_rounded,
                        label: l10n.dataUsageUploaded,
                        value: DataUsageTracker.formatBytes(
                          tracker.bytesUploaded,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 16),
              if (tracker.trackingSince != null) ...[
                Text(
                  '${l10n.dataUsageTrackingSince} '
                  '${formatDate(tracker.trackingSince!)}',
                  style: FwText.body,
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 8),
              ],
              Text(
                l10n.dataUsageScopeNote,
                style: FwText.body,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 32),
              OutlinedButton.icon(
                icon: const Icon(Icons.restart_alt),
                label: Text(l10n.dataUsageReset),
                style: OutlinedButton.styleFrom(
                  foregroundColor: AppTheme.statusError,
                ),
                onPressed: () => _confirmReset(context, l10n, tracker),
              ),
            ],
          ),
        ),
      ),
    );
  }

  void _confirmReset(
    BuildContext context,
    AppLocalizations l10n,
    DataUsageTracker tracker,
  ) {
    showDialog<void>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.dataUsageReset),
        content: Text(l10n.dataUsageResetConfirm),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: Text(l10n.cancel),
          ),
          FilledButton(
            onPressed: () {
              tracker.reset();
              Navigator.of(ctx).pop();
            },
            style: FilledButton.styleFrom(
              backgroundColor: AppTheme.statusError,
              foregroundColor: Colors.white,
              minimumSize: const Size(0, 48),
            ),
            child: Text(l10n.dataUsageReset),
          ),
        ],
      ),
    );
  }
}

/// One counter on a flat sand tile: the label over the amount.
class _StatTile extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;

  const _StatTile({
    required this.icon,
    required this.label,
    required this.value,
  });

  @override
  Widget build(BuildContext context) {
    return FwCard.sand(
      padding: const EdgeInsets.fromLTRB(14, 12, 14, 13),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(icon, size: 16, color: AppTheme.inkSoft),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  label,
                  style: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                    color: AppTheme.inkSoft,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 2),
          Text(
            value,
            style: const TextStyle(
              fontSize: 22,
              fontWeight: FontWeight.w800,
              letterSpacing: -0.4,
              color: AppTheme.ink,
            ),
          ),
        ],
      ),
    );
  }
}
