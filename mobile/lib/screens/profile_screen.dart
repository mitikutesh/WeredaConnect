import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:intl/intl.dart';
// The device build gets isar via isar_service.dart's re-export, but the web
// build swaps in isar_service_web.dart which does NOT re-export it — keep the
// direct import so QAfterFilterCondition resolves on both platforms.
// ignore: unnecessary_import
import 'package:isar/isar.dart' show QAfterFilterCondition;
import 'package:wereda_connect/core/auth/offline_auth_cache.dart'
    if (dart.library.html) 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/payment_intent.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/account/presentation/screens/delete_account_screen.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/fayda/screens/fayda_verification_screen.dart';
import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';
import 'package:wereda_connect/screens/settings_screen.dart';

/// Profile screen in the Fieldway boards' language (no board of its own):
/// the page header with the role as eyebrow and a gear to Settings, a white
/// identity card (initials, name, role pill, Verified chip), the three
/// stats, then the menu as white card groups under uppercase section labels
/// and the logout button. One composition at every width, centred by
/// [FwContentWidth] on wide windows.
///
/// Absorbs settings/about/logout that were previously in the drawer.
class ProfileScreen extends StatefulWidget {
  /// Loads the signed-in user (Fayda badge). Defaults to Isar on mobile and
  /// the cached user on web; tests inject one.
  final Future<User?> Function(AuthAuthenticated authState)? loadUser;

  /// App lock settings behind the switch; tests inject one.
  final AppLockService? appLockService;

  const ProfileScreen({this.loadUser, this.appLockService, super.key});

  @override
  State<ProfileScreen> createState() => _ProfileScreenState();
}

class _ProfileScreenState extends State<ProfileScreen> {
  int _rebuildKey = 0;
  Future<Map<String, String>>? _statsFuture;
  String? _statsUserId;
  UserRole? _statsRole;

