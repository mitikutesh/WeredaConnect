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
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

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
            // Stored on the local row until the server stamps the real
            // name; an id is never kept as one (FUP-130).
            recipientName: switch (partyNameToStore(widget.counterpartName)) {
              '' => null,
              final name => name,
            },
            body: text,
          ),
        );
    _controller.clear();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    // No name, or an id where one belongs: a neutral noun, never the id
    // (FUP-130).
    final title = partyNameOrUnnamed(widget.counterpartName, l10n);

    // The boards' detail header in place of an app bar: the round back
    // button, the name with the linked crop or trip under it, and Call as
    // the black round button of the order boards' contact rows.
    final header = Padding(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 10),
      child: Row(
        children: [
          const FwBackButton(),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Semantics(
                  header: true,
                  child: Text(
                    title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontSize: 19,
                      fontWeight: FontWeight.w800,
                      letterSpacing: -0.4,
                      color: AppTheme.ink,
                    ),
                  ),
                ),
                // The crop or trip this thread is linked to; tap to open it.
                ConversationContextTag(
                  contextType: widget.contextType,
                  contextId: widget.contextId,
                  resolver: context.read<InboxBloc>().contextResolver,
                  color: _contextOpensDetail
                      ? context.roleColors.primary
                      : AppTheme.slate,
                  fontSize: 13,
                  onTap: _contextOpensDetail ? _openLinkedContext : null,
                ),
              ],
            ),
          ),
          if (_contact.open == true && _contact.phone != null) ...[
            const SizedBox(width: 8),
            Tooltip(
              message: l10n.contact,
              child: FwSquareButton(
                background: AppTheme.ink,
                semanticLabel: l10n.contact,
                onTap: () => showContactActionsSheet(
                  context,
                  name: title,
                  roleLabel: l10n.contact,
                  phone: _contact.phone,
                ),
                child: const Icon(
                  Icons.call_outlined,
                  size: 18,
                  color: Colors.white,
                ),
              ),
            ),
          ],
        ],
      ),
    );

    return Scaffold(
      body: SafeArea(
        bottom: false,
        child: FwContentWidth(
          child: Column(
            children: [
              header,
              const Divider(height: 1),
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
                        padding: const EdgeInsets.fromLTRB(20, 12, 20, 12),
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
                    padding: const EdgeInsets.fromLTRB(20, 12, 20, 12),
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
        ),
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
    final clockTime = clockFormatOf(context).time(time);
    // Mine: black with white ink, at the end. Theirs: the paper grey, at
    // the start. The corner by the speaker is the small one.
    const big = Radius.circular(20);
    const small = Radius.circular(6);

    return Align(
      alignment: isMine
          ? AlignmentDirectional.centerEnd
          : AlignmentDirectional.centerStart,
      child: Container(
        margin: const EdgeInsets.symmetric(vertical: 4),
        padding: const EdgeInsets.fromLTRB(14, 10, 14, 9),
        constraints: BoxConstraints(
          maxWidth: MediaQuery.of(context).size.width * 0.78,
        ),
        decoration: BoxDecoration(
          color: isMine ? AppTheme.ink : AppTheme.paper,
          borderRadius: BorderRadiusDirectional.only(
            topStart: big,
            topEnd: big,
            bottomStart: isMine ? big : small,
            bottomEnd: isMine ? small : big,
          ),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              body,
              style: TextStyle(
                fontSize: 15,
                height: 1.4,
                color: isMine ? Colors.white : AppTheme.ink,
              ),
            ),
            const SizedBox(height: 3),
            Text(
              clockTime,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: isMine ? AppTheme.inkMuted : AppTheme.slate,
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
        padding: const EdgeInsets.fromLTRB(20, 8, 20, 12),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Expanded(
              child: TextField(
                controller: controller,
                minLines: 1,
                maxLines: 4,
                textInputAction: TextInputAction.send,
                onSubmitted: (_) => onSend(),
                // The theme's field: paper fill, radius 16.
                decoration: InputDecoration(hintText: hintText),
              ),
            ),
            const SizedBox(width: 8),
            // The screen's one action: the orange round button.
            IconButton.filled(
              icon: const Icon(Icons.send, size: 20),
              tooltip: sendLabel,
              onPressed: onSend,
              style: IconButton.styleFrom(
                backgroundColor: AppTheme.orange,
                foregroundColor: AppTheme.ink,
                minimumSize: const Size(52, 52),
              ),
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
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.forum_outlined, size: 48, color: AppTheme.sketch),
            const SizedBox(height: 16),
            Text(
              text,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 15,
                height: 1.45,
                color: AppTheme.slate,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
