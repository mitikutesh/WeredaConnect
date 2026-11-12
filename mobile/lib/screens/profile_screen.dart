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
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_executor_web.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_queue_web.dart';
import 'package:wereda_connect/features/account/presentation/screens/delete_account_screen.dart';
import 'package:wereda_connect/features/account/presentation/screens/edit_profile_screen.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/fayda/screens/fayda_verification_screen.dart';
import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';
import 'package:wereda_connect/features/reputation/domain/own_rating.dart';
import 'package:wereda_connect/features/reputation/presentation/screens/my_reputation_screen.dart';
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

  /// What a logout would delete before it reached the server (E-1).
  /// Defaults to [AuthBloc.unsentWorkFor]; tests inject one.
  final Future<LogoutUnsentWork> Function(String userId)? loadUnsentWork;

  /// Sends the queued changes now (the logout dialog's Sync now). Defaults
  /// to the app's sync queue and executor (mobile); tests inject one.
  final Future<void> Function()? syncUnsentWork;

  /// Whether the phone has a network, for the logout dialog's wording.
  /// Defaults to the app's connectivity monitor; tests inject one.
  final Future<bool> Function()? isOnline;

  /// ESC-31: reads the signed-in user's own rating for the Rating tile
  /// (null: it could not be read). Defaults to [loadOwnRating] over the
  /// app's repositories; tests inject one.
  final Future<OwnRating?> Function(String userId, UserRole role)?
      loadOwnRating;

  /// ESC-31: the user's own reputation, for the default rating read and
  /// the My reputation screen. Defaults to the app's; tests inject one.
  final ReputationRepository? reputationRepository;

  /// How long the logout dialog's Sync now may run before the counts are
  /// read again (the sync itself goes on).
  static const logoutSyncTimeout = Duration(seconds: 20);

  const ProfileScreen({
    this.loadUser,
    this.loadOwnRating,
    this.reputationRepository,
    this.appLockService,
    this.loadUnsentWork,
    this.syncUnsentWork,
    this.isOnline,
    super.key,
  });

  @override
  State<ProfileScreen> createState() => _ProfileScreenState();
}

class _ProfileScreenState extends State<ProfileScreen> {
  int _rebuildKey = 0;
  Future<Map<String, String>>? _statsFuture;
  String? _statsUserId;
  UserRole? _statsRole;

  /// ESC-31: the user's own rating; null while it is being read or when it
  /// cannot be read (the tile then keeps the dash).
  OwnRating? _ownRating;

  static const _fallbackStats = {'trades': '0', 'rating': '-', 'escrow': '0'};

  /// The app's reputation repository, looked up once outside a build; null
  /// where none is provided (a test that does not need it).
  ReputationRepository? _reputationRepository;

  ReputationRepository? _findReputationRepository() {
    final injected = widget.reputationRepository;
    if (injected != null) return injected;
    try {
      return context.read<ReputationRepository>();
    } on Object {
      return null;
    }
  }

  BaseRatingRepository? get _ratingRepository {
    try {
      return context.read<RatingBloc>().repository;
    } on Object {
      return null;
    }
  }

  /// Reads the user's own rating once per user and role, beside the local
  /// stats: a slow network never holds the rest of the profile back.
  Future<void> _readOwnRating(String userId, UserRole role) async {
    // Started from a build: look the providers up after it.
    await Future<void>.value();
    if (!mounted) return;
    _reputationRepository ??= _findReputationRepository();
    final load = widget.loadOwnRating ??
        (String userId, UserRole role) => loadOwnRating(
              userId: userId,
              role: role,
              reputation: _reputationRepository,
              ratings: _ratingRepository,
            );
    OwnRating? rating;
    try {
      rating = await load(userId, role);
    } on Object {
      rating = null;
    }
    if (!mounted || _statsUserId != userId || _statsRole != role) return;
    setState(() => _ownRating = rating);
  }

  /// The Rating tile: a farmer's or trucker's own average, a neutral word
  /// when nobody has rated them yet, and the dash while it is unknown and
  /// for a buyer (buyers are never rated, so no rating is coming).
  String _ratingText(AppLocalizations l10n) {
    final rating = _ownRating;
    if (rating == null || !rating.known) return '-';
    return rating.hasRating
        ? rating.average!.toStringAsFixed(1)
        : l10n.profileRatingNone;
  }

