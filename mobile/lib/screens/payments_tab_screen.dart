import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/notification_bell.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// Payments tab: the boards' page header (person · place, "My Payments",
/// profile square) over the embedded [PaymentHistoryScreen], which brings
/// the escrow summary card, the status pills and the history rows. One
/// composition at every window size, centred by [FwContentWidth].
class PaymentsTabScreen extends StatelessWidget {
  const PaymentsTabScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final authState = context.read<AuthBloc>().state;
    final isBuyer =
        authState is AuthAuthenticated && authState.role == UserRole.buyer;

    return FwContentWidth(
      child: Column(
        children: [
          Padding(
            // A tab child draws under the status bar.
            padding: EdgeInsets.fromLTRB(
              20,
              16 + MediaQuery.paddingOf(context).top,
              20,
              12,
            ),
            child: FwPageHeader(
              padding: EdgeInsets.zero,
              eyebrow: fwPersonPlaceEyebrow(
                authState,
                isBuyer ? l10n.trackBuyerPayments : l10n.trackPayments,
              ),
              title: l10n.myPayments,
              trailing: const HeaderBell(),
            ),
          ),
          const OfflineBanner(),
          const Expanded(
            child: PaymentHistoryScreen(embedded: true),
          ),
        ],
      ),
    );
  }
}
