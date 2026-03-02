import 'package:flutter/material.dart';

import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';

/// Payments tab screen — GreenHeader + embedded PaymentHistoryScreen.
///
/// Uses the existing PaymentHistoryScreen in embedded mode (no Scaffold/AppBar)
/// so its filter chips and payment list render as tab content.
class PaymentsTabScreen extends StatelessWidget {
  const PaymentsTabScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Column(
      children: [
        // ── Header ──────────────────────────────────────────────────
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
                  GestureDetector(
                    onTap: () => Navigator.push(
                      context,
                      MaterialPageRoute<void>(
                        builder: (_) => const ProfileScreen(),
                      ),
                    ),
                    child: const CircleAvatar(
                      radius: 18,
                      backgroundColor: Colors.white24,
                      child: Icon(
                        Icons.person,
                        size: 20,
                        color: Colors.white,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 4),
              Text(
                l10n.trackPayments,
                style: TextStyle(
                  fontSize: 14,
                  color: Colors.white.withAlpha(180),
                ),
              ),
            ],
          ),
        ),

        // ── Embedded payment history ────────────────────────────────
        const Expanded(
          child: PaymentHistoryScreen(embedded: true),
        ),
      ],
    );
  }
}