  static const _fallbackStats = {'trades': '0', 'rating': '-', 'escrow': '0'};

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        // When user logs out while on profile, pop back to auth gate, also
        // past a screen opened from here (Delete account).
        if (state is! AuthAuthenticated) {
          Navigator.of(context).popUntil((route) => route.isFirst);
        }
      },
      child: BlocBuilder<AuthBloc, AuthState>(
        builder: (context, authState) {
          if (authState is AuthAuthenticated) {
            return _buildProfile(context, authState, l10n);
          }
          return _buildUnauthenticated(context, l10n);
        },
      ),
    );
  }

  Widget _buildProfile(
    BuildContext context,
    AuthAuthenticated authState,
    AppLocalizations l10n,
  ) {
    return FutureBuilder<User?>(
      key: ValueKey(_rebuildKey),
      future: (widget.loadUser ?? _getUser)(authState),
      builder: (context, snapshot) {
        final user = snapshot.data;
        final name = user?.name ?? authState.userName;
        final role = user?.role ?? authState.role;
        final initials = name.isNotEmpty ? name[0].toUpperCase() : '?';
        final isVerified = user?.isVerified ?? false;

        return Scaffold(
          body: SafeArea(
            child: FwContentWidth(
              child: FutureBuilder<Map<String, String>>(
                future: _getStatsFuture(authState.userId, authState.role),
                builder: (context, snap) {
                  final stats = snap.data ?? _fallbackStats;
                  return ListView(
                    padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
                    children: [
                      // Pushed from Home on the phone; a tab on the web.
                      if (Navigator.of(context).canPop()) ...[
                        const Align(
                          alignment: AlignmentDirectional.centerStart,
                          child: JoinBackButton(),
                        ),
                        const SizedBox(height: 12),
                      ],
                      FwPageHeader(
                        padding: EdgeInsets.zero,
                        eyebrow: _getRoleLabel(role, l10n),
                        title: l10n.profile,
                        trailing: FwSquareButton(
                          semanticLabel: l10n.settings,
                          onTap: () => _openSettings(context),
                          child: const Icon(
                            Icons.settings_outlined,
                            size: 20,
                            color: AppTheme.soilDark,
                          ),
                        ),
                      ),
                      const SizedBox(height: 16),
                      _IdentityCard(
                        initials: initials,
                        name: name,
                        role: role,
                        roleLabel: _getRoleLabel(role, l10n),
                        isVerified: isVerified,
                      ),
                      const SizedBox(height: 12),
                      _StatsCard(stats: stats),
                      const SizedBox(height: 22),
                      ..._menuSections(
                        context,
                        l10n,
                        stats,
                        isVerified: isVerified,
                      ),
                      const SizedBox(height: 24),
                      _buildLogoutButton(context, l10n),
                    ],
                  );
                },
              ),
            ),
          ),
        );
      },
    );
  }

  void _openSettings(BuildContext context) {
    Navigator.push(
      context,
      MaterialPageRoute<void>(builder: (_) => const SettingsScreen()),
    );
  }

  /// The menu as card groups: Account, Money, App, then Delete account.
  List<Widget> _menuSections(
    BuildContext context,
    AppLocalizations l10n,
    Map<String, String> stats, {
    bool isVerified = false,
  }) {
    final trades = stats['trades'] ?? '0';
    return [
      FwSectionLabel(l10n.profileSectionAccount),
      const SizedBox(height: 8),
      _RowGroup(
        // Device lock on app open (hidden on web / devices without a lock).
        trailing: _AppLockRow(service: widget.appLockService),
        children: [
          // Link-later Fayda: needed for payments above 10,000 ETB.
          if (!isVerified)
            _ProfileRow(
              key: const Key('profile_verify_fayda'),
              icon: Icons.verified_user_outlined,
              title: l10n.verifyWithFayda,
              subtitle: l10n.faydaNotVerifiedSubtitle,
              iconColor: AppTheme.money,
              iconBackground: AppTheme.actionCream,
              onTap: () => _openFaydaVerification(context),
            ),
          _ProfileRow(
            icon: Icons.description_outlined,
            title: l10n.documents,
            subtitle: l10n.idAndCerts,
            onTap: () => _showComingSoon(context, l10n),
          ),
        ],
      ),
      const SizedBox(height: 20),
      FwSectionLabel(l10n.profileSectionMoney),
      const SizedBox(height: 8),
      _RowGroup(
        children: [
          _ProfileRow(
            icon: Icons.account_balance_wallet_outlined,
            title: l10n.wallet,
            subtitle: '$trades ${l10n.trades}',
            onTap: () => _openPaymentHistory(context),
          ),
          _ProfileRow(
            icon: Icons.history,
            title: l10n.escrowHistory,
            subtitle: '${stats['escrow'] ?? '0'} ETB',
            onTap: () => _openPaymentHistory(context),
          ),
        ],
      ),
      const SizedBox(height: 20),
      FwSectionLabel(l10n.profileSectionApp),
      const SizedBox(height: 8),
      _RowGroup(
        children: [
          _ProfileRow(
            icon: Icons.settings_outlined,
            title: l10n.settings,
            subtitle: l10n.languageAndAlerts,
            onTap: () => _openSettings(context),
          ),
          _ProfileRow(
            icon: Icons.help_outline,
            title: l10n.help,
            subtitle: l10n.faqAndSupport,
            onTap: () => _showComingSoon(context, l10n),
          ),
          if (kDebugMode)
            _ProfileRow(
              icon: Icons.delete_sweep,
              title: 'Reset Test Data',
              subtitle: 'Clear all business data',
              iconColor: AppTheme.statusWarning,
              iconBackground: AppTheme.statusWarningContainer,
              onTap: () => _showResetTestData(context),
            ),
        ],
      ),
      const SizedBox(height: 20),
      // Same entry for every role (account-deletion.md).
      _RowGroup(
        children: [
          _ProfileRow(
            key: const Key('profile_delete_account'),
            icon: Icons.person_remove_outlined,
            title: l10n.deleteAccount,
            subtitle: l10n.deleteAccountSubtitle,
            iconColor: AppTheme.statusError,
            iconBackground: AppTheme.statusErrorContainer,
            onTap: () {
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (_) => const DeleteAccountScreen(),
                ),
              );
            },
          ),
        ],
      ),
    ];
  }

  void _openPaymentHistory(BuildContext context) {
    Navigator.push(
      context,
      MaterialPageRoute<void>(builder: (_) => const PaymentHistoryScreen()),
    );
  }

  /// The logout button: the boards' secondary button with red ink.
  Widget _buildLogoutButton(BuildContext context, AppLocalizations l10n) {
    return OutlinedButton.icon(
      onPressed: () => _showLogoutConfirmation(context, l10n),
      style: OutlinedButton.styleFrom(foregroundColor: AppTheme.statusError),
      icon: const Icon(Icons.logout, size: 18),
      label: Text(l10n.logout),
    );
  }

  void _showComingSoon(BuildContext context, AppLocalizations l10n) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(l10n.comingSoon),
        duration: const Duration(seconds: 2),
      ),
    );
  }

  Widget _buildUnauthenticated(BuildContext context, AppLocalizations l10n) {
    return Scaffold(
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(
              Icons.person_off_outlined,
              size: 80,
              color: AppTheme.textMuted,
            ),
            const SizedBox(height: 24),
            Text(
              l10n.notLoggedIn,
              style: const TextStyle(
                fontSize: 18,
                fontWeight: FontWeight.w600,
                color: AppTheme.textDark,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              l10n.profileRestartApp,
              style: const TextStyle(
                fontSize: 14,
                color: AppTheme.textMuted,
              ),
            ),
          ],
        ),
      ),
    );
  }

  Future<Map<String, String>> _getStatsFuture(String userId, UserRole role) {
    if (_statsFuture == null || _statsUserId != userId || _statsRole != role) {
      _statsUserId = userId;
      _statsRole = role;
      _statsFuture = _loadProfileStats(userId, role);
    }
    return _statsFuture!;
  }

  Future<User?> _getUser(AuthAuthenticated authState) async {
    if (kIsWeb) {
      // No Isar on web: the cached user JSON carries the verification badge.
      final phone = authState.phoneNumber;
      if (phone == null || phone.isEmpty) return null;
      try {
        return await OfflineAuthCache().getCachedUser(phone);
      } on Object {
        return null;
      }
    }
    try {
      final isar = IsarService.instance.db;
      return await isar.users
          .where()
          .userIdEqualTo(authState.userId)
          .findFirst();
    } on Object {
      return null;
    }
  }

  /// Open Fayda link-later verification; refresh the badge on return.
  Future<void> _openFaydaVerification(BuildContext context) async {
    await Navigator.of(context).push<bool>(
      MaterialPageRoute<bool>(builder: (_) => const FaydaVerificationScreen()),
    );
    if (mounted) setState(() => _rebuildKey++);
  }

  static final NumberFormat _etbFormat = NumberFormat('#,##0');

  static String _formatEtb(double amount) => _etbFormat.format(amount);

  /// ETB this user has locked in escrow on one order, own leg only: the
  /// buyer's full held total, the farmer's crop cost, the trucker's transport
  /// fee (SIM-15).
  static double _escrowStake(UserRole role, Order order) {
    final subtotal = order.quantity * order.pricePerUnit;
    return switch (role) {
      UserRole.trucker => order.transportCost ?? 0,
      UserRole.farmer =>
        farmerEscrowShare(cropCost: order.cropCost, subtotal: subtotal),
      UserRole.buyer =>
        orderEscrowValue(orderTotal: order.orderTotal, subtotal: subtotal),
    };
  }

  Future<Map<String, String>> _loadProfileStats(
    String userId,
    UserRole role,
  ) async {
    if (kIsWeb) return _loadWebProfileStats(userId, role);
    try {
      final isar = IsarService.instance.db;
      // Count deliveries involving this user (any role)
      final deliveries = await isar.deliverys
          .where()
          .filter()
          .farmerIdEqualTo(userId)
          .or()
          .buyerIdEqualTo(userId)
          .or()
          .truckerIdEqualTo(userId)
          .count();
      // Count payments involving this user (any role)
      final payments = await isar.payments
          .where()
          .filter()
          .farmerIdEqualTo(userId)
          .or()
          .buyerIdEqualTo(userId)
          .count();
      final total = deliveries + payments;
      // ETB currently locked in escrow on this user's orders, scoped to the
      // role's own id field (mirrors the web path's idParam scoping).
      final base = isar.orders.filter();
      final scoped = switch (role) {
        UserRole.farmer => base.farmerIdEqualTo(userId),
        UserRole.buyer => base.buyerIdEqualTo(userId),
        UserRole.trucker => base.truckerIdEqualTo(userId),
      };
      final escrowOrders = await scoped
          .anyOf<OrderStatus, QAfterFilterCondition>(
            escrowHeldOrderStatuses,
            (q, s) => q.statusEqualTo(s),
          )
          .findAll();
      final escrowEtb = escrowOrders.fold<double>(
        0,
        (sum, o) => sum + _escrowStake(role, o),
      );
      return {
        'trades': '$total',
        'rating': '-',
        'escrow': _formatEtb(escrowEtb),
      };
    } on Object {
      return _fallbackStats;
    }
  }

  /// Web has no Isar — sum escrow-held order values from the API instead.
  /// Trades/rating stay placeholders (no local history on web).
  Future<Map<String, String>> _loadWebProfileStats(
    String userId,
    UserRole role,
  ) async {
    const fallback = _fallbackStats;
    try {
      final api = ApiClient();
      if (!api.isInitialized) return fallback;
      final idParam = switch (role) {
        UserRole.farmer => 'farmerId',
        UserRole.buyer => 'buyerId',
        UserRole.trucker => 'truckerId',
      };
      // Follow nextToken pagination so the money sum doesn't silently
      // truncate at one page; 5 pages (250 orders) bounds the cost on 3G.
      final items = <dynamic>[];
      String? nextToken;
      for (var page = 0; page < 5; page++) {
        final data = await api.get<Map<String, dynamic>>(
          '/orders',
          queryParameters: {
            idParam: userId,
            'limit': '50',
            if (nextToken != null) 'nextToken': nextToken,
          },
        );
        final list = data['orders'] ?? data['items'];
        if (list is List) items.addAll(list);
        final token = data['nextToken'];
        if (token is! String || token.isEmpty) break;
        nextToken = token;
      }
      var escrowEtb = 0.0;
      for (final item in items) {
        if (item is! Map) continue;
        final status = item['status'];
        if (status is! String) continue;
        if (!escrowHeldOrderStatusNames.contains(orderStatusFromWire(status))) {
          continue;
        }
        final subtotal =
            wireDouble(item['quantity']) * wireDouble(item['pricePerUnit']);
        escrowEtb += switch (role) {
          UserRole.trucker => wireDouble(item['transportCost']),
          UserRole.farmer => farmerEscrowShare(
              cropCost: wireDouble(item['cropCost']),
              subtotal: subtotal,
            ),
          UserRole.buyer => orderEscrowValue(
              orderTotal: wireDouble(item['orderTotal']),
              subtotal: subtotal,
            ),
        };
      }
      return {'trades': '0', 'rating': '-', 'escrow': _formatEtb(escrowEtb)};
    } on Object {
      return fallback;
    }
  }

  String _getRoleLabel(UserRole role, AppLocalizations l10n) {
    return switch (role) {
      UserRole.farmer => l10n.farmer,
      UserRole.trucker => l10n.trucker,
      UserRole.buyer => l10n.buyer,
    };
  }

  Future<void> _showLogoutConfirmation(
    BuildContext context,
    AppLocalizations l10n,
  ) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: Text(l10n.logout),
          content: Text(l10n.logoutConfirmation),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(false),
              child: Text(l10n.cancel),
            ),
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(true),
              style: TextButton.styleFrom(
                foregroundColor: AppTheme.statusError,
              ),
              child: Text(l10n.logout),
            ),
          ],
        );
      },
    );
    // Only logout when the user explicitly confirms.
    // Escape key or barrier tap returns null, which is not true.
    if (confirmed == true && context.mounted) {
      // Explicit logout MUST clear the offline credential cache (tokens + PBKDF2
      // password hash) — auth-api.md invariant 7, stolen-device mitigation. The
      // AuthLogoutRequested default (clearCache: true) does exactly this; passing
      // clearCache:false here previously defeated it.
      context.read<AuthBloc>().add(const AuthLogoutRequested());
    }
  }

  void _showResetTestData(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: const Text('Reset Test Data?'),
          content: const Text(
            'This will delete all listings, trips, deliveries, and payments. '
            'Your registered accounts will be preserved.',
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: const Text('Cancel'),
            ),
            TextButton(
              onPressed: () async {
                Navigator.of(dialogContext).pop();
                if (kIsWeb) {
                  // No local data to reset on web
                  if (context.mounted) Navigator.pop(context);
                  return;
                }
                final messenger = ScaffoldMessenger.of(context);
                try {
                  final isar = IsarService.instance.db;
                  await isar.writeTxn(() async {
                    await isar.cropListings.clear();
                    await isar.trips.clear();
                    await isar.deliverys.clear();
                    await isar.orders.clear();
                    await isar.payments.clear();
                    await isar.paymentIntents.clear();
                  });
                  if (mounted) {
                    messenger.showSnackBar(
                      const SnackBar(
                        content: Text('Test data cleared successfully'),
                      ),
                    );
                  }
                } on Exception catch (e) {
                  if (mounted) {
                    messenger.showSnackBar(
                      SnackBar(
                        content: Text('Reset failed: $e'),
                        backgroundColor: AppTheme.statusError,
                      ),
                    );
                  }
                }
              },
              style:
                  TextButton.styleFrom(foregroundColor: AppTheme.statusWarning),
              child: const Text('Reset'),
            ),
          ],
        );
      },
    );
  }
}

