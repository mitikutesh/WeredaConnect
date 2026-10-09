import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// The buyer-facing message for a checkout [issue] (ESC-44), in the app's
/// language. `ESCROW_030` has three of them (not waiting, time ran out,
/// cannot be paid online); the bloc tells them apart.
String localizeCheckoutIssue(AppLocalizations l10n, CheckoutIssue issue) {
  return switch (issue) {
    CheckoutIssue.paymentFailed => l10n.orderCheckoutPaymentFailed,
    CheckoutIssue.notWaiting => l10n.orderCheckoutNotWaiting,
    CheckoutIssue.timeRanOut => l10n.orderCheckoutTimeRanOut,
    CheckoutIssue.notPayableOnline => l10n.orderCheckoutNotPayableOnline,
    CheckoutIssue.totalOutOfRange => l10n.paymentTotalOutOfRange,
    CheckoutIssue.notFound => l10n.orderCheckoutOrderMissing,
    CheckoutIssue.notSent => l10n.orderCheckoutNotSent,
    CheckoutIssue.sessionExpired => l10n.escrowErrorAuthRequired,
    CheckoutIssue.unauthorized => l10n.escrowErrorUnauthorized,
    CheckoutIssue.alreadyOpen => l10n.orderCheckoutAlreadyOpen,
    CheckoutIssue.gateway => l10n.orderCheckoutGatewayProblem,
    CheckoutIssue.rateLimited => l10n.escrowErrorRateLimit,
    CheckoutIssue.unavailable => l10n.escrowErrorServiceUnavailable,
    CheckoutIssue.offline => l10n.escrowErrorOffline,
  };
}
