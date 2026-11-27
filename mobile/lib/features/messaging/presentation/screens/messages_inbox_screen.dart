import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/conversation_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/widgets/conversation_context_tag.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// The conversations inbox: the current user's chats across all roles.
///
/// Drawn in the boards' language (no board of its own): the 42 px page
/// header with the role's sketch, the threads as rows of one raised paper
/// card (the Contact card of the order boards), an unread count as the
/// orange pill. One composition at every width, centred by
/// [FwContentWidth] on wide windows.
class MessagesInboxScreen extends StatefulWidget {
  const MessagesInboxScreen({super.key});

  @override
  State<MessagesInboxScreen> createState() => _MessagesInboxScreenState();
}

class _MessagesInboxScreenState extends State<MessagesInboxScreen> {
  @override
  void initState() {
    super.initState();
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      context.read<InboxBloc>().add(LoadInbox(authState.userId));
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final authState = context.read<AuthBloc>().state;
    final me = authState is AuthAuthenticated ? authState.userId : '';
    final role = authState is AuthAuthenticated ? authState.role : null;

    // Pushed from the account sheet on the phone; a route of its own on web.
    final header = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (Navigator.of(context).canPop())
          const Padding(
            padding: EdgeInsets.fromLTRB(20, 16, 20, 0),
            child: FwBackButton(),
          ),
        FwPageHeader(
          padding: const EdgeInsets.fromLTRB(20, 14, 20, 16),
          eyebrow: fwPersonPlaceEyebrow(authState, ''),
          title: l10n.messages,
          sketchRole: role,
        ),
      ],
    );

    return Scaffold(
      body: SafeArea(
        child: FwContentWidth(
          child: BlocBuilder<InboxBloc, InboxState>(
            builder: (context, state) {
              if (state is InboxLoaded) {
                if (state.conversations.isEmpty) {
                  return Column(
                    children: [
                      header,
                      Expanded(
                        child: _EmptyInbox(
                          title: l10n.noConversationsYet,
                          hint: l10n.noConversationsHint,
                          role: role,
                        ),
                      ),
                    ],
                  );
                }
                return RefreshIndicator(
                  onRefresh: () async =>
                      context.read<InboxBloc>().add(LoadInbox(me)),
                  child: ListView(
                    padding: const EdgeInsets.only(bottom: 32),
                    children: [
                      header,
                      Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 20),
                        child: FwCard(
                          raised: true,
                          padding: const EdgeInsets.symmetric(vertical: 4),
                          child: Column(
                            children: [
                              for (var i = 0;
                                  i < state.conversations.length;
                                  i++) ...[
                                if (i > 0)
                                  const Divider(indent: 16, endIndent: 16),
                                _ConversationTile(
                                  summary: state.conversations[i],
                                  resolver:
                                      context.read<InboxBloc>().contextResolver,
                                  onTap: () {
                                    final c = state.conversations[i];
                                    Navigator.push(
                                      context,
                                      MaterialPageRoute<void>(
                                        builder: (_) => ConversationScreen(
                                          contextType: c.contextType,
                                          contextId: c.contextId,
                                          counterpartId: c.counterpartId,
                                          counterpartName: c.counterpartName,
                                        ),
                                      ),
                                    );
                                  },
                                ),
                              ],
                            ],
                          ),
                        ),
                      ),
                    ],
                  ),
                );
              }
              if (state is InboxError) {
                return Column(
                  children: [
                    header,
                    Expanded(
                      child: _EmptyInbox(
                        title: l10n.messages,
                        hint: state.message,
                        role: role,
                      ),
                    ),
                  ],
                );
              }
              return Column(
                children: [
                  header,
                  const Expanded(
                    child: Center(child: CircularProgressIndicator()),
                  ),
                ],
              );
            },
          ),
        ),
      ),
    );
  }
}

class _ConversationTile extends StatelessWidget {
  final ConversationSummary summary;
  final ConversationContextResolver? resolver;
  final VoidCallback onTap;

  const _ConversationTile({
    required this.summary,
    required this.onTap,
    this.resolver,
  });

  /// The counterpart's initials; `?` without a real name (FUP-130: the
  /// id is never shown, and the "User" fallback has no initials).
  String _initials(AppLocalizations l10n) {
    final name = partyDisplayName(summary.counterpartName, l10n).trim();
    if (name.isEmpty) return '?';
    final parts = name.split(RegExp(r'\s+'));
    if (parts.length == 1) return parts.first.characters.first.toUpperCase();
    return (parts.first.characters.first + parts.last.characters.first)
        .toUpperCase();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final time = clockFormatOf(context).time(summary.lastMessageAt);
    final hasUnread = summary.unreadCount > 0;

    return InkWell(
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            ExcludeSemantics(
              child: Container(
                width: 44,
                height: 44,
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: context.roleColors.tint,
                  shape: BoxShape.circle,
                ),
                child: Text(
                  _initials(l10n),
                  style: TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w800,
                    color: context.roleColors.primary,
                  ),
                ),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    partyNameOrUnnamed(summary.counterpartName, l10n),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 15,
                      fontWeight: hasUnread ? FontWeight.w800 : FontWeight.w700,
                      letterSpacing: -0.15,
                      color: AppTheme.ink,
                    ),
                  ),
                  const SizedBox(height: 3),
                  // What the thread is about: the linked crop or trip.
                  ConversationContextTag(
                    contextType: summary.contextType,
                    contextId: summary.contextId,
                    resolver: resolver,
                    color: context.roleColors.primary,
                  ),
                  const SizedBox(height: 3),
                  Text(
                    summary.lastMessageBody,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 14,
                      fontWeight: hasUnread ? FontWeight.w700 : FontWeight.w400,
                      color: hasUnread ? AppTheme.ink : AppTheme.slate,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(width: 10),
            Column(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text(
                  time,
                  style: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                    color: AppTheme.slate,
                  ),
                ),
                if (hasUnread) ...[
                  const SizedBox(height: 6),
                  // Something new: the orange pill, as the bell's dot.
                  FwPill(
                    key: const Key('inbox_unread_pill'),
                    label: '${summary.unreadCount}',
                    background: AppTheme.orange,
                    foreground: AppTheme.ink,
                  ),
                ],
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// The empty and the failed inbox: the role's large line drawing (only an
/// empty state gets one), the title and one line of help.
class _EmptyInbox extends StatelessWidget {
  final String title;
  final String hint;
  final UserRole? role;

  const _EmptyInbox({required this.title, required this.hint, this.role});

  @override
  Widget build(BuildContext context) {
    final role = this.role;
    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (role != null)
              RoleSketch.emptyState(role: role)
            else
              const Icon(
                Icons.forum_outlined,
                size: 48,
                color: AppTheme.sketch,
              ),
            const SizedBox(height: 16),
            Text(title, style: FwText.detailTitle.copyWith(fontSize: 26)),
            const SizedBox(height: 8),
            Text(
              hint,
              style: const TextStyle(
                fontSize: 16,
                height: 1.5,
                color: AppTheme.inkSoft,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