/// The white identity card: initials, name, the role pill in the role's
/// colours (as the Welcome board's pills) and the Verified chip.
class _IdentityCard extends StatelessWidget {
  final String initials;
  final String name;
  final UserRole role;
  final String roleLabel;
  final bool isVerified;

  const _IdentityCard({
    required this.initials,
    required this.name,
    required this.role,
    required this.roleLabel,
    required this.isVerified,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final (pillBackground, pillForeground) = switch (role) {
      UserRole.farmer => (AppTheme.fieldGreenTint, AppTheme.fieldGreen),
      UserRole.trucker => (AppTheme.actionCream, AppTheme.money),
      UserRole.buyer => (AppTheme.dropoffBlueTint, AppTheme.dropoffBlue),
    };
    return FwCard(
      child: Row(
        children: [
          CircleAvatar(
            radius: 28,
            backgroundColor: pillBackground,
            child: Text(
              initials,
              style: TextStyle(
                fontSize: 22,
                fontWeight: FontWeight.w800,
                color: pillForeground,
              ),
            ),
          ),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: FwText.cardTitle.copyWith(fontSize: 17),
                ),
                const SizedBox(height: 6),
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    FwPill(
                      label: roleLabel,
                      background: pillBackground,
                      foreground: pillForeground,
                    ),
                    if (isVerified)
                      StatusChip(
                        label: l10n.verified,
                        kind: StatusKind.success,
                        icon: Icons.verified,
                      ),
                  ],
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// Trades, rating and ETB in escrow side by side.
class _StatsCard extends StatelessWidget {
  final Map<String, String> stats;

  const _StatsCard({required this.stats});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return FwCard(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      child: Row(
        children: [
          _StatColumn(value: stats['trades'] ?? '0', label: l10n.trades),
          _verticalDivider(),
          _StatColumn(value: stats['rating'] ?? '-', label: l10n.rating),
          _verticalDivider(),
          _StatColumn(value: stats['escrow'] ?? '0', label: l10n.inEscrowEtb),
        ],
      ),
    );
  }

  Widget _verticalDivider() {
    return Container(
      width: 1,
      height: 32,
      margin: const EdgeInsets.symmetric(horizontal: 12),
      color: AppTheme.whisperBorder,
    );
  }
}

class _StatColumn extends StatelessWidget {
  final String value;
  final String label;

  const _StatColumn({required this.value, required this.label});

  @override
  Widget build(BuildContext context) {
    return Expanded(
      child: Column(
        children: [
          // Scale down instead of wrapping — escrow ETB sums can be long.
          FittedBox(
            fit: BoxFit.scaleDown,
            child: Text(
              value,
              maxLines: 1,
              style: const TextStyle(
                fontSize: 18,
                fontWeight: FontWeight.w800,
                color: AppTheme.soilDark,
              ),
            ),
          ),
          const SizedBox(height: 2),
          Text(
            label,
            textAlign: TextAlign.center,
            style: const TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: AppTheme.mutedSteel,
            ),
          ),
        ],
      ),
    );
  }
}

/// A white card holding rows separated by whisper dividers. [trailing] is
/// appended without one: a row that may render nothing (the app lock on
/// web) draws its own divider, so no stray line is left behind.
class _RowGroup extends StatelessWidget {
  final List<Widget> children;
  final Widget? trailing;

  const _RowGroup({required this.children, this.trailing});

  @override
  Widget build(BuildContext context) {
    return FwCard(
      padding: EdgeInsets.zero,
      child: Column(
        children: [
          for (var i = 0; i < children.length; i++) ...[
            if (i > 0) const Divider(indent: 16, endIndent: 16),
            children[i],
          ],
          if (trailing != null) trailing!,
        ],
      ),
    );
  }
}

/// A menu row: icon tile, title and subtitle, chevron. At least 56 tall.
class _ProfileRow extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;
  final Color? iconColor;
  final Color? iconBackground;

  const _ProfileRow({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
    this.iconColor,
    this.iconBackground,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      child: Container(
        constraints: const BoxConstraints(minHeight: 56),
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
        child: Row(
          children: [
            Container(
              width: 36,
              height: 36,
              decoration: BoxDecoration(
                color: iconBackground ?? AppTheme.fieldGreenTint,
                borderRadius: BorderRadius.circular(10),
              ),
              child: Icon(
                icon,
                size: 20,
                color: iconColor ?? AppTheme.fieldGreen,
              ),
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    title,
                    style: const TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w700,
                      color: AppTheme.soilDark,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    subtitle,
                    style: const TextStyle(
                      fontSize: 12,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.mutedSteel,
                    ),
                  ),
                ],
              ),
            ),
            const Icon(
              Icons.chevron_right,
              size: 20,
              color: AppTheme.mutedSteel,
            ),
          ],
        ),
      ),
    );
  }
}

