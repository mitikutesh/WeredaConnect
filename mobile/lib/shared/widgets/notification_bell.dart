import 'dart:async';

import 'package:flutter/foundation.dart'
    show TargetPlatform, defaultTargetPlatform, kIsWeb;
import 'package:flutter/gestures.dart' show PointerDeviceKind;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_executor_web.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_queue_web.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/messages_inbox_screen.dart';
import 'package:wereda_connect/features/notifications/domain/base_notification_repository.dart';
import 'package:wereda_connect/features/notifications/presentation/notification_text.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

// The boards' notification bell and the sync status sheet it opens, shared
// by Home and the other tab headers (Deliveries, Market, Orders, Payments,
// My Trips) so every header carries the same bell.

/// The bell in a tab header's white square, for the signed-in user: the
/// boards put it at the top right of every tab.
class HeaderBell extends StatelessWidget {
  const HeaderBell({super.key});

  @override
  Widget build(BuildContext context) {
    String userId;
    try {
      userId = context.select<AuthBloc, String>((bloc) {
        final state = bloc.state;
        return state is AuthAuthenticated ? state.userId : '';
      });
    } on ProviderNotFoundException {
      // No auth in scope (widget tests): the bell shows the sync items only.
      userId = '';
    }
    return NotificationBell(
      userId: userId,
      l10n: AppLocalizations.of(context)!,
      square: true,
    );
  }
}

/// Notification bell icon with dynamic red dot indicator.
///
/// Lists the user's inbox (ESC-21, `GET /notifications`: every order,
/// delivery and money event of the user's orders) on mobile and web, and on
/// mobile the sync queue's failed and pending changes. The dot shows while
/// an inbox entry is unseen or a sync item is waiting; opening the sheet
/// marks the inbox seen. Tapping an entry opens its order.
class NotificationBell extends StatefulWidget {
  final String userId;
  final AppLocalizations l10n;
  final bool isOnline;

  /// The bell's colour: dark in the light header's square, pale on the
  /// trucker's dark header.
  final Color iconColor;

  /// The ring around the unread dot: the bell's own background.
  final Color ringColor;

  /// In a white [FwSquareButton] (the Farmer · Home board); else a quiet
  /// icon button.
  final bool square;

  const NotificationBell({
    required this.userId,
    required this.l10n,
    this.isOnline = true,
    this.iconColor = AppTheme.soilDark,
    this.ringColor = AppTheme.surfaceWhite,
    this.square = false,
    super.key,
  });

  @override
  State<NotificationBell> createState() => _NotificationBellState();
}

class _NotificationBellState extends State<NotificationBell> {
  List<_NotifItem> _notifications = [];
  bool _hasUnread = false;
  late Future<void> _loadFuture;
  final Set<String> _dismissedKeys = {};
  StreamSubscription<void>? _inboxChanges;

  /// Web has no push: the bell pulls on a timer while Home is shown (each
  /// pull is throttled by the repository).
  Timer? _webRefresh;
  static const _webRefreshInterval = Duration(minutes: 2);

  @override
  void initState() {
    super.initState();
    _inboxChanges = _inbox()?.changes.listen((_) {
      if (mounted) _loadNotifications(pull: false);
    });
    _loadFuture = _loadNotifications();
    if (kIsWeb) {
      _webRefresh = Timer.periodic(_webRefreshInterval, (_) {
        if (mounted) _loadNotifications();
      });
    }
  }

