import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/conversation_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/messages_inbox_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/widgets/conversation_context_tag.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The conversation currently on screen — arrivals for it are already visible
/// in the thread, so the banner suppresses them. Set/cleared by the
/// conversation screen.
class ActiveConversation {
  ActiveConversation._();

  static String? id;
}

/// Shows an in-app heads-up banner when a chat message arrives while the app
/// is open (mobile AND web — background delivery on Android is FCM's job).
///
/// Wraps the app shell; listens to the message repository's inbound stream
/// (fed by the inbox poll / pulls). Tapping the banner opens the thread; a
/// multi-thread batch opens the inbox instead.
class MessageArrivalListener extends StatefulWidget {
  final Widget child;

  const MessageArrivalListener({required this.child, super.key});

  @override
  State<MessageArrivalListener> createState() => _MessageArrivalListenerState();
}

class _MessageArrivalListenerState extends State<MessageArrivalListener>
    with WidgetsBindingObserver {
  StreamSubscription<AuthState>? _authSubscription;
  StreamSubscription<List<Message>>? _subscription;
  OverlayEntry? _banner;
  Timer? _dismissTimer;

  /// Which user the inbox was last bootstrapped for — guards against
  /// re-dispatching [LoadInbox] on every auth re-emission (token refresh).
  String? _inboxLoadedFor;

  static const _autoDismissAfter = Duration(seconds: 6);

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    // Subscribe only while authenticated: reading InboxBloc forces its lazy
    // creation, which touches the local store — and signed-out sessions have
    // no arrivals to announce anyway.
    final authBloc = context.read<AuthBloc>();
    _syncToAuth(authBloc.state);
    _authSubscription = authBloc.stream.listen(_syncToAuth);
  }

  void _syncToAuth(AuthState state) {
    if (state is AuthAuthenticated) {
      _subscription ??= context
          .read<InboxBloc>()
          .repository
          .watchInbound()
          .listen(_onArrivals);
      // Bootstrap the inbox as soon as we're signed in: LoadInbox starts the
      // poll that feeds watchInbound() and the unread badges. Without this,
      // layouts that never mount MessagesBadgeButton (the web/desktop shell)
      // get no arrival banners and no unread counts until the user happens
      // to open the Messages screen.
      if (_inboxLoadedFor != state.userId) {
        _inboxLoadedFor = state.userId;
        context.read<InboxBloc>().add(LoadInbox(state.userId));
      }
    } else {
      // Only touch InboxBloc when a user WAS signed in (we had subscribed) —
      // reading it while signed out forces lazy creation and touches the
      // local store for nothing.
      if (_subscription != null) {
        _subscription?.cancel();
        _subscription = null;
        // Stop the poll timer and drop per-user state; the app-global bloc
        // would otherwise keep polling as the signed-out user forever.
        context.read<InboxBloc>().add(const InboxReset());
      }
      _inboxLoadedFor = null;
      _removeBanner();
    }
  }

  /// Pause the poll while backgrounded/hidden (3G data budget), catch up the
  /// moment the app (or web tab) is visible again. `hidden` covers web tab
  /// switches, where `paused` never fires; `inactive` (permission dialogs,
  /// notification shade) is deliberately ignored.
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (_subscription == null) return; // Not signed in — nothing polls.
    final inbox = context.read<InboxBloc>();
    if (state == AppLifecycleState.resumed) {
      inbox.add(const InboxResumed());
    } else if (state == AppLifecycleState.paused ||
        state == AppLifecycleState.hidden) {
      inbox.add(const InboxPaused());
    }
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _authSubscription?.cancel();
    _subscription?.cancel();
    _removeBanner();
    super.dispose();
  }

  void _onArrivals(List<Message> batch) {
    if (!mounted) return;
    // The open thread already shows its own new messages live.
    final unseen =
        batch.where((m) => m.conversationId != ActiveConversation.id).toList();
    if (unseen.isEmpty) return;
    _showBanner(unseen);
  }

  void _showBanner(List<Message> messages) {
    _removeBanner();

    final overlay = Overlay.maybeOf(context, rootOverlay: true);
    if (overlay == null) return;

    // A gentle nudge on handsets; silently no-ops on web/desktop.
    unawaited(HapticFeedback.mediumImpact());

    _banner = OverlayEntry(
      builder: (_) => _ArrivalBanner(
        messages: messages,
        contextResolver: context.read<InboxBloc>().contextResolver,
        onTap: () => _openTarget(messages),
        onDismiss: _removeBanner,
      ),
    );
    overlay.insert(_banner!);
    _dismissTimer = Timer(_autoDismissAfter, _removeBanner);
  }

  void _openTarget(List<Message> messages) {
    _removeBanner();
    if (!mounted) return;

    final conversationIds = messages.map((m) => m.conversationId).toSet();
    if (conversationIds.length == 1) {
      final m = messages.last;
      Navigator.push(
        context,
        MaterialPageRoute<void>(
          builder: (_) => ConversationScreen(
            contextType: m.contextType,
            contextId: m.contextId,
            counterpartId: m.senderId,
            counterpartName: m.senderName,
            counterpartPhone: m.senderPhone,
          ),
        ),
      );
    } else {
      Navigator.push(
        context,
        MaterialPageRoute<void>(builder: (_) => const MessagesInboxScreen()),
      );
    }
  }

  void _removeBanner() {
    _dismissTimer?.cancel();
    _dismissTimer = null;
    _banner?.remove();
    _banner = null;
  }

  @override
  Widget build(BuildContext context) => widget.child;
}

