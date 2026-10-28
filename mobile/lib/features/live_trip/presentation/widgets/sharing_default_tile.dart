import 'package:flutter/material.dart';

import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';
import 'package:wereda_connect/features/live_trip/presentation/check_in_engine_of.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The label of a Trip location setting choice.
String sharingDefaultLabel(AppLocalizations l10n, SharingDefault value) =>
    switch (value) {
      SharingDefault.ask => l10n.sharingDefaultAsk,
      SharingDefault.share => l10n.sharingDefaultShare,
      SharingDefault.scansOnly => l10n.sharingDefaultScansOnly,
    };

/// Settings > Trip location (LT-09): what a trip's first pickup scan does
/// while the trucker has not answered for that trip (ask with the consent
/// sheet, share, or scans only). Only for a signed-in trucker: the engine
/// has a user only then, so a farmer or a buyer never sees the row.
class SharingDefaultTile extends StatefulWidget {
  const SharingDefaultTile({super.key, this.engine});

  /// Defaults to the app's engine.
  final CheckInEngine? engine;

  @override
  State<SharingDefaultTile> createState() => _SharingDefaultTileState();
}

class _SharingDefaultTileState extends State<SharingDefaultTile> {
  CheckInEngine? _engine;

  @override
  void initState() {
    super.initState();
    _engine = widget.engine ?? checkInEngineOf(context);
  }

  Future<void> _pick(CheckInEngine engine) async {
    await showDialog<void>(
      context: context,
      builder: (_) => SharingDefaultDialog(engine: engine),
    );
    if (mounted) setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    final engine = _engine;
    if (engine == null || engine.userId == null) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context)!;
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        ListTile(
          key: const Key('settings_trip_location_tile'),
          leading: const Icon(Icons.share_location),
          title: Text(l10n.sharingDefaultSetting),
          subtitle: Text(sharingDefaultLabel(l10n, engine.sharingDefault)),
          trailing: const Icon(Icons.arrow_forward_ios, size: 16),
          onTap: () => _pick(engine),
        ),
        const Divider(),
      ],
    );
  }
}

/// Picks the Trip location default. The choice applies at once; trips the
/// trucker already answered for keep their answer.
class SharingDefaultDialog extends StatelessWidget {
  const SharingDefaultDialog({required this.engine, super.key});

  final CheckInEngine engine;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final current = engine.sharingDefault;

    return AlertDialog(
      title: Text(l10n.sharingDefaultSetting),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              l10n.sharingDefaultHint,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AppTheme.textMuted,
                  ),
            ),
            const SizedBox(height: 8),
            RadioGroup<SharingDefault>(
              groupValue: current,
              onChanged: (value) async {
                if (value == null) return;
                await engine.setSharingDefault(value);
                if (context.mounted) Navigator.of(context).pop();
              },
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  for (final value in SharingDefault.values)
                    RadioListTile<SharingDefault>(
                      key: Key('sharing_default_${value.name}'),
                      value: value,
                      contentPadding: EdgeInsets.zero,
                      title: Text(
                        sharingDefaultLabel(l10n, value),
                        style: TextStyle(
                          fontWeight: value == current
                              ? FontWeight.bold
                              : FontWeight.normal,
                        ),
                      ),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: Text(l10n.cancel),
        ),
      ],
    );
  }
}