  void _openMyReputation(BuildContext context, String userId) {
    final repository = _reputationRepository;
    if (repository == null) return;
    Navigator.push(
      context,
      MaterialPageRoute<void>(
        builder: (_) =>
            MyReputationScreen(userId: userId, repository: repository),
      ),
    );
  }

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
                      _StatsCard(stats: stats, rating: _ratingText(l10n)),
                      const SizedBox(height: 22),
                      ..._menuSections(
                        context,
                        l10n,
                        stats,
                        isVerified: isVerified,
                        // ESC-31: a farmer's or trucker's own reputation,
                        // once the server says penalties are switched on.
                        // Buyers have none.
                        myReputationUserId: authState.role != UserRole.buyer &&
                                (_ownRating?.penaltiesEnabled ?? false) &&
                                _reputationRepository != null
                            ? authState.userId
                            : null,
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
    String? myReputationUserId,
  }) {
    final trades = stats['trades'] ?? '0';
    return [
      FwSectionLabel(l10n.profileSectionAccount),
      const SizedBox(height: 8),
      _RowGroup(
        // Device lock on app open (hidden on web / devices without a lock).
        trailing: _AppLockRow(service: widget.appLockService),
        children: [
          // SIM-56: the name, woreda and a company buyer's company name.
          _ProfileRow(
            key: const Key('profile_edit'),
            icon: Icons.edit_outlined,
            title: l10n.profileEditTitle,
            subtitle: l10n.profileEditSubtitle,
            onTap: () => _openEditProfile(context),
          ),
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
          if (myReputationUserId != null)
            _ProfileRow(
              key: const Key('profile_my_reputation'),
              icon: Icons.workspace_premium_outlined,
              title: l10n.reputationMyTitle,
              subtitle: l10n.reputationMySubtitle,
              onTap: () => _openMyReputation(context, myReputationUserId),
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
      _ownRating = null;
      _readOwnRating(userId, role);
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

  /// Open the profile edit; read the saved name again on return.
  Future<void> _openEditProfile(BuildContext context) async {
    await Navigator.of(context).push<bool>(
      MaterialPageRoute<bool>(builder: (_) => const EditProfileScreen()),
    );
    if (mounted) setState(() => _rebuildKey++);
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
      // role's own id field (mirrors the web path's idParam scoping). Each id
      // field is indexed (truckerId since ESC-22), so this reads only the
      // user's own orders.
      final base = isar.orders.where();
      final scoped = switch (role) {
        UserRole.farmer => base.farmerIdEqualTo(userId),
        UserRole.buyer => base.buyerIdEqualTo(userId),
        UserRole.trucker => base.truckerIdEqualTo(userId),
      };
      final escrowOrders = await scoped
          .filter()
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

  /// E-1: logout deletes this user's unsent changes from the phone (the
  /// shared-device protection), so the dialog first says how many there
  /// are, with Sync now and Log out anyway. With nothing unsent (always on
  /// web, where writes go straight to the API) it only asks.
  Future<void> _showLogoutConfirmation(
    BuildContext context,
    AppLocalizations l10n,
  ) async {
    final auth = context.read<AuthBloc>();
    final authState = auth.state;
    final userId = authState is AuthAuthenticated ? authState.userId : '';
    final load = widget.loadUnsentWork ?? auth.unsentWorkFor;
    Future<LogoutUnsentWork> unsent() async {
      try {
        return await load(userId);
      } on Object {
        // Unreadable: never block the logout on it.
        return (changes: 0, payments: 0);
      }
    }

    final syncNow = widget.syncUnsentWork ?? _appSync(context);
    final isOnline = widget.isOnline ?? _appConnectivity(context);
    final work = await unsent();
    // Only the warning says offline.
    final online = !_hasUnsent(work) || await isOnline();
    if (!context.mounted) return;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => _LogoutDialog(
        work: work,
        online: online,
        reload: unsent,
        syncNow: syncNow,
        isOnline: isOnline,
      ),
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

  static bool _hasUnsent(LogoutUnsentWork work) =>
      work.changes > 0 || work.payments > 0;

  /// The app's Sync now: failed changes are retried, then the queue is sent
  /// (the sync status sheet's Retry). Nothing on web, which has no queue.
  static Future<void> Function() _appSync(BuildContext context) {
    SyncQueue? queue;
    SyncExecutor? executor;
    if (!kIsWeb) {
      try {
        queue = context.read<SyncQueue>();
        executor = context.read<SyncExecutor>();
      } on Object {
        // No sync above this screen.
      }
    }
    return () async {
      await queue?.retryFailed();
      await executor?.syncNow();
    };
  }

  /// The app's connectivity (the device's network, no request); online
  /// when unknown, so the dialog never claims an offline it cannot see.
  static Future<bool> Function() _appConnectivity(BuildContext context) {
    ConnectivityMonitor? monitor;
    try {
      monitor = context.read<ConnectivityMonitor>();
    } on Object {
      monitor = null;
    }
    return () async {
      try {
        return await monitor?.isConnected() ?? true;
      } on Object {
        return true;
      }
    };
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

/// Trades, rating and ETB in escrow side by side. [rating] is the user's
/// own (ESC-31).
class _StatsCard extends StatelessWidget {
  final Map<String, String> stats;
  final String rating;

  const _StatsCard({required this.stats, required this.rating});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return FwCard(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      child: Row(
        children: [
          _StatColumn(value: stats['trades'] ?? '0', label: l10n.trades),
          _verticalDivider(),
          _StatColumn(
            key: const Key('profile_rating'),
            value: rating,
            label: l10n.rating,
          ),
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

  const _StatColumn({required this.value, required this.label, super.key});

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

/// The logout confirmation (E-1). With unsent work: how many changes are
/// not sent yet and how many payments waiting to be sent will be
/// cancelled, that logging in again needs internet when offline, then
/// Cancel, Sync now (with queued changes) and Log out anyway. Logout waits while Sync now runs
/// (at most [ProfileScreen.logoutSyncTimeout]); the counts are then read
/// again, and once nothing is left the dialog only asks. Pops true to log
/// out.
class _LogoutDialog extends StatefulWidget {
  final LogoutUnsentWork work;
  final bool online;
  final Future<LogoutUnsentWork> Function() reload;
  final Future<void> Function() syncNow;
  final Future<bool> Function() isOnline;

  const _LogoutDialog({
    required this.work,
    required this.online,
    required this.reload,
    required this.syncNow,
    required this.isOnline,
  });

  @override
  State<_LogoutDialog> createState() => _LogoutDialogState();
}

class _LogoutDialogState extends State<_LogoutDialog> {
  late LogoutUnsentWork _work = widget.work;
  late bool _online = widget.online;
  bool _syncing = false;

  bool get _unsent => _work.changes > 0 || _work.payments > 0;

  Future<void> _sync() async {
    setState(() => _syncing = true);
    try {
      await widget.syncNow().timeout(ProfileScreen.logoutSyncTimeout);
    } on Object {
      // Timed out or failed: the counts read below say what is left.
    }
    final work = await widget.reload();
    final online = await widget.isOnline();
    if (!mounted) return;
    setState(() {
      _work = work;
      _online = online;
      _syncing = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final lines = [
      if (!_unsent) l10n.logoutConfirmation,
      if (_work.changes > 0) l10n.logoutUnsentChanges(_work.changes),
      if (_work.payments > 0) l10n.logoutUnpaidPayments(_work.payments),
      if (_unsent && !_online) l10n.logoutOfflineNeedsInternet,
    ];
    return AlertDialog(
      title: Text(l10n.logout),
      content: Column(
        key: const Key('logout_dialog_text'),
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          for (var i = 0; i < lines.length; i++)
            Padding(
              padding: EdgeInsets.only(top: i == 0 ? 0 : 8),
              child: Text(lines[i]),
            ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(false),
          child: Text(l10n.cancel),
        ),
        // Only queued changes: a payment waiting to be sent goes with the
        // escrow sync on reconnect, which this button does not drive.
        if (_work.changes > 0)
          TextButton(
            key: const Key('logout_sync_now'),
            onPressed: _syncing ? null : _sync,
            child: _syncing
                ? Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const SizedBox(
                        width: 14,
                        height: 14,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                      const SizedBox(width: 8),
                      Text(l10n.syncInProgress),
                    ],
                  )
                : Text(l10n.syncNow),
          ),
        TextButton(
          key: const Key('logout_confirm'),
          onPressed: _syncing ? null : () => Navigator.of(context).pop(true),
          style: TextButton.styleFrom(foregroundColor: AppTheme.statusError),
          child: Text(_unsent ? l10n.logoutAnyway : l10n.logout),
        ),
      ],
    );
  }
}