/// "App lock" switch. Hidden on web and on devices that cannot authenticate
/// the user (no lock screen). Turning it off needs a device check first.
class _AppLockRow extends StatefulWidget {
  final AppLockService? service;

  const _AppLockRow({this.service});

  @override
  State<_AppLockRow> createState() => _AppLockRowState();
}

class _AppLockRowState extends State<_AppLockRow> {
  late final AppLockService _service = widget.service ?? AppLockService();
  bool? _supported;
  bool _enabled = false;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final supported = await _service.isSupported();
    final enabled = supported && await _service.isEnabled();
    if (!mounted) return;
    setState(() {
      _supported = supported;
      _enabled = enabled;
    });
  }

  Future<void> _toggle(bool value) async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      if (!value) {
        final reason = AppLocalizations.of(context)!.appLockReason;
        if (!await _service.unlock(reason)) return;
      }
      await _service.setEnabled(enabled: value);
      if (mounted) setState(() => _enabled = value);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_supported != true) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context)!;
    return Column(
      children: [
        const Divider(indent: 16, endIndent: 16),
        SwitchListTile(
          key: const Key('profile_app_lock_switch'),
          contentPadding: const EdgeInsets.fromLTRB(16, 2, 12, 2),
          secondary: Container(
            width: 36,
            height: 36,
            decoration: BoxDecoration(
              color: AppTheme.fieldGreenTint,
              borderRadius: BorderRadius.circular(10),
            ),
            child: const Icon(
              Icons.lock_outline,
              size: 20,
              color: AppTheme.fieldGreen,
            ),
          ),
          title: Text(
            l10n.appLockTitle,
            style: const TextStyle(
              fontSize: 14,
              fontWeight: FontWeight.w700,
              color: AppTheme.soilDark,
            ),
          ),
          subtitle: Text(
            l10n.appLockSubtitle,
            style: const TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: AppTheme.mutedSteel,
            ),
          ),
          value: _enabled,
          onChanged: _busy ? null : _toggle,
        ),
      ],
    );
  }
}
