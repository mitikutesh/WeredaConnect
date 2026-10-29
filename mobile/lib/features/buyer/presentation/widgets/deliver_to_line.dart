import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// "Deliver to Merkato, Addis Ababa" (Buyer · Find crops board): the buyer's
/// wereda, the drop-off that transport suggestions use. Nothing without one.
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
        Container(
          width: 36,
          height: 36,
          decoration: BoxDecoration(
            color: const Color(0xFFE6ECF5),
            borderRadius: BorderRadius.circular(10),
          ),
          child: const Icon(
            Icons.place_outlined,
            size: 18,
            color: Color(0xFF2E4C7D),
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
                  color: AppTheme.mutedSteel,
                ),
              ),
              Text(
                woreda,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.soilDark,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
