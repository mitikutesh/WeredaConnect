import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/conversation_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The conversations inbox — the current user's chats across all roles.
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

    return Scaffold(
      appBar: AppBar(title: Text(l10n.messages)),
      body: BlocBuilder<InboxBloc, InboxState>(
        builder: (context, state) {
          if (state is InboxLoaded) {
            if (state.conversations.isEmpty) {
              return _EmptyInbox(
                title: l10n.noConversationsYet,
                hint: l10n.noConversationsHint,
              );
            }
            return RefreshIndicator(
              onRefresh: () async =>
                  context.read<InboxBloc>().add(LoadInbox(me)),
              child: ListView.separated(
                itemCount: state.conversations.length,
                separatorBuilder: (_, __) => const Divider(height: 1),
                itemBuilder: (context, i) {
                  final c = state.conversations[i];
                  return _ConversationTile(
                    summary: c,
                    prefixYou: l10n.messageContact,
                    lastFromMePrefix: l10n.send,
                    onTap: () => Navigator.push(
                      context,
                      MaterialPageRoute<void>(
                        builder: (_) => ConversationScreen(
                          contextType: c.contextType,
                          contextId: c.contextId,
                          counterpartId: c.counterpartId,
                          counterpartName: c.counterpartName,
                          counterpartPhone: c.counterpartPhone,
                        ),
                      ),
                    ),
                  );
                },
              ),
            );
          }
          if (state is InboxError) {
            return _EmptyInbox(title: l10n.messages, hint: state.message);
          }
          return const Center(child: CircularProgressIndicator());
        },
      ),
    );
  }
}

class _ConversationTile extends StatelessWidget {
  final ConversationSummary summary;
  final String prefixYou;
  final String lastFromMePrefix;
  final VoidCallback onTap;

  const _ConversationTile({
    required this.summary,
    required this.prefixYou,
    required this.lastFromMePrefix,
    required this.onTap,
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
    final theme = Theme.of(context);
    final t = summary.lastMessageAt.toLocal();
    final time =
        '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';
    final hasUnread = summary.unreadCount > 0;

    return ListTile(
      leading: CircleAvatar(
        backgroundColor: AppTheme.primary,
        child: Text(_initials, style: const TextStyle(color: Colors.white)),
      ),
      title: Text(
        _title,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: TextStyle(
          fontWeight: hasUnread ? FontWeight.w700 : FontWeight.w500,
        ),
      ),
      subtitle: Text(
        summary.lastMessageBody,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      trailing: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Text(time, style: theme.textTheme.labelSmall),
          const SizedBox(height: 4),
          if (hasUnread)
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
              decoration: const BoxDecoration(
                color: AppTheme.primary,
                shape: BoxShape.circle,
              ),
              child: Text(
                '${summary.unreadCount}',
                style: const TextStyle(color: Colors.white, fontSize: 11),
              ),
            ),
        ],
      ),
      onTap: onTap,
    );
  }
}

class _EmptyInbox extends StatelessWidget {
  final String title;
  final String hint;

  const _EmptyInbox({required this.title, required this.hint});

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
              size: 80,
              color: theme.colorScheme.onSurface.withValues(alpha: 0.3),
            ),
            const SizedBox(height: 16),
            Text(title, style: theme.textTheme.titleLarge),
            const SizedBox(height: 8),
            Text(
              hint,
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
