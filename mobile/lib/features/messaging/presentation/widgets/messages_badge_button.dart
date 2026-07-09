import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/messages_inbox_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// An app-bar action that opens the messages inbox and shows an unread badge.
/// Reachable from every role's home header.
class MessagesBadgeButton extends StatefulWidget {
  /// Icon colour (e.g. white on a coloured header). Null → theme default.
  final Color? color;

  const MessagesBadgeButton({super.key, this.color});

  @override
  State<MessagesBadgeButton> createState() => _MessagesBadgeButtonState();
}

class _MessagesBadgeButtonState extends State<MessagesBadgeButton> {
  @override
  void initState() {
    super.initState();
    // Populate the badge without needing the inbox screen to be opened first.
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      context.read<InboxBloc>().add(LoadInbox(authState.userId));
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    final button = IconButton(
      icon: Icon(Icons.chat_bubble_outline, color: widget.color),
      tooltip: l10n.messages,
      onPressed: () => Navigator.push(
        context,
        MaterialPageRoute<void>(
          builder: (_) => const MessagesInboxScreen(),
        ),
      ),
    );

    return BlocBuilder<InboxBloc, InboxState>(
      builder: (context, state) {
        final unread = state is InboxLoaded ? state.totalUnread : 0;
        if (unread <= 0) return button;
        return Badge(
          label: Text('$unread'),
          offset: const Offset(-6, 6),
          child: button,
        );
      },
    );
  }
}
