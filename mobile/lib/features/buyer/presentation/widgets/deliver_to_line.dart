import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// "Deliver to Merkato, Addis Ababa" (MarketM board): a pin, the label,
/// then the buyer's wereda in bold, as the Find Crops header's eyebrow. The
/// wereda is the drop-off that transport suggestions use. Nothing without
/// one.
class DeliverToLine extends StatelessWidget {
  const DeliverToLine({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final auth = context.watch<AuthBloc>().state;
    final woreda = auth is AuthAuthenticated ? auth.woreda?.trim() : null;
    if (woreda == null || woreda.isEmpty) return const SizedBox.shrink();

    return Row(
      children: [
        const Icon(Icons.place_outlined, size: 14, color: AppTheme.slate),
        const SizedBox(width: 6),
        Flexible(
          child: Text.rich(
            TextSpan(
              text: '${l10n.deliverTo} ',
              children: [
                TextSpan(
                  text: woreda,
                  style: const TextStyle(
                    fontWeight: FontWeight.w700,
                    color: AppTheme.ink,
                  ),
                ),
              ],
            ),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w500,
              color: AppTheme.slate,
            ),
          ),
        ),
      ],
    );
  }
}