/// The heads-up card itself: sender, preview, and the linked crop/trip.
class _ArrivalBanner extends StatelessWidget {
  final List<Message> messages;
  final ConversationContextResolver? contextResolver;
  final VoidCallback onTap;
  final VoidCallback onDismiss;

  const _ArrivalBanner({
    required this.messages,
    required this.contextResolver,
    required this.onTap,
    required this.onDismiss,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    final single = messages.map((m) => m.conversationId).toSet().length == 1;
    final last = messages.last;
    final sender = last.senderName?.trim();
    final title = single
        ? (sender != null && sender.isNotEmpty ? sender : l10n.newMessage)
        : l10n.newMessagesCount(messages.length);
    final preview = single
        ? last.body
        : messages.map((m) => m.senderName ?? '').toSet().join(', ');

    return Positioned(
      top: 0,
      left: 0,
      right: 0,
      child: SafeArea(
        child: Align(
          alignment: Alignment.topCenter,
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 480),
            child: Dismissible(
              key: const ValueKey('message-arrival-banner'),
              direction: DismissDirection.up,
              onDismissed: (_) => onDismiss(),
              child: Padding(
                padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
                child: Material(
                  elevation: 6,
                  borderRadius: BorderRadius.circular(14),
                  color: theme.colorScheme.surface,
                  child: InkWell(
                    onTap: onTap,
                    borderRadius: BorderRadius.circular(14),
                    child: Padding(
                      padding: const EdgeInsets.all(12),
                      child: Row(
                        children: [
                          const CircleAvatar(
                            radius: 18,
                            backgroundColor: AppTheme.primary,
                            child: Icon(
                              Icons.chat_bubble_outline,
                              color: Colors.white,
                              size: 18,
                            ),
                          ),
                          const SizedBox(width: 12),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(
                                  title,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: theme.textTheme.titleSmall?.copyWith(
                                    fontWeight: FontWeight.w700,
                                  ),
                                ),
                                const SizedBox(height: 2),
                                Text(
                                  preview,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: theme.textTheme.bodySmall,
                                ),
                                if (single) ...[
                                  const SizedBox(height: 4),
                                  ConversationContextTag(
                                    contextType: last.contextType,
                                    contextId: last.contextId,
                                    resolver: contextResolver,
                                  ),
                                ],
                              ],
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
