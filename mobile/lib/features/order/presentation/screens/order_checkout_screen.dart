import 'dart:async';

import 'package:app_links/app_links.dart';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:url_launcher/link.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/telebirr_launcher.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_bloc.dart';
import 'package:wereda_connect/features/order/presentation/order_checkout_l10n.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';

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
      child: Scaffold(
        appBar: AppBar(title: Text(l10n.orderCheckoutTitle)),
        body: BlocConsumer<OrderCheckoutBloc, OrderCheckoutState>(
          listener: _onState,
          builder: (context, state) => SafeArea(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(24),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _AmountCard(
                    amount: state is CheckoutAwaitingPayment
                        ? state.amount
                        : (widget.order.orderTotal ?? widget.order.totalPrice),
                  ),
                  const SizedBox(height: 24),
                  ..._body(context, state, l10n),
                  if (_capReached && state.polls) ...[
                    const SizedBox(height: 16),
                    Text(l10n.orderCheckoutStillWaiting),
                    const SizedBox(height: 8),
                    OutlinedButton(
                      onPressed: () => _trigger(refreshNow: true),
                      style: OutlinedButton.styleFrom(
                        minimumSize: const Size.fromHeight(48),
                      ),
                      child: Text(l10n.orderCheckoutCheckAgain),
                    ),
                  ],
                  const SizedBox(height: 12),
                  TextButton(
                    onPressed: () => Navigator.of(context).pop(),
                    style: TextButton.styleFrom(
                      minimumSize: const Size.fromHeight(48),
                    ),
                    child: Text(
                      state is CheckoutPaid || state is CheckoutStopped
                          ? l10n.done
                          : l10n.payLater,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  List<Widget> _body(
    BuildContext context,
    OrderCheckoutState state,
    AppLocalizations l10n,
  ) {
    return switch (state) {
      CheckoutChecking() => [
          _Status(text: l10n.orderCheckoutCheckingPayment, busy: true),
        ],
      CheckoutOpening() => [
          _Status(text: l10n.escrowPaymentOpening, busy: true),
        ],
      CheckoutWaitingToSend() => [
          _Status(text: l10n.orderCheckoutWaitingToSend, busy: true),
          if (state.stepUpPending) ...[
            const SizedBox(height: 16),
            FilledButton(
              onPressed: () => _confirmItsYou(withFayda: state.stepUpWithFayda),
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
              child: Text(l10n.stepUpTitle),
            ),
          ],
        ],
      CheckoutAwaitingPayment() => [
          _Status(text: l10n.escrowAwaitingPayment, busy: true),
          const SizedBox(height: 12),
          ClockText(
            clockFormatOf(context).deadline(
              state.payableUntil,
              sentence: l10n.orderCheckoutOpenUntil,
            ),
          ),
          if (state.offline) ...[
            const SizedBox(height: 8),
            Text(
              l10n.escrowErrorOffline,
              style: const TextStyle(color: AppTheme.statusError),
            ),
          ],
          const SizedBox(height: 16),
          _OpenTelebirrButton(
            url: state.toPayUrl,
            isWeb: _isWeb,
            label: l10n.escrowOpenPaymentAgain,
            onOpen: () => _openAgain(state.toPayUrl),
          ),
        ],
      CheckoutConfirmingAutomatically() => [
          _Status(text: l10n.orderCheckoutConfirmedAutomatically, busy: true),
        ],
      CheckoutWindowClosed() => [
          _Status(text: l10n.orderCheckoutWindowClosed),
          const SizedBox(height: 16),
          FilledButton(
            onPressed: _requestOpen,
            style: FilledButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
            ),
            child: Text(l10n.orderCheckoutStartNew),
          ),
        ],
      CheckoutProblem() => [
          _Status(
            text: localizeCheckoutIssue(l10n, state.issue),
            busy: true,
            error: true,
          ),
          if (state.canRetry) ...[
            const SizedBox(height: 16),
            OutlinedButton(
              onPressed: _requestOpen,
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
              child: Text(l10n.retry),
            ),
          ],
        ],
      CheckoutPaid() => [
          _Status(text: l10n.escrowPaymentConfirmed, done: true),
        ],
      CheckoutStopped() => [
          _Status(
            text: localizeCheckoutIssue(l10n, state.issue),
            error: true,
          ),
          if (state.issue == CheckoutIssue.paymentFailed) ...[
            const SizedBox(height: 16),
            FilledButton(
              onPressed: _payAgain,
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
              child: Text(l10n.payAgain),
            ),
          ],
          if (state.issue == CheckoutIssue.notSent) ...[
            const SizedBox(height: 16),
            OutlinedButton(
              onPressed: () => _trigger(refreshNow: true),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
              child: Text(l10n.orderCheckoutCheckAgain),
            ),
          ],
        ],
    };
  }
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
      minimumSize: const Size.fromHeight(48),
    );
    final uri = Uri.tryParse(url);
    if (isWeb && uri != null) {
      return Link(
        uri: uri,
        target: LinkTarget.blank,
        builder: (context, followLink) => FilledButton.icon(
          onPressed: followLink,
          style: style,
          icon: const Icon(Icons.open_in_new, size: 18),
          label: Text(label),
        ),
      );
    }
    return FilledButton.icon(
      onPressed: onOpen,
      style: style,
      icon: const Icon(Icons.open_in_new, size: 18),
      label: Text(label),
    );
  }
}

class _AmountCard extends StatelessWidget {
  final double amount;

  const _AmountCard({required this.amount});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 20),
      decoration: BoxDecoration(
        color: AppTheme.fieldGreen,
        borderRadius: BorderRadius.circular(24),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.paymentAmount,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w700,
              color: Color(0xFFCFE3D6),
            ),
          ),
          const SizedBox(height: 6),
          Text(
            l10n.etbCurrency(amount.toStringAsFixed(2)),
            style: const TextStyle(
              fontSize: 32,
              fontWeight: FontWeight.w800,
              letterSpacing: -0.5,
              color: Colors.white,
            ),
          ),
          const SizedBox(height: 4),
          Text(
            l10n.checkoutHeldInEscrowNote,
            style: const TextStyle(
              fontSize: 13,
              color: Color(0xFFCFE3D6),
            ),
          ),
        ],
      ),
    );
  }
}

/// The current step, announced to screen readers as it changes.
class _Status extends StatelessWidget {
  final String text;
  final bool busy;
  final bool error;
  final bool done;

  const _Status({
    required this.text,
    this.busy = false,
    this.error = false,
    this.done = false,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final Widget leading;
    if (busy) {
      leading = const SizedBox(
        height: 20,
        width: 20,
        child: CircularProgressIndicator(strokeWidth: 2),
      );
    } else if (done) {
      leading = const Icon(Icons.check_circle, color: AppTheme.statusSuccess);
    } else {
      leading = Icon(
        error ? Icons.error_outline : Icons.info_outline,
        color: error ? AppTheme.statusError : theme.colorScheme.primary,
      );
    }
    return Semantics(
      liveRegion: true,
      child: Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          color: theme.colorScheme.primaryContainer.withValues(alpha: 0.3),
          borderRadius: BorderRadius.circular(12),
        ),
        child: Row(
          children: [
            leading,
            const SizedBox(width: 16),
            Expanded(
              child: Text(text, style: theme.textTheme.bodyMedium),
            ),
          ],
        ),
      ),
    );
  }
}
