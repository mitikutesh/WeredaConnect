import 'dart:async';

import 'package:app_links/app_links.dart';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:url_launcher/link.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/telebirr_launcher.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_bloc.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
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
/// The PaymentM board, with its two full states: PaymentFailedM when the
/// order's payment failed, PaymentOfflineM when the step needs a network
/// the device does not have.
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

  /// Test seam: whether the device has a network, as it changes. Null
  /// reads the app's [ConnectivityMonitor] (mobile; web has none and is
  /// taken to be online until a request fails).
  final Stream<bool>? online;

  /// The order detail is the page under this one, so closing returns to the
  /// order. False when the buyer came straight from Place order: Done on a
  /// paid order then opens the order's detail in this screen's place
  /// (BUG-21).
  final bool fromOrderDetail;

  /// How the buyer confirmed it is them on the page before this one (the
  /// password or Fayda prompt of the first submit); null when they were
  /// not asked.
  final StepUpMethod? confirmedWith;

  /// Test seam: the bus a step-up confirmed elsewhere (the background
  /// prompt of a queued payment) is heard on. Null is the app's.
  final SecurityEvents? securityEvents;

  const OrderCheckoutScreen({
    required this.order,
    this.autoOpen = true,
    this.fromOrderDetail = true,
    this.confirmedWith,
    this.securityEvents,
    this.bloc,
    this.launcher,
    this.returnLinks,
    this.isWeb,
    this.connectivityRestored,
    this.verificationScreen,
    this.online,
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
  StreamSubscription<bool>? _onlineSub;
  StreamSubscription<void>? _stepUpSub;

  /// How the buyer confirmed it is them for this payment, once they did:
  /// the board's "Confirmed with Fayda" pill. Never named before, and gone
  /// again when a queued payment asks once more.
  StepUpMethod? _confirmedWith;

  /// The device has no network (as far as it can tell). Only decides which
  /// state is drawn while a payment waits on the phone; nothing is sent or
  /// held back because of it.
  bool _deviceOffline = false;

  @override
  void initState() {
    super.initState();
    _isWeb = widget.isWeb ?? kIsWeb;
    _ownsBloc = widget.bloc == null;
    _bloc = widget.bloc ?? _createBloc();
    WidgetsBinding.instance.addObserver(this);
    TelebirrReturnWatchers.add();
    _confirmedWith = widget.confirmedWith;
    final securityEvents = widget.securityEvents ?? SecurityEvents.instance;
    // Also a prompt this screen did not open (the shell's, for a queued
    // payment) while the checkout shows.
    _stepUpSub = securityEvents.onStepUpCompleted.listen((_) {
      final method = securityEvents.lastStepUpMethod;
      if (mounted && method != null) setState(() => _confirmedWith = method);
    });
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
    _onlineSub = (widget.online ?? _networkState()).listen(
      (online) {
        if (mounted && _deviceOffline == online) {
          setState(() => _deviceOffline = !online);
        }
      },
      onError: (Object _) {},
    );
    _bloc.add(CheckoutStarted(autoOpen: widget.autoOpen));
    _trigger();
  }

  /// Mobile: the network state now, then each change. Web has no monitor.
  Stream<bool> _networkState() async* {
    final monitor = _read<ConnectivityMonitor>();
    if (monitor == null) return;
    try {
      yield await monitor.isConnected();
    } on Object {
      // Unknown: taken to be online; a failed request says otherwise.
    }
    yield* monitor.onConnectivityChanged.map(monitor.isConnectedFromResults);
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
    unawaited(_onlineSub?.cancel());
    unawaited(_stepUpSub?.cancel());
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
    // The queued payment asks again: the earlier confirmation is spent.
    if (state is CheckoutWaitingToSend &&
        state.stepUpPending &&
        _confirmedWith != null) {
      setState(() => _confirmedWith = null);
    }
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
    final securityEvents = widget.securityEvents;
    // A completed prompt reports its method on the bus ([_stepUpSub]).
    if (withFayda) {
      await showFaydaStepUpDialog(
        context,
        securityEvents: securityEvents,
        verificationScreen: widget.verificationScreen,
      );
    } else {
      await showStepUpDialog(context, securityEvents: securityEvents);
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
        builder: (_) => PaymentInstructionsScreen(
          order: widget.order,
          fromOrderDetail: widget.fromOrderDetail,
        ),
      ),
    );
  }

  /// Leave the payment for the order: back to its detail when that is the
  /// page underneath, else the detail in this screen's place.
  void _backToOrder() {
    if (widget.fromOrderDetail) {
      Navigator.of(context).pop();
    } else {
      replaceWithOrderDetail(context, widget.order.orderId);
    }
  }

  /// The full state that takes the page instead of the cards, if any:
  /// - the order's payment failed (PaymentFailedM): pay again or later
  /// - the checkout request did not reach the server, or the payment still
  ///   waits on a phone with no network (PaymentOfflineM)
  Widget? _fullState(OrderCheckoutState state, AppLocalizations l10n) {
    if (state is CheckoutStopped &&
        state.issue == CheckoutIssue.paymentFailed) {
      return PaymentStateView.failed(
        l10n: l10n,
        onTryAgain: _payAgain,
        onPayLater: () => Navigator.of(context).pop(),
      );
    }
    if (state is CheckoutProblem && state.issue == CheckoutIssue.offline) {
      return PaymentStateView.offline(
        l10n: l10n,
        onTryAgain: _requestOpen,
        onBackToOrder: _backToOrder,
      );
    }
    // A queued payment that asks for a step-up keeps its card: the buyer
    // has something to do there that needs no network to start.
    if (state is CheckoutWaitingToSend &&
        _deviceOffline &&
        !state.stepUpPending) {
      return PaymentStateView.offline(
        l10n: l10n,
        onTryAgain: () => _trigger(refreshNow: true),
        onBackToOrder: _backToOrder,
      );
    }
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return BlocProvider<OrderCheckoutBloc>.value(
      value: _bloc,
      child: BlocConsumer<OrderCheckoutBloc, OrderCheckoutState>(
        listener: _onState,
        builder: (context, state) {
          final fullState = _fullState(state, l10n);
          if (fullState != null) {
            return Scaffold(body: SafeArea(child: fullState));
          }
          return _page(context, state, l10n);
        },
      ),
    );
  }

  Widget _page(
    BuildContext context,
    OrderCheckoutState state,
    AppLocalizations l10n,
  ) {
    return Scaffold(
      body: SafeArea(
        bottom: false,
        child: SingleChildScrollView(
          child: FwContentWidth(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                // The board closes the payment with an X, not Back.
                PaymentHeader(
                  title: l10n.orderCheckoutTitle,
                  onClose: () => Navigator.of(context).pop(),
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(20, 20, 20, 24),
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
                      const SizedBox(height: 18),
                      // Telebirr payment board: how the payment asks the
                      // buyer to confirm it is them, before it asks.
                      if (state is! CheckoutPaid) ...[
                        ConfirmItsYouCard(confirmedWith: _confirmedWith),
                        const SizedBox(height: 18),
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
            const SizedBox(height: 8),
          ],
          OutlinedButton(
            onPressed: () {
              if (state is CheckoutPaid && !widget.fromOrderDetail) {
                replaceWithOrderDetail(context, widget.order.orderId);
              } else {
                Navigator.of(context).pop();
              }
            },
            style: paymentSecondaryButtonStyle(),
            child: Text(
              state is CheckoutPaid || state is CheckoutStopped
                  ? l10n.done
                  : l10n.payLater,
            ),
          ),
          if (state is! CheckoutPaid) FwActionNote(l10n.escrowErrorOffline),
        ],
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
    // The card's own main action: a black pill, the second strongest
    // button (the orange one is the page's).
    final actionStyle = FilledButton.styleFrom(
      minimumSize: const Size.fromHeight(44),
      backgroundColor: AppTheme.ink,
      foregroundColor: Colors.white,
      textStyle: const TextStyle(
        fontFamily: AppTheme.fontFamily,
        fontFamilyFallback: AppTheme.ethiopicFontFallback,
        fontSize: 14,
        fontWeight: FontWeight.w700,
      ),
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
            // What the buyer has to do, and what happens to the money.
            Text(
              l10n.orderCheckoutApproveNote,
              style: const TextStyle(
                fontSize: 14,
                height: 1.45,
                color: AppTheme.ink,
              ),
            ),
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
    color: AppTheme.slate,
  );
}

/// The line under the amount: the crop and weight with where the money
/// sits ("Wheat 1,500 kg · held in escrow until you confirm delivery").
String paymentAmountDetail(Order order, AppLocalizations l10n) {
  final crop = CropType.labelFor(order.cropType, l10n);
  final kg = kgQuantityText(order.quantity, l10n);
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
    // The page's one orange action (the theme's button).
    final uri = Uri.tryParse(url);
    if (isWeb && uri != null) {
      return Link(
        uri: uri,
        target: LinkTarget.blank,
        builder: (context, followLink) => FilledButton(
          onPressed: followLink,
          child: Text(label),
        ),
      );
    }
    return FilledButton(
      onPressed: onOpen,
      child: Text(label),
    );
  }
}
