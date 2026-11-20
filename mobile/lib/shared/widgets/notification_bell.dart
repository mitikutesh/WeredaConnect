import 'dart:async';

import 'package:flutter/foundation.dart'
    show TargetPlatform, defaultTargetPlatform, kIsWeb, visibleForTesting;
import 'package:flutter/gestures.dart' show PointerDeviceKind;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_executor_web.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_queue_web.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/notifications/domain/base_notification_repository.dart';
import 'package:wereda_connect/features/notifications/presentation/notification_text.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';
import 'package:wereda_connect/features/reputation/presentation/screens/my_reputation_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

// The boards' notification bell and the sync status sheet it opens, shared
// by Home and the other tab headers (Deliveries, Market, Orders, Payments,
// My Trips) so every header carries the same bell.

/// The bell in a tab header's round paper button, for the signed-in user:
/// the boards put it at the top right of every tab.
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

/// Notification bell icon with an orange dot when something is new.
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

  /// The bell's colour: black on the round paper button, pale on a black
  /// header.
  final Color iconColor;

  /// The ring around the unread dot: the bell's own background. The paper
  /// grey in the round button when null.
  final Color? ringColor;

  /// In the 44 px round paper button (the Farmer home board); else a quiet
  /// icon button.
  final bool square;

  const NotificationBell({
    required this.userId,
    required this.l10n,
    this.isOnline = true,
    this.iconColor = AppTheme.ink,
    this.ringColor,
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
              color: AppTheme.statusWarning,
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
              // ESC-31: an entry about the user's own reputation opens
              // My reputation; every other entry opens its order.
              action: reputationNotificationTypes.contains(entry.type)
                  ? _NotifAction.openReputation
                  : _NotifAction.openOrder,
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
      case _NotifAction.openReputation:
        _openMyReputation();
    }
  }

  /// ESC-31: the signed-in user's own reputation (the bell's own user id;
  /// the server only ever sends these entries to the party themselves).
  void _openMyReputation() {
    final ReputationRepository repository;
    try {
      repository = context.read<ReputationRepository>();
    } on ProviderNotFoundException {
      return;
    }
    if (widget.userId.isEmpty) return;
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => MyReputationScreen(
          userId: widget.userId,
          repository: repository,
        ),
      ),
    );
  }

  void _openSyncStatusSheet() {
    final l10n = AppLocalizations.of(context)!;
    final executor = context.read<SyncExecutor>();
    showModalBottomSheet<void>(
      context: context,
      // Over the shell's bar and create button, not under them.
      useRootNavigator: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
      ),
      backgroundColor: AppTheme.ground,
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
              // The board's dot: 8 px of orange in a 2 px ring of the
              // button's own ground, at the bell's top right.
              Positioned(
                right: -2,
                top: -3,
                child: Container(
                  key: const Key('notification_bell_dot'),
                  width: 12,
                  height: 12,
                  decoration: BoxDecoration(
                    color: AppTheme.orange,
                    shape: BoxShape.circle,
                    border: Border.all(
                      color: widget.ringColor ??
                          (widget.square
                              ? AppTheme.paper
                              : AppTheme.surfaceWhite),
                      width: 2,
                    ),
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
      backgroundColor: AppTheme.ground,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
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
                      padding: const EdgeInsets.fromLTRB(20, 12, 12, 4),
                      child: Column(
                        children: [
                          const _SheetHandle(),
                          const SizedBox(height: 12),
                          Row(
                            children: [
                              Flexible(
                                child: Semantics(
                                  header: true,
                                  child: Text(
                                    widget.l10n.notifications,
                                    maxLines: 2,
                                    overflow: TextOverflow.ellipsis,
                                    style: _sheetTitle,
                                  ),
                                ),
                              ),
                              if (visible.isNotEmpty) ...[
                                const SizedBox(width: 10),
                                FwPill(
                                  label: '${visible.length}',
                                  background: AppTheme.sand,
                                  foreground: AppTheme.inkSoft,
                                ),
                                const Spacer(),
                                TextButton(
                                  onPressed: () {
                                    _clearAllNotifications();
                                    setSheetState(() {});
                                    Navigator.of(sheetContext).pop();
                                  },
                                  style: TextButton.styleFrom(
                                    foregroundColor: AppTheme.statusError,
                                    minimumSize: const Size(48, 48),
                                  ),
                                  child: Text(widget.l10n.clearAll),
                                ),
                              ] else
                                const SizedBox(height: 48),
                            ],
                          ),
                        ],
                      ),
                    ),
                    // Notification list
                    Expanded(
                      child: visible.isEmpty
                          ? Center(
                              child: Column(
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  const Icon(
                                    Icons.notifications_none,
                                    size: 40,
                                    color: AppTheme.sketch,
                                  ),
                                  const SizedBox(height: 12),
                                  Padding(
                                    padding: const EdgeInsets.symmetric(
                                      horizontal: 24,
                                    ),
                                    child: Text(
                                      widget.l10n.noNotificationsYet,
                                      textAlign: TextAlign.center,
                                      style: const TextStyle(
                                        fontSize: 17,
                                        fontWeight: FontWeight.w800,
                                        color: AppTheme.ink,
                                      ),
                                    ),
                                  ),
                                  const SizedBox(height: 4),
                                  Padding(
                                    padding: const EdgeInsets.symmetric(
                                      horizontal: 24,
                                    ),
                                    child: Text(
                                      widget.l10n.noNotificationsDescription,
                                      textAlign: TextAlign.center,
                                      style: const TextStyle(
                                        fontSize: 14,
                                        color: AppTheme.slate,
                                      ),
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
                                        borderRadius: BorderRadius.circular(24),
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

enum _NotifAction { syncStatus, openOrder, openReputation }

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

    // A paper card, radius 24. The icon sits in a circle on its state's
    // tint; an unseen entry has a bolder title and the orange dot.
    return FwCard(
      padding: const EdgeInsets.all(14),
      onTap: onTap,
      child: Row(
        children: [
          Container(
            width: 40,
            height: 40,
            decoration: BoxDecoration(
              color: item.color.withAlpha(28),
              shape: BoxShape.circle,
            ),
            child: Icon(item.icon, size: 20, color: item.color),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Flexible(
                      child: Text(
                        item.title,
                        style: TextStyle(
                          fontSize: 15,
                          fontWeight:
                              item.unread ? FontWeight.w800 : FontWeight.w700,
                          letterSpacing: -0.15,
                          color: AppTheme.ink,
                        ),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    if (item.unread) ...[
                      const SizedBox(width: 6),
                      Container(
                        width: 8,
                        height: 8,
                        decoration: const BoxDecoration(
                          color: AppTheme.orange,
                          shape: BoxShape.circle,
                        ),
                      ),
                    ],
                  ],
                ),
                const SizedBox(height: 2),
                Text(
                  item.subtitle,
                  style: const TextStyle(
                    fontSize: 13,
                    height: 1.35,
                    color: AppTheme.slate,
                  ),
                  maxLines: item.action == _NotifAction.openOrder ? 3 : 1,
                  overflow: TextOverflow.ellipsis,
                ),
                if (item.detail != null) ...[
                  const SizedBox(height: 3),
                  ClockText(
                    item.detail!,
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                      color: AppTheme.ink,
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
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: AppTheme.slate,
            ),
          ),
          const SizedBox(width: 2),
          const Icon(Icons.chevron_right, size: 18, color: AppTheme.slate),
        ],
      ),
    );
  }
}

/// A sheet's drag handle, as the boards draw it: 40 by 4, rounded.
class _SheetHandle extends StatelessWidget {
  const _SheetHandle();

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 40,
      height: 4,
      decoration: BoxDecoration(
        color: AppTheme.sketch,
        borderRadius: BorderRadius.circular(999),
      ),
    );
  }
}

/// A sheet's title (the boards' "Pause sharing?").
const TextStyle _sheetTitle = TextStyle(
  fontSize: 26,
  fontWeight: FontWeight.w800,
  letterSpacing: -0.9,
  height: 1.1,
  color: AppTheme.ink,
);

/// The line the sync sheet shows under its header after a sync run; null
/// when the run says nothing new.
///
/// BUG-34: a run that ended without an error but left [pendingCount] changes
/// in the queue (a listing whose photo upload failed is held back and
/// counted as neither sent nor failed) used to read "Sync Complete". It now
/// says that they still wait and will be sent again.
@visibleForTesting
String? syncResultMessage(
  AppLocalizations l10n,
  SyncBatchResult result, {
  required int pendingCount,
}) {
  String finished() => pendingCount > 0
      ? l10n.syncStillWaiting(pendingCount)
      : l10n.syncComplete;
  return switch (result.status) {
    SyncBatchStatus.success => finished(),
    SyncBatchStatus.partial =>
      '${result.succeeded} synced, ${result.failed} failed',
    SyncBatchStatus.empty => finished(),
    SyncBatchStatus.skipped => null, // another sync in progress, no change
    SyncBatchStatus.noInternet => l10n.offlineModeIndicator,
    SyncBatchStatus.networkError => 'Network error',
    SyncBatchStatus.authError => 'Authentication error',
    SyncBatchStatus.rateLimited => 'Rate limited, try later',
    SyncBatchStatus.error => result.errorMessage ?? 'Error',
    // The account is being deleted: the AuthBloc wipes or the user
    // finishes the deletion from Profile.
    SyncBatchStatus.accountDeleted => null,
  };
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

  Future<void> _onSyncResult(SyncBatchResult result) async {
    if (!mounted) return;
    // The counts first: the message depends on what still waits (BUG-34).
    await _loadCounts();
    if (!mounted) return;
    setState(() {
      _isSyncing = widget.executor.isSyncing;
      _lastStatusMessage = syncResultMessage(
        widget.l10n,
        result,
        pendingCount: _pendingCount,
      );
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
        await _onSyncResult(result);
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
      // Refresh counts: failed ops may have been cleared
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
      statusColor = AppTheme.slate;
      statusText = l10n.offlineModeIndicator;
    } else if (_failedCount > 0) {
      statusIcon = Icons.error_outline;
      statusColor = AppTheme.statusError;
      statusText = '$_failedCount ${l10n.syncFailed.toLowerCase()}';
    } else if (_pendingCount > 0) {
      statusIcon = Icons.cloud_upload;
      statusColor = AppTheme.statusWarning;
      statusText = '$_pendingCount ${l10n.syncPending.toLowerCase()}';
    } else {
      statusIcon = Icons.cloud_done;
      statusColor = AppTheme.statusSuccess;
      statusText = l10n.syncComplete;
    }

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const _SheetHandle(),
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
              textAlign: TextAlign.center,
              style: _sheetTitle.copyWith(fontSize: 22, letterSpacing: -0.5),
            ),
            if (_lastStatusMessage != null &&
                _lastStatusMessage != statusText) ...[
              const SizedBox(height: 4),
              Text(
                _lastStatusMessage!,
                textAlign: TextAlign.center,
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.slate,
                ),
              ),
            ],
            const SizedBox(height: 20),
            // The two counts, on one sand tile.
            FwCard.sand(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
              child: Column(
                children: [
                  _SyncInfoRow(
                    icon: Icons.cloud_upload_outlined,
                    label: l10n.syncPending,
                    value: '$_pendingCount',
                    color: _pendingCount > 0
                        ? AppTheme.statusWarning
                        : AppTheme.slate,
                  ),
                  const SizedBox(height: 10),
                  // Failed operations row
                  _SyncInfoRow(
                    icon: Icons.error_outline,
                    label: l10n.syncFailed,
                    value: '$_failedCount',
                    color: _failedCount > 0
                        ? AppTheme.statusError
                        : AppTheme.slate,
                  ),
                ],
              ),
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
                            color: AppTheme.ink,
                          ),
                        )
                      : const Icon(Icons.refresh, size: 20),
                  label: Text(_isSyncing ? l10n.syncInProgress : l10n.retry),
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
                            color: AppTheme.ink,
                          ),
                        )
                      : const Icon(Icons.sync, size: 20),
                  label: Text(
                    _isSyncing ? l10n.syncInProgress : l10n.syncNow,
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
        Expanded(
          child: Text(
            label,
            style: const TextStyle(
              fontSize: 14,
              fontWeight: FontWeight.w500,
              color: AppTheme.inkSoft,
            ),
          ),
        ),
        const SizedBox(width: 10),
        Text(
          value,
          style: TextStyle(
            fontSize: 17,
            fontWeight: FontWeight.w800,
            color: color == AppTheme.slate ? AppTheme.ink : color,
          ),
        ),
      ],
    );
  }
}
