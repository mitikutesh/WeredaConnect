import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// "Deliver to Merkato, Addis Ababa" (Buyer · Find crops board): the buyer's
/// wereda, the drop-off that transport suggestions use, after the board's
/// blue place tile. Nothing without one.
class DeliverToLine extends StatelessWidget {
  const DeliverToLine({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final auth = context.watch<AuthBloc>().state;
    final woreda = auth is AuthAuthenticated ? auth.woreda?.trim() : null;
    if (woreda == null || woreda.isEmpty) return const SizedBox.shrink();

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Row(
        children: [
          Container(
            width: 44,
            height: 44,
            decoration: BoxDecoration(
              color: AppTheme.dropoffBlueTint,
              borderRadius: BorderRadius.circular(14),
            ),
            child: const Icon(
              Icons.place_outlined,
              size: 20,
              color: AppTheme.dropoffBlue,
            ),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.deliverTo,
                  style: const TextStyle(
                    fontSize: 12,
                    fontWeight: FontWeight.w500,
                    color: AppTheme.mutedSteel,
                  ),
                ),
                Text(
                  woreda,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.soilDark,
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
