import 'dart:async';

import 'package:app_links/app_links.dart';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:url_launcher/link.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/utils/money_format.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/telebirr_launcher.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_bloc.dart';
import 'package:wereda_connect/features/order/presentation/order_checkout_l10n.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/features/order/presentation/widgets/payment_cards.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// The live Telebirr checkout of a `payment_submitted` order (ESC-44).
///
/// Opens Telebirr (Android: on its own, once per page; web: only from a tap
/// on a real link, since browsers block pop-ups after a network wait), then
/// reads the order until the webhook funds it. Reads back off from 5 to 30
/// seconds, stop 10 minutes after the last trigger and pause while the app
/// is in the background; coming back (resume, the
/// `weredaconnect://payment/complete` return link) reads at once.
class OrderCheckoutScreen extends StatefulWidget {
  final Order order;

  /// The buyer just paid or tapped "Continue payment": request the checkout
  /// without another tap once the order is `payment_submitted`.
  final bool autoOpen;

  /// Test seams. [bloc] replaces the one built from the app's repositories;
  /// [launcher] opens Telebirr; [returnLinks] are the app's incoming links
  /// (none on web); [isWeb] picks the launch; [connectivityRestored] fires
  /// when the network comes back; [verificationScreen] replaces the Fayda
  /// screen of the step-up prompt.
  final OrderCheckoutBloc? bloc;
  final ExternalUrlLauncher? launcher;
  final Stream<Uri>? returnLinks;
  final bool? isWeb;
  final Stream<void>? connectivityRestored;
  final WidgetBuilder? verificationScreen;

  const OrderCheckoutScreen({
    required this.order,
    this.autoOpen = true,
    this.bloc,
    this.launcher,
    this.returnLinks,
    this.isWeb,
    this.connectivityRestored,
    this.verificationScreen,
    super.key,
  });

  @override
  State<OrderCheckoutScreen> createState() => _OrderCheckoutScreenState();
}

