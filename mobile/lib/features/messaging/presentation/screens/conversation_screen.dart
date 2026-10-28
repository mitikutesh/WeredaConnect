import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/conversation_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/conversation_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/conversation_state.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/widgets/conversation_context_tag.dart';
import 'package:wereda_connect/features/messaging/presentation/widgets/message_arrival_listener.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/trip_detail_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/contact_actions_sheet.dart';
import 'package:wereda_connect/shared/widgets/contact_locked_note.dart';

/// A single conversation thread, scoped to a marketplace context
/// (crop / order / trip / delivery). Creates its own [ConversationBloc] from the
/// shared repository so its state never leaks into sibling screens.
///
/// ESC-48: the thread's contact comes from its order (the context resolver):
/// Call shows only while the order is open and carries the counterpart's
/// number, and a closed thread (every crop or trip thread, an order before the
/// farmer accepted it or after the money went back) is read-only with a note.
class ConversationScreen extends StatelessWidget {
  final String contextType;
  final String contextId;
  final String counterpartId;
  final String? counterpartName;

  const ConversationScreen({
    required this.contextType,
    required this.contextId,
    required this.counterpartId,
    this.counterpartName,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final authState = context.read<AuthBloc>().state;
    final me = authState is AuthAuthenticated ? authState.userId : '';
    final myName = authState is AuthAuthenticated ? authState.userName : '';

    final conversationId = BaseMessageRepository.conversationIdFor(
      contextType: contextType,
      contextId: contextId,
      userA: me,
      userB: counterpartId,
    );

    return BlocProvider<ConversationBloc>(
      create: (ctx) => ConversationBloc(
        repository: ctx.read<InboxBloc>().repository,
      )..add(LoadThread(conversationId)),
      child: _ConversationView(
        conversationId: conversationId,
        contextType: contextType,
        contextId: contextId,
        me: me,
        myName: myName,
        counterpartId: counterpartId,
        counterpartName: counterpartName,
      ),
    );
  }
}

class _ConversationView extends StatefulWidget {
  final String conversationId;
  final String contextType;
  final String contextId;
  final String me;
  final String myName;
  final String counterpartId;
  final String? counterpartName;

  const _ConversationView({
    required this.conversationId,
    required this.contextType,
    required this.contextId,
    required this.me,
    required this.myName,
    required this.counterpartId,
    required this.counterpartName,
  });

  @override
  State<_ConversationView> createState() => _ConversationViewState();
}

class _ConversationViewState extends State<_ConversationView> {
  final _controller = TextEditingController();
  final _scrollController = ScrollController();

  /// ESC-48: unknown until resolved; never cached across screens.
  ConversationContact _contact = ConversationContact.unknown;

  @override
  void initState() {
    super.initState();
    // Suppress arrival banners for the thread the user is looking at.
    ActiveConversation.id = widget.conversationId;
    _loadContact();
  }

  Future<void> _loadContact() async {
    final resolver = context.read<InboxBloc>().contextResolver;
    if (resolver == null) return;
    final contact = await resolver.resolveContact(
      widget.contextType,
      widget.contextId,
      widget.counterpartId,
    );
    if (!mounted) return;
    setState(() => _contact = contact);
  }

  @override
  void dispose() {
    if (ActiveConversation.id == widget.conversationId) {
      ActiveConversation.id = null;
    }
    _controller.dispose();
    _scrollController.dispose();
    super.dispose();
  }

  /// Only crop and trip threads have a detail screen to open; order/delivery
  /// tags stay label-only for now.
  bool get _contextOpensDetail =>
      widget.contextType == 'crop' || widget.contextType == 'trip';

  /// Open the crop/trip this thread is linked to. Loads the entity through
  /// the marketplace repositories (Isar on mobile, HTTP on web) — the detail
  /// screens need the full model, not just the tag's label info.
  Future<void> _openLinkedContext() async {
    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;
    final l10n = AppLocalizations.of(context)!;
    final messenger = ScaffoldMessenger.of(context);

    Widget? screen;
    switch (widget.contextType) {
      case 'crop':
        final listing = await context
            .read<CropListingBloc>()
            .repository
            .getListingById(widget.contextId);
        if (listing != null) {
          screen = CropListingDetailScreen(
            listing: listing,
            currentUserId: authState.userId,
            currentUserRole: authState.role,
          );
        }
      case 'trip':
        final trip = await context
            .read<TripListingBloc>()
            .repository
            .getTripById(widget.contextId);
        if (trip != null) screen = TripDetailScreen(trip: trip);
    }

    if (!mounted) return;
    if (screen == null) {
      // Not synced locally / not visible to this role / offline on web.
      messenger.showSnackBar(SnackBar(content: Text(l10n.contextUnavailable)));
      return;
    }
    final target = screen;
    await Navigator.push(
      context,
      MaterialPageRoute<void>(builder: (_) => target),
    );
  }

