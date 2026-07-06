import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// Payments tab screen — GreenHeader + embedded PaymentHistoryScreen.
///
/// Uses the existing PaymentHistoryScreen in embedded mode (no Scaffold/AppBar)
/// so its filter chips and payment list render as tab content.
class PaymentsTabScreen extends StatelessWidget {
  const PaymentsTabScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final authState = context.read<AuthBloc>().state;
    final isBuyer =
        authState is AuthAuthenticated && authState.role == UserRole.buyer;

    final isMobile = Breakpoints.isMobile(context);
    final isDesktop = Breakpoints.isDesktop(context);
    final hPad = isDesktop ? 32.0 : 24.0;

    // ── Profile avatar (shared between mobile and desktop headers) ──
    final profileAvatar = GestureDetector(
      onTap: () => Navigator.push(
        context,
        MaterialPageRoute<void>(
          builder: (_) => const ProfileScreen(),
        ),
      ),
      child: CircleAvatar(
        radius: 18,
        backgroundColor:
            isMobile ? Colors.white24 : AppTheme.textMuted.withAlpha(40),
        child: Icon(
          Icons.person,
          size: 20,
          color: isMobile ? Colors.white : AppTheme.textDark,
        ),
      ),
    );

    // ── Mobile layout (unchanged) ─────────────────────────────────
    if (isMobile) {
      return Column(
        children: [
          GreenHeader(
            height: 120,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const SizedBox(height: 8),
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        l10n.myPayments,
                        style: const TextStyle(
                          fontSize: 22,
                          fontWeight: FontWeight.w700,
                          color: Colors.white,
                        ),
                      ),
                    ),
                    profileAvatar,
                  ],
                ),
                const SizedBox(height: 4),
                Text(
                  isBuyer ? l10n.trackBuyerPayments : l10n.trackPayments,
                  style: TextStyle(
                    fontSize: 14,
                    color: Colors.white.withAlpha(180),
                  ),
                ),
              ],
            ),
          ),
          const OfflineBanner(),
          const Expanded(
            child: PaymentHistoryScreen(embedded: true),
          ),
        ],
      );
    }

    // ── Desktop / Tablet layout ───────────────────────────────────
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 900),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // ── Flat page header ──────────────────────────────────
            Padding(
              padding: EdgeInsets.fromLTRB(hPad, 28, hPad, 20),
              child: Column(
                children: [
                  Text(
                    l10n.myPayments,
                    style: const TextStyle(
                      fontSize: 24,
                      fontWeight: FontWeight.w700,
                      color: AppTheme.textDark,
                    ),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 4),
                  Text(
                    isBuyer ? l10n.trackBuyerPayments : l10n.trackPayments,
                    style: const TextStyle(
                      fontSize: 14,
                      color: AppTheme.textMuted,
                    ),
                    textAlign: TextAlign.center,
                  ),
                ],
              ),
            ),
            const Expanded(
              child: PaymentHistoryScreen(embedded: true),
            ),
          ],
        ),
      ),
    );
  }
}
