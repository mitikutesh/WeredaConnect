import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Screen that shows how much data the app has sent and received,
/// with an option to reset the counters.
class DataUsageScreen extends StatelessWidget {
  const DataUsageScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final tracker = Provider.of<DataUsageTracker>(context);

    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.dataUsage),
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          _StatCard(
            icon: Icons.download_rounded,
            label: l10n.dataUsageDownloaded,
            value: DataUsageTracker.formatBytes(tracker.bytesDownloaded),
            color: AppTheme.statusInfo,
          ),
          const SizedBox(height: 12),
          _StatCard(
            icon: Icons.upload_rounded,
            label: l10n.dataUsageUploaded,
            value: DataUsageTracker.formatBytes(tracker.bytesUploaded),
            color: AppTheme.accentGold,
          ),
          const SizedBox(height: 12),
          _StatCard(
            icon: Icons.data_usage,
            label: l10n.dataUsageTotal,
            value: DataUsageTracker.formatBytes(tracker.totalBytes),
            color: AppTheme.statusSuccess,
          ),
          if (tracker.trackingSince != null) ...[
            const SizedBox(height: 16),
            Text(
              '${l10n.dataUsageTrackingSince} '
              '${DateFormat.yMMMd().format(tracker.trackingSince!)}',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AppTheme.textMuted,
                  ),
              textAlign: TextAlign.center,
            ),
          ],
          const SizedBox(height: 8),
          Text(
            l10n.dataUsageScopeNote,
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AppTheme.textMuted,
                ),
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 32),
          OutlinedButton.icon(
            icon: const Icon(Icons.restart_alt),
            label: Text(l10n.dataUsageReset),
            style: OutlinedButton.styleFrom(
              foregroundColor: AppTheme.statusError,
              side: const BorderSide(color: AppTheme.statusError),
            ),
            onPressed: () => _confirmReset(context, l10n, tracker),
          ),
        ],
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

class _StatCard extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;
  final Color color;

  const _StatCard({
    required this.icon,
    required this.label,
    required this.value,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Card(
      child: ListTile(
        leading: CircleAvatar(
          backgroundColor: color.withAlpha(38),
          child: Icon(icon, color: color),
        ),
        title: Text(label),
        trailing: Text(
          value,
          style: Theme.of(context)
              .textTheme
              .titleMedium
              ?.copyWith(fontWeight: FontWeight.bold),
        ),
      ),
    );
  }
}
