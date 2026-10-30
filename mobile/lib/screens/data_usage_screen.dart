import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';

/// Screen that shows how much data the app has sent and received,
/// with an option to reset the counters. Drawn in the Fieldway boards'
/// language: page header, one white card with the three counters, the
/// notes, and the reset button.
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
              const Align(
                alignment: AlignmentDirectional.centerStart,
                child: JoinBackButton(),
              ),
              const SizedBox(height: 12),
              FwPageHeader(
                padding: EdgeInsets.zero,
                eyebrow: l10n.settings,
                title: l10n.dataUsage,
              ),
              const SizedBox(height: 16),
              FwCard(
                padding: EdgeInsets.zero,
                child: Column(
                  children: [
                    _StatRow(
                      icon: Icons.download_rounded,
                      label: l10n.dataUsageDownloaded,
                      value: DataUsageTracker.formatBytes(
                        tracker.bytesDownloaded,
                      ),
                      color: AppTheme.statusInfo,
                      background: AppTheme.statusInfoContainer,
                    ),
                    const Divider(indent: 16, endIndent: 16),
                    _StatRow(
                      icon: Icons.upload_rounded,
                      label: l10n.dataUsageUploaded,
                      value:
                          DataUsageTracker.formatBytes(tracker.bytesUploaded),
                      color: AppTheme.money,
                      background: AppTheme.actionCream,
                    ),
                    const Divider(indent: 16, endIndent: 16),
                    _StatRow(
                      icon: Icons.data_usage,
                      label: l10n.dataUsageTotal,
                      value: DataUsageTracker.formatBytes(tracker.totalBytes),
                      color: AppTheme.fieldGreen,
                      background: AppTheme.fieldGreenTint,
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 16),
              if (tracker.trackingSince != null) ...[
                Text(
                  '${l10n.dataUsageTrackingSince} '
                  '${DateFormat.yMMMd().format(tracker.trackingSince!)}',
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
          TextButton(
            onPressed: () {
              tracker.reset();
              Navigator.of(ctx).pop();
            },
            child: Text(
              l10n.dataUsageReset,
              style: const TextStyle(color: AppTheme.statusError),
            ),
          ),
        ],
      ),
    );
  }
}

class _StatRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;
  final Color color;
  final Color background;

  const _StatRow({
    required this.icon,
    required this.label,
    required this.value,
    required this.color,
    required this.background,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: const BoxConstraints(minHeight: 56),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Row(
        children: [
          Container(
            width: 36,
            height: 36,
            decoration: BoxDecoration(
              color: background,
              borderRadius: BorderRadius.circular(10),
            ),
            child: Icon(icon, size: 20, color: color),
          ),
          const SizedBox(width: 14),
          Expanded(
            child: Text(
              label,
              style: const TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w700,
                color: AppTheme.soilDark,
              ),
            ),
          ),
          const SizedBox(width: 12),
          Text(
            value,
            style: const TextStyle(
              fontSize: 16,
              fontWeight: FontWeight.w800,
              color: AppTheme.soilDark,
            ),
          ),
        ],
      ),
    );
  }
}