  @override
  void didUpdateWidget(covariant NotificationBell oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.userId != widget.userId) {
      _loadFuture = _loadNotifications();
    }
  }

  @override
  void dispose() {
    _inboxChanges?.cancel();
    _webRefresh?.cancel();
    super.dispose();
  }

  /// The inbox, or null when the app provides none (widget tests).
  BaseNotificationRepository? _inbox() {
    try {
      return context.read<BaseNotificationRepository>();
    } on ProviderNotFoundException {
      return null;
    }
  }

  /// [pull]: read new entries from the server first (best-effort; offline
  /// the device's entries show).
  Future<void> _loadNotifications({bool pull = true}) async {
    final items = <_NotifItem>[];
    var unread = 0;
    final userId = widget.userId;
    // Read before any await: the deadline line uses the viewer's clock.
    final clock = ClockFormat.resolve(
      widget.l10n,
      clockPreferenceOf(context, listen: false),
    );
    if (!kIsWeb) {
      try {
        final syncQueue = SyncQueue(IsarService.instance.db);

        // Failed sync operations
        final failedCount = await syncQueue.getFailedCount();
        if (failedCount > 0) {
          items.add(
            _NotifItem(
              icon: Icons.sync_problem,
              title: widget.l10n.syncFailed,
              subtitle: widget.l10n.syncFailureCount(failedCount),
              color: AppTheme.statusError,
              time: DateTime.now(),
              action: _NotifAction.syncStatus,
              key: 'sync_failed',
            ),
          );
        }

        // Pending sync operations
        final pendingCount = await syncQueue.getPendingCount();
        if (pendingCount > 0) {
          items.add(
            _NotifItem(
              icon: Icons.cloud_upload_outlined,
              title: widget.l10n.syncPending,
              subtitle: widget.l10n.syncPendingCount(pendingCount),
              color: AppTheme.accentGold,
              time: DateTime.now(),
              action: _NotifAction.syncStatus,
              key: 'sync_pending',
            ),
          );
        }
      } on Object {
        // No local queue: the inbox below still shows.
      }
    }

    final inbox = _inbox();
    if (inbox != null && userId.isNotEmpty) {
      if (pull) {
        try {
          await inbox.pull(userId);
        } on Object {
          // Offline or the server failed: show what the device has.
        }
      }
      try {
        for (final entry in await inbox.recent(userId, limit: 30)) {
          final text = notificationText(widget.l10n, entry);
          final style = notificationStyle(entry.type);
          items.add(
            _NotifItem(
              icon: style.icon,
              title: text.title,
              subtitle: text.body,
              detail: notificationDetailLine(widget.l10n, clock, entry),
              color: style.color,
              time: entry.createdAt,
              action: _NotifAction.openOrder,
              key: 'inbox_${entry.notificationId}',
              orderId: entry.orderId,
              unread: !entry.read,
            ),
          );
        }
        unread = await inbox.unreadCount(userId);
      } on Object {
        // Show the sync items alone.
      }
    }

    items.sort((a, b) => b.time.compareTo(a.time));
    if (mounted) {
      setState(() {
        _notifications = items;
        _hasUnread =
            unread > 0 || items.any((n) => n.action == _NotifAction.syncStatus);
      });
    }
  }

  List<_NotifItem> get _visibleNotifications =>
      _notifications.where((n) => !_dismissedKeys.contains(n.key)).toList();

  void _dismissNotification(String key) {
    setState(() => _dismissedKeys.add(key));
  }

  void _clearAllNotifications() {
    setState(() {
      _dismissedKeys.addAll(_notifications.map((n) => n.key));
    });
  }

  void _handleNotifTap(BuildContext sheetContext, _NotifItem item) {
    Navigator.of(sheetContext).pop(); // close sheet
    switch (item.action) {
      case _NotifAction.syncStatus:
        _dismissNotification(item.key);
        _openSyncStatusSheet();
      case _NotifAction.openOrder:
        final orderId = item.orderId;
        if (orderId != null) openOrderDetail(context, orderId);
    }
  }

  void _openSyncStatusSheet() {
    final l10n = AppLocalizations.of(context)!;
    final executor = context.read<SyncExecutor>();
    showModalBottomSheet<void>(
      context: context,
      // Over the shell's bar and create button, not under them.
      useRootNavigator: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (_) {
        return SyncStatusSheet(
          isOnline: widget.isOnline,
          l10n: l10n,
          executor: executor,
        );
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<void>(
      future: _loadFuture,
      builder: (context, _) {
        final bell = Stack(
          clipBehavior: Clip.none,
          children: [
            Icon(
              Icons.notifications_outlined,
              size: widget.square ? 20 : 24,
              color: widget.iconColor,
            ),
            if (_hasUnread)
              Positioned(
                right: -1,
                top: -1,
                child: Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(
                    color: AppTheme.accentGold,
                    shape: BoxShape.circle,
                    border: Border.all(color: widget.ringColor, width: 2),
                  ),
                ),
              ),
          ],
        );
        if (widget.square) {
          return FwSquareButton(
            semanticLabel: widget.l10n.notifications,
            onTap: () => _showNotifications(context),
            child: bell,
          );
        }
        return IconButton(
          tooltip: widget.l10n.notifications,
          onPressed: () => _showNotifications(context),
          icon: bell,
        );
      },
    );
  }

  void _showNotifications(BuildContext context) {
    // Mark as read when opened
    setState(() => _hasUnread = false);
    final inbox = _inbox();
    if (inbox != null && widget.userId.isNotEmpty) {
      inbox.markAllRead(widget.userId).ignore();
    }

    showModalBottomSheet<void>(
      context: context,
      // Over the shell's bar and create button, not under them.
      useRootNavigator: true,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (sheetContext) {
        // A phone opens it low and swipes it up. A desktop browser opens it
        // tall: no mouse could drag a sheet, so it stayed at 40% with the
        // rest of the list below the window.
        final desktop = switch (defaultTargetPlatform) {
          TargetPlatform.macOS ||
          TargetPlatform.windows ||
          TargetPlatform.linux =>
            true,
          _ => false,
        };
        return StatefulBuilder(
          builder: (sheetContext, setSheetState) {
            final visible = _visibleNotifications;
            return DraggableScrollableSheet(
              expand: false,
              initialChildSize: desktop ? _notifSheetMax : 0.4,
              maxChildSize: _notifSheetMax,
              builder: (context, scrollController) {
                return Column(
                  children: [
                    // Drag handle + title
                    Padding(
                      padding: const EdgeInsets.fromLTRB(24, 16, 24, 12),
                      child: Column(
                        children: [
                          Container(
                            width: 40,
                            height: 4,
                            decoration: BoxDecoration(
                              color: AppTheme.textMuted.withAlpha(60),
                              borderRadius: BorderRadius.circular(2),
                            ),
                          ),
                          const SizedBox(height: 16),
                          Row(
                            children: [
                              Text(
                                widget.l10n.notifications,
                                style: const TextStyle(
                                  fontSize: 18,
                                  fontWeight: FontWeight.w800,
                                  color: AppTheme.textDark,
                                ),
                              ),
                              const Spacer(),
                              if (visible.isNotEmpty) ...[
                                GestureDetector(
                                  onTap: () {
                                    _clearAllNotifications();
                                    setSheetState(() {});
                                    Navigator.of(sheetContext).pop();
                                  },
                                  child: Text(
                                    widget.l10n.clearAll,
                                    style: const TextStyle(
                                      fontSize: 14,
                                      fontWeight: FontWeight.w600,
                                      color: AppTheme.statusError,
                                    ),
                                  ),
                                ),
                                const SizedBox(width: 12),
                                Text(
                                  '${visible.length}',
                                  style: const TextStyle(
                                    fontSize: 14,
                                    fontWeight: FontWeight.w600,
                                    color: AppTheme.textMuted,
                                  ),
                                ),
                              ],
                            ],
                          ),
                        ],
                      ),
                    ),
                    // The boards' headers draw no Messages or Profile icon
                    // and the phone bar has no tab for them, so they open
                    // from here (the desktop sidebar lists them too).
                    _AccountShortcuts(
                      l10n: widget.l10n,
                      onOpen: () => Navigator.of(sheetContext).pop(),
                    ),
                    const Divider(height: 1),
                    // Notification list
                    Expanded(
                      child: visible.isEmpty
                          ? Center(
                              child: Column(
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  Icon(
                                    Icons.notifications_none,
                                    size: 48,
                                    color: AppTheme.textMuted.withAlpha(80),
                                  ),
                                  const SizedBox(height: 12),
                                  Text(
                                    widget.l10n.noNotificationsYet,
                                    style: const TextStyle(
                                      fontSize: 16,
                                      fontWeight: FontWeight.w600,
                                      color: AppTheme.textMuted,
                                    ),
                                  ),
                                  const SizedBox(height: 4),
                                  Text(
                                    widget.l10n.noNotificationsDescription,
                                    style: TextStyle(
                                      fontSize: 13,
                                      color: AppTheme.textMuted.withAlpha(160),
                                    ),
                                  ),
                                ],
                              ),
                            )
                          : ScrollConfiguration(
                              // A mouse or trackpad drag grows and shrinks
                              // the sheet too, not only a finger.
                              behavior:
                                  ScrollConfiguration.of(context).copyWith(
                                dragDevices: PointerDeviceKind.values.toSet(),
                              ),
                              child: ListView.separated(
                                controller: scrollController,
                                // The last card clears the phone's gesture
                                // bar or home indicator.
                                padding: EdgeInsets.fromLTRB(
                                  20,
                                  12,
                                  20,
                                  12 + MediaQuery.viewPaddingOf(context).bottom,
                                ),
                                itemCount: visible.length,
                                separatorBuilder: (_, __) =>
                                    const SizedBox(height: 8),
                                itemBuilder: (context, index) {
                                  final item = visible[index];
                                  return Dismissible(
                                    key: ValueKey(item.key),
                                    direction: DismissDirection.endToStart,
                                    onDismissed: (_) {
                                      _dismissNotification(item.key);
                                      setSheetState(() {});
                                    },
                                    background: Container(
                                      alignment: Alignment.centerRight,
                                      padding: const EdgeInsets.only(right: 20),
                                      decoration: BoxDecoration(
                                        color: AppTheme.statusError,
                                        borderRadius: BorderRadius.circular(18),
                                      ),
                                      child: const Icon(
                                        Icons.delete_outline,
                                        color: Colors.white,
                                      ),
                                    ),
                                    child: _NotifCard(
                                      item: item,
                                      l10n: widget.l10n,
                                      onTap: () =>
                                          _handleNotifTap(sheetContext, item),
                                    ),
                                  );
                                },
                              ),
                            ),
                    ),
                  ],
                );
              },
            );
          },
        );
      },
    );
  }
}

