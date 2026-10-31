import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/messages_inbox_screen.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/screens/settings_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/notification_bell.dart';

/// A tab header's trailing actions: the bell, then the [AccountButton]
/// where no sidebar lists the account pages. [bell] replaces the default
/// [HeaderBell] (Home passes its own with the online state).
class HeaderActions extends StatelessWidget {
  final Widget? bell;

  const HeaderActions({this.bell, super.key});

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        bell ?? const HeaderBell(),
        if (!SidebarScope.of(context)) ...[
          const SizedBox(width: 8),
          const AccountButton(),
        ],
      ],
    );
  }
}

/// The round profile button at the top right of a phone's tab headers. It
/// opens the account sheet: what the web sidebar lists beside the bar's
/// tabs ([sidebarExtrasForRole]: Messages and Payments, or Orders for the
/// trucker), then Profile and Settings. The phone bar has no slot for them.
///
/// Draws nothing under a web sidebar ([SidebarScope]), which lists the same
/// pages, or when no one is signed in. A gold dot shows unread messages.
class AccountButton extends StatelessWidget {
  /// The trucker's dark Home header.
  final bool dark;

  const AccountButton({this.dark = false, super.key});

  static InboxBloc? _inbox(BuildContext context) {
    try {
      return context.read<InboxBloc>();
    } on ProviderNotFoundException {
      return null;
    }
  }

  @override
  Widget build(BuildContext context) {
    if (SidebarScope.of(context)) return const SizedBox.shrink();
    AuthState? auth;
    try {
      auth = context.watch<AuthBloc>().state;
    } on ProviderNotFoundException {
      auth = null;
    }
    if (auth is! AuthAuthenticated) return const SizedBox.shrink();

    final l10n = AppLocalizations.of(context)!;
    final inbox = _inbox(context);
    Widget avatar(int unread) => _Avatar(
          dark: dark,
          unread: unread > 0,
          label: l10n.profile,
          onTap: () => showAccountSheet(context),
        );
    if (inbox == null) return avatar(0);
    return BlocBuilder<InboxBloc, InboxState>(
      bloc: inbox,
      builder: (context, state) =>
          avatar(state is InboxLoaded ? state.totalUnread : 0),
    );
  }
}

class _Avatar extends StatelessWidget {
  final bool dark;
  final bool unread;
  final String label;
  final VoidCallback onTap;