  void _send() {
    final text = _controller.text.trim();
    if (text.isEmpty) return;
    context.read<ConversationBloc>().add(
          SendMessageRequested(
            contextType: widget.contextType,
            contextId: widget.contextId,
            senderId: widget.me,
            senderName: widget.myName,
            recipientId: widget.counterpartId,
            recipientName: widget.counterpartName,
            body: text,
          ),
        );
    _controller.clear();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final title = (widget.counterpartName?.trim().isNotEmpty ?? false)
        ? partyDisplayName(widget.counterpartName!.trim(), l10n)
        : widget.counterpartId;

    return Scaffold(
      appBar: AppBar(
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(title, maxLines: 1, overflow: TextOverflow.ellipsis),
            // The crop/trip this thread is linked to — tap to open it.
            ConversationContextTag(
              contextType: widget.contextType,
              contextId: widget.contextId,
              resolver: context.read<InboxBloc>().contextResolver,
              color: Colors.white70,
              onTap: _contextOpensDetail ? _openLinkedContext : null,
            ),
          ],
        ),
        actions: [
          if (_contact.open == true && _contact.phone != null)
            IconButton(
              icon: const Icon(Icons.call_outlined),
              tooltip: l10n.contact,
              onPressed: () => showContactActionsSheet(
                context,
                name: title,
                roleLabel: l10n.contact,
                phone: _contact.phone,
              ),
            ),
        ],
      ),
      body: Column(
        children: [
          Expanded(
            child: BlocConsumer<ConversationBloc, ConversationState>(
              listener: (context, state) {
                if (state is ConversationLoaded) {
                  WidgetsBinding.instance.addPostFrameCallback((_) {
                    if (_scrollController.hasClients) {
                      _scrollController.jumpTo(
                        _scrollController.position.maxScrollExtent,
                      );
                    }
                  });
                }
              },
              builder: (context, state) {
                if (state is ConversationLoaded) {
                  if (state.messages.isEmpty) {
                    return _EmptyThread(text: l10n.noMessagesInThread);
                  }
                  return ListView.builder(
                    controller: _scrollController,
                    padding: const EdgeInsets.all(12),
                    itemCount: state.messages.length,
                    itemBuilder: (context, i) {
                      final m = state.messages[i];
                      return _MessageBubble(
                        body: m.body,
                        time: m.createdAt,
                        isMine: m.senderId == widget.me,
                      );
                    },
                  );
                }
                if (state is ConversationError) {
                  return _EmptyThread(text: state.message);
                }
                return const Center(child: CircularProgressIndicator());
              },
            ),
          ),
          if (_contact.open == false)
            SafeArea(
              top: false,
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
                child: ContactLockedNote(
                  text: switch (_contact.closedReason) {
                    ContactClosedReason.beforeAccept =>
                      l10n.contactAfterAcceptNote,
                    ContactClosedReason.closed => l10n.contactClosedNote,
                    _ => l10n.contactAfterEscrowNote,
                  },
                ),
              ),
            )
          else
            _Composer(
              controller: _controller,
              hintText: l10n.typeAMessage,
              sendLabel: l10n.send,
              onSend: _send,
            ),
        ],
      ),
    );
  }
}

class _MessageBubble extends StatelessWidget {
  final String body;
  final DateTime time;
  final bool isMine;

  const _MessageBubble({
    required this.body,
    required this.time,
    required this.isMine,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final clockTime = clockFormatOf(context).time(time);

    return Align(
      alignment: isMine ? Alignment.centerRight : Alignment.centerLeft,
      child: Container(
        margin: const EdgeInsets.symmetric(vertical: 4),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
        constraints: BoxConstraints(
          maxWidth: MediaQuery.of(context).size.width * 0.78,
        ),
        decoration: BoxDecoration(
          color: isMine
              ? AppTheme.primary
              : theme.colorScheme.surfaceContainerHighest,
          borderRadius: BorderRadius.only(
            topLeft: const Radius.circular(14),
            topRight: const Radius.circular(14),
            bottomLeft: Radius.circular(isMine ? 14 : 4),
            bottomRight: Radius.circular(isMine ? 4 : 14),
          ),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              body,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: isMine ? Colors.white : theme.colorScheme.onSurface,
              ),
            ),
            const SizedBox(height: 2),
            Text(
              clockTime,
              style: theme.textTheme.labelSmall?.copyWith(
                color: (isMine ? Colors.white : theme.colorScheme.onSurface)
                    .withValues(alpha: 0.6),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _Composer extends StatelessWidget {
  final TextEditingController controller;
  final String hintText;
  final String sendLabel;
  final VoidCallback onSend;

  const _Composer({
    required this.controller,
    required this.hintText,
    required this.sendLabel,
    required this.onSend,
  });

  @override
  Widget build(BuildContext context) {
    return SafeArea(
      top: false,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
        child: Row(
          children: [
            Expanded(
              child: TextField(
                controller: controller,
                minLines: 1,
                maxLines: 4,
                textInputAction: TextInputAction.send,
                onSubmitted: (_) => onSend(),
                decoration: InputDecoration(
                  hintText: hintText,
                  border: const OutlineInputBorder(),
                  isDense: true,
                ),
              ),
            ),
            const SizedBox(width: 8),
            IconButton.filled(
              icon: const Icon(Icons.send),
              tooltip: sendLabel,
              onPressed: onSend,
            ),
          ],
        ),
      ),
    );
  }
}

class _EmptyThread extends StatelessWidget {
  final String text;

  const _EmptyThread({required this.text});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.forum_outlined,
              size: 64,
              color: theme.colorScheme.onSurface.withValues(alpha: 0.3),
            ),
            const SizedBox(height: 16),
            Text(
              text,
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
