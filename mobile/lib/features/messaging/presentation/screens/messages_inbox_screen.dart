import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

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

/// The conversations inbox — the current user's chats across all roles.
///
/// Drawn in the Fieldway boards' language (no board of its own): the page
/// header on the chalk ground, the threads in one white card. One
/// composition at every width, centred by [FwContentWidth] on wide windows.
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

    final header = FwPageHeader(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
      title: l10n.messages,
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
                          padding: EdgeInsets.zero,
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

  String get _title => (summary.counterpartName?.trim().isNotEmpty ?? false)
      ? summary.counterpartName!.trim()
      : summary.counterpartId;

  String get _initials {
    final name = _title.trim();
    if (name.isEmpty) return '?';
    final parts = name.split(RegExp(r'\s+'));
    if (parts.length == 1) return parts.first.characters.first.toUpperCase();
    return (parts.first.characters.first + parts.last.characters.first)
        .toUpperCase();
  }

  @override
  Widget build(BuildContext context) {
    final time = clockFormatOf(context).time(summary.lastMessageAt);
    final hasUnread = summary.unreadCount > 0;

    return InkWell(
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            CircleAvatar(
              radius: 22,
              backgroundColor: AppTheme.fieldGreenTint,
              child: Text(
                _initials,
                style: const TextStyle(
                  fontSize: 15,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.fieldGreen,
                ),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    _title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 15,
                      fontWeight: hasUnread ? FontWeight.w800 : FontWeight.w700,
                      color: AppTheme.soilDark,
                    ),
                  ),
                  const SizedBox(height: 3),
                  // What the thread is about — the linked crop or trip.
                  ConversationContextTag(
                    contextType: summary.contextType,
                    contextId: summary.contextId,
                    resolver: resolver,
                    color: AppTheme.fieldGreen,
                  ),
                  const SizedBox(height: 3),
                  Text(
                    summary.lastMessageBody,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: hasUnread ? FontWeight.w600 : FontWeight.w500,
                      color: hasUnread
                          ? AppTheme.inkSecondary
                          : AppTheme.mutedSteel,
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
                    fontSize: 12,
                    fontWeight: FontWeight.w500,
                    color: AppTheme.mutedSteel,
                  ),
                ),
                if (hasUnread) ...[
                  const SizedBox(height: 6),
                  Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 7,
                      vertical: 2,
                    ),
                    decoration: BoxDecoration(
                      color: AppTheme.fieldGreen,
                      borderRadius: BorderRadius.circular(999),
                    ),
                    child: Text(
                      '${summary.unreadCount}',
                      style: const TextStyle(
                        color: Colors.white,
                        fontSize: 11,
                        fontWeight: FontWeight.w700,
                      ),
                    ),
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

class _EmptyInbox extends StatelessWidget {
  final String title;
  final String hint;

  const _EmptyInbox({required this.title, required this.hint});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 64,
              height: 64,
              decoration: BoxDecoration(
                color: AppTheme.fieldGreenTint,
                borderRadius: BorderRadius.circular(20),
              ),
              child: const Icon(
                Icons.forum_outlined,
                size: 30,
                color: AppTheme.fieldGreen,
              ),
            ),
            const SizedBox(height: 16),
            Text(title, style: FwText.cardTitle, textAlign: TextAlign.center),
            const SizedBox(height: 8),
            Text(hint, textAlign: TextAlign.center, style: FwText.body),
          ],
        ),
      ),
    );
  }
}