  const _Avatar({
    required this.dark,
    required this.unread,
    required this.label,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: true,
      label: label,
      child: Material(
        key: const Key('account_button'),
        color: dark ? const Color(0xFF22302A) : const Color(0xFFE6ECF5),
        shape: const CircleBorder(),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: SizedBox(
            width: 44,
            height: 44,
            child: Stack(
              alignment: Alignment.center,
              clipBehavior: Clip.none,
              children: [
                Icon(
                  Icons.person,
                  size: 22,
                  color: dark ? Colors.white : const Color(0xFF2E4C7D),
                ),
                if (unread)
                  Positioned(
                    right: 9,
                    top: 9,
                    child: Container(
                      key: const Key('account_button_dot'),
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        color: AppTheme.accentGold,
                        shape: BoxShape.circle,
                        border: Border.all(
                          color: dark
                              ? const Color(0xFF22302A)
                              : const Color(0xFFE6ECF5),
                          width: 2,
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// One row of the account sheet.
class _AccountItem {
  final String id;
  final IconData icon;
  final String label;

  /// The go_router path the web app opens.
  final String route;

  /// The screen the device app pushes.
  final Widget Function() screen;
  final bool messages;

  const _AccountItem({
    required this.id,
    required this.icon,
    required this.label,
    required this.route,
    required this.screen,
    this.messages = false,
  });
}

/// The screen the device app pushes for a sidebar extra: the same screens
/// Home's tiles push. Bar tabs never reach the sheet.
Widget Function()? _extraScreen(RoleTab tab) => switch (tab) {
      RoleTab.messages => () => const MessagesInboxScreen(),
      RoleTab.payments => () => const PaymentHistoryScreen(),
      RoleTab.orders => () =>
          const Scaffold(body: OrdersTabScreen(showBackButton: true)),
      RoleTab.home ||
      RoleTab.myCrops ||
      RoleTab.myTrips ||
      RoleTab.market ||
      RoleTab.deliveries =>
        null,
    };

/// The account sheet's rows for [role]: the sidebar's extras, then Profile
/// and Settings, as the web sidebar lists them.
List<_AccountItem> _itemsForRole(UserRole role, AppLocalizations l10n) {
  return [
    for (final tab in sidebarExtrasForRole(role))
      if (_extraScreen(tab) case final screen?)
        _AccountItem(
          id: tab.name,
          icon: tab.icon,
          label: tab.label(l10n),
          route: tab.route,
          screen: screen,
          messages: tab == RoleTab.messages,
        ),
    _AccountItem(
      id: 'profile',
      icon: Icons.person_outline,
      label: l10n.profile,
      route: '/profile',
      screen: () => const ProfileScreen(),
    ),
    _AccountItem(
      id: 'settings',
      icon: Icons.settings_outlined,
      label: l10n.settings,
      route: '/settings',
      screen: () => const SettingsScreen(),
    ),
  ];
}

/// Opens the account sheet for the signed-in user.
Future<void> showAccountSheet(BuildContext context) async {
  final auth = context.read<AuthBloc>().state;
  if (auth is! AuthAuthenticated) return;
  final l10n = AppLocalizations.of(context)!;
  final navigator = Navigator.of(context);
  final router = kIsWeb ? GoRouter.maybeOf(context) : null;
  final inbox = AccountButton._inbox(context);
  final items = _itemsForRole(auth.role, l10n);

  final picked = await showModalBottomSheet<_AccountItem>(
    context: context,
    // Over the shell's bar and create button, not under them.
    useRootNavigator: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
    ),
    builder: (sheetContext) => _AccountSheet(
      auth: auth,
      l10n: l10n,
      items: items,
      inbox: inbox,
    ),
  );
  if (picked == null) return;
  if (router != null) {
    router.go(picked.route);
    return;
  }
  await navigator
      .push(MaterialPageRoute<void>(builder: (_) => picked.screen()));
}

class _AccountSheet extends StatelessWidget {
  final AuthAuthenticated auth;
  final AppLocalizations l10n;
  final List<_AccountItem> items;
  final InboxBloc? inbox;

  const _AccountSheet({
    required this.auth,
    required this.l10n,
    required this.items,
    required this.inbox,
  });

  @override
  Widget build(BuildContext context) {
    final roleName = switch (auth.role) {
      UserRole.farmer => l10n.farmer,
      UserRole.trucker => l10n.trucker,
      UserRole.buyer => l10n.buyer,
    };
    final woreda = auth.woreda ?? '';
    // Where the user is and what they do: "Meki · Farmer", as the sidebar.
    final subtitle = woreda.isNotEmpty ? '$woreda · $roleName' : roleName;
    final name = auth.userName.isNotEmpty ? auth.userName : roleName;

    return SafeArea(
      top: false,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 16, 12, 12),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 40,
                height: 4,
                decoration: BoxDecoration(
                  color: AppTheme.textMuted.withAlpha(60),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 16),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    name,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontSize: 18,
                      fontWeight: FontWeight.w800,
                      color: AppTheme.soilDark,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    subtitle,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.mutedSteel,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 12),
            const Divider(height: 1),
            const SizedBox(height: 4),
            for (final item in items) _row(context, item),
          ],
        ),
      ),
    );
  }

  Widget _row(BuildContext context, _AccountItem item) {
    Widget tile(int unread) => ListTile(
          key: Key('account_menu_${item.id}'),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
          ),
          leading: Icon(item.icon, color: AppTheme.soilDark),
          title: Text(
            item.label,
            style: const TextStyle(
              fontSize: 15,
              fontWeight: FontWeight.w600,
              color: AppTheme.soilDark,
            ),
          ),
          trailing: unread > 0
              ? Badge(label: Text('$unread'))
              : const Icon(Icons.chevron_right, color: AppTheme.mutedSteel),
          onTap: () => Navigator.of(context).pop(item),
        );
    final inbox = this.inbox;
    if (!item.messages || inbox == null) return tile(0);
    return BlocBuilder<InboxBloc, InboxState>(
      bloc: inbox,
      builder: (context, state) =>
          tile(state is InboxLoaded ? state.totalUnread : 0),
    );
  }
}
