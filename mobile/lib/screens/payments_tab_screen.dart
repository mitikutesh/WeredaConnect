import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/account_menu.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// Payments tab: the boards' page header (person · place, "My Payments",
/// profile square) over the embedded [PaymentHistoryScreen], which brings
/// the escrow summary card, the status pills and the history rows. One
/// composition at every window size, centred by [FwContentWidth].
///
/// Payments left the bottom bar (design canvas page M): on a phone the page
/// is pushed from Home and the account sheet, on web it sits in the shell
/// beside the sidebar. Pushed, it has no page under it to draw on, so it
/// brings its own Scaffold and the round Back before the title; in the
/// shell it is the plain column it always was. [pushed] says which; by
/// default the page looks for a Scaffold above it.
class PaymentsTabScreen extends StatelessWidget {
  const PaymentsTabScreen({this.pushed, super.key});

  /// True for a page pushed on its own route (Back, own Scaffold), false
  /// inside the shell. Null: pushed when no Scaffold is above.
  final bool? pushed;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final authState = context.read<AuthBloc>().state;
    final isBuyer =
        authState is AuthAuthenticated && authState.role == UserRole.buyer;

    final pushed = this.pushed ?? Scaffold.maybeOf(context) == null;
    final header = FwPageHeader(
      padding: EdgeInsets.zero,
      eyebrow: fwPersonPlaceEyebrow(
        authState,
        isBuyer ? l10n.trackBuyerPayments : l10n.trackPayments,
      ),
      title: l10n.myPayments,
      // The account sheet is where a pushed page came from: no bell and
      // account buttons on it, the title gets the room.
      trailing: pushed ? null : const HeaderActions(),
    );

    final page = FwContentWidth(
      child: Column(
        children: [
          Padding(
            // The page draws under the status bar.
            padding: EdgeInsets.fromLTRB(
              20,
              16 + MediaQuery.paddingOf(context).top,
              20,
              12,
            ),
            child: pushed
                ? Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const FwBackButton(key: Key('payments_back')),
                      const SizedBox(width: 12),
                      Expanded(child: header),
                    ],
                  )
                : header,
          ),
          const OfflineBanner(),
          const Expanded(
            child: PaymentHistoryScreen(embedded: true),
          ),
        ],
      ),
    );
    return pushed ? Scaffold(body: page) : page;
  }
}