/// The notification sheet's tallest size, a share of the window height.
const double _notifSheetMax = 0.9;

enum _NotifAction { syncStatus, openOrder }

class _NotifItem {
  final IconData icon;
  final String title;
  final String subtitle;

  /// Crop, quantity, amount and deadline of an inbox entry.
  final ClockLabel? detail;
  final Color color;
  final DateTime time;
  final _NotifAction action;
  final String key;

  /// The order an inbox entry opens; null for sync items and entries
  /// without an order.
  final String? orderId;

  /// An inbox entry the user has not seen in the bell yet.
  final bool unread;

  _NotifItem({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.color,
    required this.time,
    required this.action,
    required this.key,
    this.detail,
    this.orderId,
    this.unread = false,
  });
}

class _NotifCard extends StatelessWidget {
  final _NotifItem item;
  final AppLocalizations l10n;
  final VoidCallback? onTap;

  const _NotifCard({required this.item, required this.l10n, this.onTap});

  @override
  Widget build(BuildContext context) {
    final timeDiff = DateTime.now().difference(item.time);
    final timeLabel = timeDiff.inMinutes < 60
        ? l10n.minutesAgo(timeDiff.inMinutes)
        : timeDiff.inHours < 24
            ? l10n.hoursAgo(timeDiff.inHours)
            : l10n.daysAgo(timeDiff.inDays);

    return Material(
      color: AppTheme.surfaceWhite,
      borderRadius: BorderRadius.circular(18),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(18),
        child: Container(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(18),
            border: Border.all(color: AppTheme.whisperBorder),
          ),
          child: Row(
            children: [
              Container(
                width: 40,
                height: 40,
                decoration: BoxDecoration(
                  color: item.color.withAlpha(20),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(item.icon, size: 20, color: item.color),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      item.title,
                      style: TextStyle(
                        fontSize: 14,
                        fontWeight:
                            item.unread ? FontWeight.w800 : FontWeight.w600,
                        color: AppTheme.textDark,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 2),
                    Text(
                      item.subtitle,
                      style: const TextStyle(
                        fontSize: 12,
                        color: AppTheme.textMuted,
                      ),
                      maxLines: item.action == _NotifAction.openOrder ? 3 : 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    if (item.detail != null) ...[
                      const SizedBox(height: 2),
                      ClockText(
                        item.detail!,
                        style: const TextStyle(
                          fontSize: 11,
                          fontWeight: FontWeight.w600,
                          color: AppTheme.textDark,
                        ),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ],
                  ],
                ),
              ),
              const SizedBox(width: 8),
              Text(
                timeLabel,
                style: const TextStyle(
                  fontSize: 11,
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(width: 4),
              Icon(
                Icons.chevron_right,
                size: 18,
                color: AppTheme.textMuted.withAlpha(120),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Bottom sheet showing live sync queue status with Sync Now / Retry actions.
class SyncStatusSheet extends StatefulWidget {
  final bool isOnline;
  final AppLocalizations l10n;
  final SyncExecutor executor;
  final VoidCallback? onSyncComplete;

  const SyncStatusSheet({
    required this.isOnline,
    required this.l10n,
    required this.executor,
    this.onSyncComplete,
    super.key,
  });

  @override
  State<SyncStatusSheet> createState() => _SyncStatusSheetState();
}

class _SyncStatusSheetState extends State<SyncStatusSheet> {
  int _pendingCount = 0;
  int _failedCount = 0;
  bool _isSyncing = false;
  String? _lastStatusMessage;
  StreamSubscription<SyncBatchResult>? _syncSub;

  @override
  void initState() {
    super.initState();
    _loadCounts();
    _isSyncing = widget.executor.isSyncing;
    _syncSub = widget.executor.onSyncResult.listen(_onSyncResult);
  }

  @override
  void dispose() {
    _syncSub?.cancel();
    super.dispose();
  }

  void _onSyncResult(SyncBatchResult result) {
    if (!mounted) return;
    _loadCounts();
    setState(() {
      _isSyncing = widget.executor.isSyncing;
      _lastStatusMessage = switch (result.status) {
        SyncBatchStatus.success => widget.l10n.syncComplete,
        SyncBatchStatus.partial =>
          '${result.succeeded} synced, ${result.failed} failed',
        SyncBatchStatus.empty => widget.l10n.syncComplete,
        SyncBatchStatus.skipped => null, // another sync in progress, no change
        SyncBatchStatus.noInternet => widget.l10n.offlineModeIndicator,
        SyncBatchStatus.networkError => 'Network error',
        SyncBatchStatus.authError => 'Authentication error',
        SyncBatchStatus.rateLimited => 'Rate limited — try later',
        SyncBatchStatus.error => result.errorMessage ?? 'Error',
        // The account is being deleted: the AuthBloc wipes or the user
        // finishes the deletion from Profile.
        SyncBatchStatus.accountDeleted => null,
      };
    });
  }

  Future<void> _loadCounts() async {
    try {
      final queue = SyncQueue(IsarService.instance.db);
      final pending = await queue.getPendingCount();
      final failed = await queue.getFailedCount();
      if (mounted) {
        setState(() {
          _pendingCount = pending;
          _failedCount = failed;
        });
      }
    } on Object {
      // Ignore
    }
  }

  Future<void> _syncNow() async {
    setState(() => _isSyncing = true);
    try {
      final result = await widget.executor.syncNow();
      if (mounted) {
        _onSyncResult(result);
        widget.onSyncComplete?.call();
      }
    } on Object {
      // Ensure _isSyncing resets even if executor throws unexpectedly
      if (mounted) {
        setState(() => _isSyncing = widget.executor.isSyncing);
      }
    }
  }

  Future<void> _retryFailed() async {
    final queue = SyncQueue(IsarService.instance.db);
    final retried = await queue.retryFailed();
    if (mounted && retried > 0) {
      await _syncNow();
    } else if (mounted) {
      // Refresh counts — failed ops may have been cleared
      _loadCounts();
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = widget.l10n;

    // Determine header status
    final IconData statusIcon;
    final Color statusColor;
    final String statusText;

    if (_isSyncing) {
      statusIcon = Icons.sync;
      statusColor = AppTheme.statusInfo;
      statusText = l10n.syncInProgress;
    } else if (!widget.isOnline) {
      statusIcon = Icons.cloud_off;
      statusColor = AppTheme.textMuted;
      statusText = l10n.offlineModeIndicator;
    } else if (_failedCount > 0) {
      statusIcon = Icons.error_outline;
      statusColor = AppTheme.statusError;
      statusText = '$_failedCount ${l10n.syncFailed.toLowerCase()}';
    } else if (_pendingCount > 0) {
      statusIcon = Icons.cloud_upload;
      statusColor = AppTheme.accentGold;
      statusText = '$_pendingCount ${l10n.syncPending.toLowerCase()}';
    } else {
      statusIcon = Icons.cloud_done;
      statusColor = AppTheme.statusSuccess;
      statusText = l10n.syncComplete;
    }

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(24, 16, 24, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            // Drag handle
            Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: AppTheme.textMuted.withAlpha(60),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
            const SizedBox(height: 20),
            // Status icon (animated when syncing)
            _isSyncing
                ? TweenAnimationBuilder<double>(
                    tween: Tween(begin: 0, end: 1),
                    duration: const Duration(seconds: 1),
                    builder: (_, value, child) => Transform.rotate(
                      angle: value * 2 * 3.14159,
                      child: child,
                    ),
                    child: Icon(statusIcon, size: 40, color: statusColor),
                    onEnd: () {
                      // Restart if still syncing
                      if (mounted) setState(() {});
                    },
                  )
                : Icon(statusIcon, size: 40, color: statusColor),
            const SizedBox(height: 10),
            Text(
              statusText,
              style: TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w600,
                color: statusColor,
              ),
            ),
            if (_lastStatusMessage != null &&
                _lastStatusMessage != statusText) ...[
              const SizedBox(height: 4),
              Text(
                _lastStatusMessage!,
                style: const TextStyle(
                  fontSize: 12,
                  color: AppTheme.textMuted,
                ),
              ),
            ],
            const SizedBox(height: 20),
            // Pending operations row
            _SyncInfoRow(
              icon: Icons.cloud_upload_outlined,
              label: l10n.syncPending,
              value: '$_pendingCount',
              color:
                  _pendingCount > 0 ? AppTheme.accentGold : AppTheme.textMuted,
            ),
            const SizedBox(height: 10),
            // Failed operations row
            _SyncInfoRow(
              icon: Icons.error_outline,
              label: l10n.syncFailed,
              value: '$_failedCount',
              color:
                  _failedCount > 0 ? AppTheme.statusError : AppTheme.textMuted,
            ),
            const SizedBox(height: 20),
            // Action buttons
            if (_failedCount > 0 && widget.isOnline)
              SizedBox(
                width: double.infinity,
                child: ElevatedButton.icon(
                  onPressed: _isSyncing ? null : _retryFailed,
                  icon: _isSyncing
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: Colors.white,
                          ),
                        )
                      : const Icon(Icons.refresh, size: 20),
                  label: Text(_isSyncing ? l10n.syncInProgress : l10n.retry),
                  style: ElevatedButton.styleFrom(
                    backgroundColor: AppTheme.statusError,
                    foregroundColor: Colors.white,
                    padding: const EdgeInsets.symmetric(vertical: 12),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(18),
                    ),
                  ),
                ),
              )
            else if (_pendingCount > 0 && widget.isOnline)
              SizedBox(
                width: double.infinity,
                child: ElevatedButton.icon(
                  onPressed: _isSyncing ? null : _syncNow,
                  icon: _isSyncing
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: Colors.white,
                          ),
                        )
                      : const Icon(Icons.sync, size: 20),
                  label: Text(
                    _isSyncing ? l10n.syncInProgress : l10n.syncNow,
                  ),
                  style: ElevatedButton.styleFrom(
                    backgroundColor: AppTheme.primaryGreen,
                    foregroundColor: Colors.white,
                    padding: const EdgeInsets.symmetric(vertical: 12),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(18),
                    ),
                  ),
                ),
              ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
  }
}

/// Single row in the sync status sheet.
class _SyncInfoRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;
  final Color color;

  const _SyncInfoRow({
    required this.icon,
    required this.label,
    required this.value,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Icon(icon, size: 20, color: color),
        const SizedBox(width: 10),
        Text(
          label,
          style: const TextStyle(
            fontSize: 14,
            color: AppTheme.textDark,
          ),
        ),
        const Spacer(),
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
          decoration: BoxDecoration(
            color: color.withAlpha(20),
            borderRadius: BorderRadius.circular(18),
          ),
          child: Text(
            value,
            style: TextStyle(
              fontSize: 14,
              fontWeight: FontWeight.w600,
              color: color,
            ),
          ),
        ),
      ],
    );
  }
}

/// Messages (with the unread count) and Profile, side by side at the top of
/// the notifications sheet.
class _AccountShortcuts extends StatelessWidget {
  final AppLocalizations l10n;

  /// Closes the sheet before the screen opens.
  final VoidCallback onOpen;

  const _AccountShortcuts({required this.l10n, required this.onOpen});

  void _open(BuildContext context, String route, Widget screen) {
    final navigator = Navigator.of(context);
    final router = kIsWeb ? GoRouter.maybeOf(context) : null;
    onOpen();
    if (router != null) {
      router.go(route);
      return;
    }
    navigator.push(MaterialPageRoute<void>(builder: (_) => screen));
  }

  @override
  Widget build(BuildContext context) {
    final messages = _ShortcutButton(
      key: const Key('notif_open_messages'),
      icon: Icons.chat_bubble_outline,
      label: l10n.messages,
      onTap: () => _open(context, '/messages', const MessagesInboxScreen()),
    );
    InboxBloc? inbox;
    try {
      inbox = context.read<InboxBloc>();
    } on ProviderNotFoundException {
      inbox = null;
    }
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 0, 24, 14),
      child: Row(
        children: [
          Expanded(
            child: inbox == null
                ? messages
                : BlocBuilder<InboxBloc, InboxState>(
                    bloc: inbox,
                    builder: (context, state) {
                      final unread =
                          state is InboxLoaded ? state.totalUnread : 0;
                      return unread <= 0
                          ? messages
                          : Badge(
                              label: Text('$unread'),
                              offset: const Offset(-8, 4),
                              child: messages,
                            );
                    },
                  ),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: _ShortcutButton(
              key: const Key('notif_open_profile'),
              icon: Icons.person_outline,
              label: l10n.profile,
              onTap: () => _open(context, '/profile', const ProfileScreen()),
            ),
          ),
        ],
      ),
    );
  }
}

class _ShortcutButton extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback onTap;

  const _ShortcutButton({
    required this.icon,
    required this.label,
    required this.onTap,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return OutlinedButton.icon(
      onPressed: onTap,
      icon: Icon(icon, size: 18),
      label: Text(label, maxLines: 1, overflow: TextOverflow.ellipsis),
      style: OutlinedButton.styleFrom(
        minimumSize: const Size.fromHeight(44),
        foregroundColor: AppTheme.soilDark,
      ),
    );
  }
}