class _OrderCheckoutScreenState extends State<OrderCheckoutScreen>
    with WidgetsBindingObserver {
  late final OrderCheckoutBloc _bloc;
  late final bool _ownsBloc;
  late final bool _isWeb;

  Timer? _pollTimer;
  Timer? _expiryTimer;
  int _attempt = 0;

  /// Time spent waiting between reads since the last trigger; the reads
  /// stop at [orderCheckoutPollCap]. Time in the background does not count.
  Duration _polledFor = Duration.zero;
  bool _capReached = false;
  bool _foreground = true;
  int _launchedToken = 0;
  bool _announcedPaid = false;
  StreamSubscription<Uri>? _linkSub;
  StreamSubscription<void>? _connectivitySub;

  @override
  void initState() {
    super.initState();
    _isWeb = widget.isWeb ?? kIsWeb;
    _ownsBloc = widget.bloc == null;
    _bloc = widget.bloc ?? _createBloc();
    WidgetsBinding.instance.addObserver(this);
    TelebirrReturnWatchers.add();
    _linkSub = (widget.returnLinks ?? _appReturnLinks()).listen(
      (uri) {
        if (isTelebirrReturnLink(uri)) _trigger(refreshNow: true);
      },
      onError: (Object _) {},
    );
    _connectivitySub = (widget.connectivityRestored ?? _networkBack()).listen(
      (_) {
        // The bloc reads the order (and retries a request that failed
        // offline); only the schedule starts over here.
        _bloc.add(const CheckoutConnectivityRestored());
        _trigger();
      },
      onError: (Object _) {},
    );
    _bloc.add(CheckoutStarted(autoOpen: widget.autoOpen));
    _trigger();
  }

  OrderCheckoutBloc _createBloc() {
    final orders = context.read<OrderBloc>().repository;
    final sync = _read<MarketplaceSync?>();
    return OrderCheckoutBloc(
      orderId: widget.order.orderId,
      escrow: context.read<EscrowBloc>().repository,
      // Mobile reads through the marketplace sync (it waits for the queued
      // payment and caches the server copy); web reads the API directly.
      readServerOrder: sync != null
          ? sync.pullOrder
          : (orders is WebOrderRepository
              ? orders.fetchOrder
              : orders.getOrderById),
      readLocalOrder: sync != null ? orders.getOrderById : null,
    );
  }

  T? _read<T>() {
    try {
      return context.read<T>();
    } on ProviderNotFoundException {
      return null;
    }
  }

  Stream<Uri> _appReturnLinks() {
    if (_isWeb) return const Stream.empty();
    try {
      return AppLinks().uriLinkStream;
    } on Object {
      return const Stream.empty();
    }
  }

  /// Mobile: fires when the device gets a network again. Web has no monitor.
  Stream<void> _networkBack() {
    final monitor = _read<ConnectivityMonitor>();
    if (monitor == null) return const Stream.empty();
    return monitor.onConnectivityChanged
        .where(monitor.isConnectedFromResults)
        .map((_) {});
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    _expiryTimer?.cancel();
    unawaited(_linkSub?.cancel());
    unawaited(_connectivitySub?.cancel());
    WidgetsBinding.instance.removeObserver(this);
    TelebirrReturnWatchers.remove();
    if (_ownsBloc) unawaited(_bloc.close());
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    switch (state) {
      case AppLifecycleState.resumed:
        _foreground = true;
        // Back from Telebirr (or the tab is visible again): read at once.
        _trigger(refreshNow: true);
      case AppLifecycleState.paused || AppLifecycleState.hidden:
        // Save data while the buyer is in Telebirr.
        _foreground = false;
        _pollTimer?.cancel();
      case AppLifecycleState.inactive || AppLifecycleState.detached:
        // A dialog, a call banner or the app switcher: keep going.
        break;
    }
  }

  /// Start the read schedule over (and read now when [refreshNow]).
  void _trigger({bool refreshNow = false}) {
    if (!mounted) return;
    _attempt = 0;
    _polledFor = Duration.zero;
    if (_capReached) setState(() => _capReached = false);
    if (refreshNow) _bloc.add(const CheckoutRefreshed());
    _schedule();
  }

  void _schedule() {
    _pollTimer?.cancel();
    _pollTimer = null;
    if (!_foreground || _capReached || !_bloc.state.polls) return;
    _pollTimer = Timer(orderCheckoutPollDelay(_attempt), _onTick);
  }

  void _onTick() {
    if (!mounted) return;
    _pollTimer = null;
    _polledFor += orderCheckoutPollDelay(_attempt);
    if (_polledFor > orderCheckoutPollCap) {
      setState(() => _capReached = true);
      return;
    }
    _attempt++;
    _bloc.add(const CheckoutRefreshed());
    _schedule();
  }

  void _onState(BuildContext context, OrderCheckoutState state) {
    if (state.polls) {
      // Reading again after a pause (a request, a stop the KYC resubmit
      // woke up): start the schedule over.
      if (_pollTimer == null) {
        _attempt = 0;
        _polledFor = Duration.zero;
        _schedule();
      }
    } else {
      _pollTimer?.cancel();
      _pollTimer = null;
    }

    _expiryTimer?.cancel();
    _expiryTimer = null;
    if (state is CheckoutAwaitingPayment) {
      // Read once more when the page stops being payable.
      _expiryTimer = Timer(
        state.timeLeft + const Duration(seconds: 1),
        () => _bloc.add(const CheckoutRefreshed()),
      );
      if (state.launchToken > _launchedToken) {
        _launchedToken = state.launchToken;
        // Android opens Telebirr itself, once per page, only in the
        // foreground. Web waits for a tap on the link.
        if (!_isWeb && _foreground) unawaited(_openAgain(state.toPayUrl));
      }
    }

    if (state is CheckoutPaid && !_announcedPaid) {
      _announcedPaid = true;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(AppLocalizations.of(context)!.escrowPaymentConfirmed),
        ),
      );
    }
  }

  Future<void> _openAgain(String url) async {
    await openTelebirrCheckout(
      context,
      url,
      launcher: widget.launcher ?? launchExternally,
    );
    _trigger();
  }

  Future<void> _confirmItsYou({required bool withFayda}) async {
    if (withFayda) {
      await showFaydaStepUpDialog(
        context,
        verificationScreen: widget.verificationScreen,
      );
    } else {
      await showStepUpDialog(context);
    }
    if (mounted) _trigger(refreshNow: true);
  }

  void _requestOpen() {
    _bloc.add(const CheckoutOpenRequested());
    _trigger();
  }

  void _payAgain() {
    Navigator.of(context).pushReplacement(
      MaterialPageRoute<void>(
        builder: (_) => PaymentInstructionsScreen(order: widget.order),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return BlocProvider<OrderCheckoutBloc>.value(
      value: _bloc,
      child: BlocConsumer<OrderCheckoutBloc, OrderCheckoutState>(
        listener: _onState,
        builder: (context, state) => Scaffold(
          body: SafeArea(
            bottom: false,
            child: SingleChildScrollView(
              child: FwContentWidth(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    // The board closes the payment with an X, not Back.
                    FwDetailHeader(
                      title: l10n.orderCheckoutTitle,
                      onBack: () => Navigator.of(context).pop(),
                    ),
                    Padding(
                      padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          PaymentAmountCard(
                            amount: state is CheckoutAwaitingPayment
                                ? state.amount
                                : (widget.order.orderTotal ??
                                    widget.order.totalPrice),
                            detail: paymentAmountDetail(widget.order, l10n),
                          ),
                          const SizedBox(height: 14),
                          // Telebirr payment board: how the payment asks the
                          // buyer to confirm it is them, before it asks.
                          if (state is! CheckoutPaid) ...[
                            const ConfirmItsYouCard(),
                            const SizedBox(height: 14),
                          ],
                          _statusCard(context, state, l10n),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
          bottomNavigationBar: FwBottomActions(
            children: [
              if (state is CheckoutAwaitingPayment) ...[
                _OpenTelebirrButton(
                  url: state.toPayUrl,
                  isWeb: _isWeb,
                  label: l10n.escrowOpenPaymentAgain,
                  onOpen: () => _openAgain(state.toPayUrl),
                ),
                const SizedBox(height: 6),
              ],
              TextButton(
                onPressed: () => Navigator.of(context).pop(),
                style: TextButton.styleFrom(
                  minimumSize: const Size.fromHeight(44),
                  foregroundColor: AppTheme.unselectedNav,
                ),
                child: Text(
                  state is CheckoutPaid || state is CheckoutStopped
                      ? l10n.done
                      : l10n.payLater,
                ),
              ),
              if (state is! CheckoutPaid) FwActionNote(l10n.escrowErrorOffline),
            ],
          ),
        ),
      ),
    );
  }

  /// The step the payment is at, as the board's status card: the line, what
  /// to know under it, and the card's own actions.
  Widget _statusCard(
    BuildContext context,
    OrderCheckoutState state,
    AppLocalizations l10n,
  ) {
    final checkAgain = OutlinedButton(
      onPressed: () => _trigger(refreshNow: true),
      style: paymentCardButtonStyle(),
      child: Text(l10n.orderCheckoutCheckAgain),
    );
    final actionStyle = FilledButton.styleFrom(
      minimumSize: const Size.fromHeight(44),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      textStyle: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700),
    );
    final stillWaiting = _capReached && state.polls
        ? Text(l10n.orderCheckoutStillWaiting, style: _noteStyle)
        : null;
    return switch (state) {
      CheckoutChecking() => PaymentStatusCard(
          text: l10n.orderCheckoutCheckingPayment,
          busy: true,
          children: [if (stillWaiting != null) stillWaiting, checkAgain],
        ),
      CheckoutOpening() => PaymentStatusCard(
          text: l10n.escrowPaymentOpening,
          busy: true,
          children: [if (stillWaiting != null) stillWaiting, checkAgain],
        ),
      CheckoutWaitingToSend() => PaymentStatusCard(
          text: l10n.orderCheckoutWaitingToSend,
          busy: true,
          children: [
            if (stillWaiting != null) stillWaiting,
            if (state.stepUpPending)
              FilledButton(
                onPressed: () =>
                    _confirmItsYou(withFayda: state.stepUpWithFayda),
                style: actionStyle,
                child: Text(l10n.stepUpTitle),
              ),
            checkAgain,
          ],
        ),
      CheckoutAwaitingPayment() => PaymentStatusCard(
          text: l10n.escrowAwaitingPayment,
          busy: true,
          children: [
            ClockText(
              clockFormatOf(context).deadline(
                state.payableUntil,
                sentence: l10n.orderCheckoutOpenUntil,
              ),
              style: _noteStyle,
            ),
            if (state.offline)
              Text(
                l10n.escrowErrorOffline,
                style: const TextStyle(color: AppTheme.statusError),
              ),
            if (stillWaiting != null) stillWaiting,
            checkAgain,
          ],
        ),
      CheckoutConfirmingAutomatically() => PaymentStatusCard(
          text: l10n.orderCheckoutConfirmedAutomatically,
          busy: true,
          children: [if (stillWaiting != null) stillWaiting, checkAgain],
        ),
      CheckoutWindowClosed() => PaymentStatusCard(
          text: l10n.orderCheckoutWindowClosed,
          children: [
            FilledButton(
              onPressed: _requestOpen,
              style: actionStyle,
              child: Text(l10n.orderCheckoutStartNew),
            ),
          ],
        ),
      CheckoutProblem() => PaymentStatusCard(
          text: localizeCheckoutIssue(l10n, state.issue),
          busy: true,
          error: true,
          children: [
            if (stillWaiting != null) stillWaiting,
            if (state.canRetry)
              OutlinedButton(
                onPressed: _requestOpen,
                style: paymentCardButtonStyle(),
                child: Text(l10n.retry),
              ),
          ],
        ),
      CheckoutPaid() => PaymentStatusCard(
          text: l10n.escrowPaymentConfirmed,
          done: true,
        ),
      CheckoutStopped() => PaymentStatusCard(
          text: localizeCheckoutIssue(l10n, state.issue),
          error: true,
          children: [
            if (state.issue == CheckoutIssue.paymentFailed)
              FilledButton(
                onPressed: _payAgain,
                style: actionStyle,
                child: Text(l10n.payAgain),
              ),
            if (state.issue == CheckoutIssue.notSent) checkAgain,
          ],
        ),
    };
  }

  static const TextStyle _noteStyle = TextStyle(
    fontSize: 13,
    height: 1.4,
    color: AppTheme.mutedSteel,
  );
}

/// The line under the amount: the crop and weight with where the money
/// sits ("Wheat 1,500 kg · held in escrow until you confirm delivery").
String paymentAmountDetail(Order order, AppLocalizations l10n) {
  final crop = CropType.labelFor(order.cropType, l10n);
  final kg = l10n.quantityKg(wholeNumber(order.quantity));
  return '$crop $kg · ${l10n.checkoutHeldInEscrowNote.toLowerCase()}';
}

/// Opens the Telebirr page. Android: a button that launches it. Web: a real
/// link (`<a target="_blank">`), so the browser opens it as a navigation
/// the buyer clicked and no pop-up blocker can swallow it while the app
/// believes it opened.
class _OpenTelebirrButton extends StatelessWidget {
  final String url;
  final bool isWeb;
  final String label;
  final VoidCallback onOpen;

  const _OpenTelebirrButton({
    required this.url,
    required this.isWeb,
    required this.label,
    required this.onOpen,
  });

  @override
  Widget build(BuildContext context) {
    final style = FilledButton.styleFrom(
      backgroundColor: AppTheme.soilDark,
      minimumSize: const Size.fromHeight(56),
      textStyle: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800),
    );
    final uri = Uri.tryParse(url);
    if (isWeb && uri != null) {
      return Link(
        uri: uri,
        target: LinkTarget.blank,
        builder: (context, followLink) => FilledButton(
          onPressed: followLink,
          style: style,
          child: Text(label),
        ),
      );
    }
    return FilledButton(
      onPressed: onOpen,
      style: style,
      child: Text(label),
    );
  }
}
