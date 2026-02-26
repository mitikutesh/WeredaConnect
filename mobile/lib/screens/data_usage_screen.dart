import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

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
            color: Colors.blue,
          ),
          const SizedBox(height: 12),
          _StatCard(
            icon: Icons.upload_rounded,
            label: l10n.dataUsageUploaded,
            value: DataUsageTracker.formatBytes(tracker.bytesUploaded),
            color: Colors.orange,
          ),
          const SizedBox(height: 12),
          _StatCard(
            icon: Icons.data_usage,
            label: l10n.dataUsageTotal,
            value: DataUsageTracker.formatBytes(tracker.totalBytes),
            color: Colors.green,
          ),
          if (tracker.trackingSince != null) ...[
            const SizedBox(height: 16),
            Text(
              '${l10n.dataUsageTrackingSince} '
              '${DateFormat.yMMMd().format(tracker.trackingSince!)}',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: Colors.grey[600],
                  ),
              textAlign: TextAlign.center,
            ),
          ],
          const SizedBox(height: 32),
          OutlinedButton.icon(
            icon: const Icon(Icons.restart_alt),
            label: Text(l10n.dataUsageReset),
            style: OutlinedButton.styleFrom(
              foregroundColor: Colors.red,
              side: const BorderSide(color: Colors.red),
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
              style: const TextStyle(color: Colors.red),
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
          backgroundColor: color.withOpacity(0.15),
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
