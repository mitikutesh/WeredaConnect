import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/payment_intent.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/screens/settings_screen.dart';

/// Profile screen with green header, stats bar, and menu items.
///
/// Absorbs settings/about/logout that were previously in the drawer.
class ProfileScreen extends StatefulWidget {
  const ProfileScreen({super.key});

  @override
  State<ProfileScreen> createState() => _ProfileScreenState();
}

class _ProfileScreenState extends State<ProfileScreen> {
  final int _rebuildKey = 0;
  Future<Map<String, String>>? _statsFuture;
  String? _statsUserId;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        // When user logs out while on profile, pop back to auth gate
        if (state is! AuthAuthenticated && Navigator.of(context).canPop()) {
          Navigator.of(context).pop();
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
      future: _getUser(authState.userId),
      builder: (context, snapshot) {
        final user = snapshot.data;
        final name = user?.name ?? authState.userName;
        final role = user?.role ?? authState.role;
        final initials = name.isNotEmpty ? name[0].toUpperCase() : '?';

        return Scaffold(
          body: SingleChildScrollView(
            child: Column(
              children: [
                // ── Green header with avatar + back button ─────────────
                GreenHeader(
                  height: 210,
                  child: Column(
                    children: [
                      // Back button row
                      Align(
                        alignment: Alignment.centerLeft,
                        child: GestureDetector(
                          onTap: () => Navigator.of(context).pop(),
                          child: const Padding(
                            padding: EdgeInsets.only(top: 4),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                Icon(
                                  Icons.arrow_back_ios_new,
                                  size: 18,
                                  color: Colors.white,
                                ),
                                SizedBox(width: 4),
                                Text(
                                  'Back',
                                  style: TextStyle(
                                    fontSize: 14,
                                    color: Colors.white,
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                      const SizedBox(height: 4),
                      // Avatar
                      CircleAvatar(
                        radius: 36,
                        backgroundColor: AppTheme.accentGold,
                        child: Text(
                          initials,
                          style: const TextStyle(
                            fontSize: 28,
                            fontWeight: FontWeight.w700,
                            color: Colors.white,
                          ),
                        ),
                      ),
                      const SizedBox(height: 10),
                      // Name
                      Text(
                        name,
                        style: const TextStyle(
                          fontSize: 20,
                          fontWeight: FontWeight.w700,
                          color: Colors.white,
                        ),
                      ),
                      const SizedBox(height: 4),
                      // Role + verified badge
                      Row(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          Text(
                            _getRoleLabel(role, l10n),
                            style: TextStyle(
                              fontSize: 13,
                              color: Colors.white.withAlpha(200),
                            ),
                          ),
                          const SizedBox(width: 8),
                          Container(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 8,
                              vertical: 2,
                            ),
                            decoration: BoxDecoration(
                              color: AppTheme.accentGold,
                              borderRadius: BorderRadius.circular(10),
                            ),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                const Icon(
                                  Icons.verified,
                                  size: 12,
                                  color: Colors.white,
                                ),
                                const SizedBox(width: 2),
                                Text(
                                  l10n.verified,
                                  style: const TextStyle(
                                    fontSize: 11,
                                    fontWeight: FontWeight.w600,
                                    color: Colors.white,
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),

                // ── Stats bar (overlapping card) ─────────────────────────
                Transform.translate(
                  offset: const Offset(0, -16),
                  child: Container(
                    margin: const EdgeInsets.symmetric(horizontal: 20),
                    padding: const EdgeInsets.symmetric(
                      horizontal: 16,
                      vertical: 14,
                    ),
                    decoration: BoxDecoration(
                      color: AppTheme.surfaceWhite,
                      borderRadius: BorderRadius.circular(12),
                      border: Border.all(color: AppTheme.borderLight),
                      boxShadow: const [
                        BoxShadow(
                          color: Color(0x0D000000),
                          blurRadius: 8,
                          offset: Offset(0, 2),
                        ),
                      ],
                    ),
                    child: FutureBuilder<Map<String, String>>(
                      future: _getStatsFuture(authState.userId),
                      builder: (context, snap) {
                        final stats = snap.data ??
                            {'trades': '0', 'rating': '0.0', 'escrow': '0%'};
                        return Row(
                          children: [
                            _StatColumn(
                              value: stats['trades']!,
                              label: l10n.trades,
                            ),
                            _verticalDivider(),
                            _StatColumn(
                              value: stats['rating']!,
                              label: l10n.rating,
                            ),
                            _verticalDivider(),
                            _StatColumn(
                              value: stats['escrow']!,
                              label: l10n.escrowPercent,
                            ),
                          ],
                        );
                      },
                    ),
                  ),
                ),

                // ── Menu items ───────────────────────────────────────────
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 20),
                  child: Column(
                    children: [
                      _ProfileMenuItem(
                        icon: Icons.account_balance_wallet_outlined,
                        title: l10n.wallet,
                        subtitle: l10n.etbCurrency('0'),
                        onTap: () {
                          Navigator.push(
                            context,
                            MaterialPageRoute<void>(
                              builder: (_) => const PaymentHistoryScreen(),
                            ),
                          );
                        },
                      ),
                      const SizedBox(height: 8),
                      _ProfileMenuItem(
                        icon: Icons.history,
                        title: l10n.escrowHistory,
                        subtitle: '0 ${l10n.trades}',
                        onTap: () {
                          Navigator.push(
                            context,
                            MaterialPageRoute<void>(
                              builder: (_) => const PaymentHistoryScreen(),
                            ),
                          );
                        },
                      ),
                      const SizedBox(height: 8),
                      _ProfileMenuItem(
                        icon: Icons.star_outline,
                        title: l10n.ratings,
                        subtitle: '0.0',
                        onTap: () => _showComingSoon(context, l10n),
                      ),
                      const SizedBox(height: 8),
                      _ProfileMenuItem(
                        icon: Icons.description_outlined,
                        title: l10n.documents,
                        subtitle: l10n.idAndCerts,
                        onTap: () => _showComingSoon(context, l10n),
                      ),
                      const SizedBox(height: 8),
                      _ProfileMenuItem(
                        icon: Icons.settings_outlined,
                        title: l10n.settings,
                        subtitle: l10n.languageAndAlerts,
                        onTap: () {
                          Navigator.push(
                            context,
                            MaterialPageRoute<void>(
                              builder: (_) => const SettingsScreen(),
                            ),
                          );
                        },
                      ),
                      const SizedBox(height: 8),
                      _ProfileMenuItem(
                        icon: Icons.help_outline,
                        title: l10n.help,
                        subtitle: l10n.faqAndSupport,
                        onTap: () => _showComingSoon(context, l10n),
                      ),
                      if (kDebugMode) ...[
                        const SizedBox(height: 8),
                        _ProfileMenuItem(
                          icon: Icons.delete_sweep,
                          title: 'Reset Test Data',
                          subtitle: 'Clear all business data',
                          iconColor: Colors.orange,
                          onTap: () => _showResetTestData(context),
                        ),
                      ],
                      const SizedBox(height: 24),
                      // Log out button
                      SizedBox(
                        width: double.infinity,
                        child: OutlinedButton.icon(
                          onPressed: () =>
                              _showLogoutConfirmation(context, l10n),
                          icon: const Icon(
                            Icons.logout,
                            color: AppTheme.statusError,
                            size: 18,
                          ),
                          label: Text(
                            l10n.logout,
                            style: const TextStyle(
                              color: AppTheme.statusError,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                          style: OutlinedButton.styleFrom(
                            side: const BorderSide(color: AppTheme.statusError),
                            padding: const EdgeInsets.symmetric(vertical: 14),
                            shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(12),
                            ),
                          ),
                        ),
                      ),
                      const SizedBox(height: 32),
                    ],
                  ),
                ),
              ],
            ),
          ),
        );
      },
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

  Widget _verticalDivider() {
    return Container(
      width: 1,
      height: 32,
      margin: const EdgeInsets.symmetric(horizontal: 12),
      color: AppTheme.borderLight,
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

  Future<Map<String, String>> _getStatsFuture(String userId) {
    if (_statsFuture == null || _statsUserId != userId) {
      _statsUserId = userId;
      _statsFuture = _loadProfileStats(userId);
    }
    return _statsFuture!;
  }

  Future<User?> _getUser(String userId) async {
    try {
      final isar = IsarService.instance.db;
      return await isar.users.where().userIdEqualTo(userId).findFirst();
    } on Object {
      return null;
    }
  }

  Future<Map<String, String>> _loadProfileStats(String userId) async {
    try {
      final isar = IsarService.instance.db;
      final deliveries = await isar.deliverys
          .where()
          .filter()
          .farmerIdEqualTo(userId)
          .or()
          .buyerIdEqualTo(userId)
          .count();
      final payments = await isar.payments
          .where()
          .filter()
          .farmerIdEqualTo(userId)
          .or()
          .buyerIdEqualTo(userId)
          .count();
      final total = deliveries + payments;
      final escrowPct = total > 0 ? ((payments / total) * 100).round() : 0;
      return {
        'trades': '$total',
        'rating': '4.5',
        'escrow': '$escrowPct%',
      };
    } on Object {
      return {'trades': '0', 'rating': '0.0', 'escrow': '0%'};
    }
  }

  String _getRoleLabel(UserRole role, AppLocalizations l10n) {
    return switch (role) {
      UserRole.farmer => l10n.farmer,
      UserRole.trucker => l10n.trucker,
      UserRole.buyer => l10n.buyer,
    };
  }

  void _showLogoutConfirmation(BuildContext context, AppLocalizations l10n) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: Text(l10n.logout),
          content: Text(l10n.logoutConfirmation),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(l10n.cancel),
            ),
            TextButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                context.read<AuthBloc>().add(
                      const AuthLogoutRequested(clearCache: false),
                    );
              },
              style: TextButton.styleFrom(
                foregroundColor: AppTheme.statusError,
              ),
              child: Text(l10n.logout),
            ),
          ],
        );
      },
    );
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
              style: TextButton.styleFrom(foregroundColor: Colors.orange),
              child: const Text('Reset'),
            ),
          ],
        );
      },
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
          Text(
            value,
            style: const TextStyle(
              fontSize: 18,
              fontWeight: FontWeight.w700,
              color: AppTheme.textDark,
            ),
          ),
          const SizedBox(height: 2),
          Text(
            label,
            style: const TextStyle(
              fontSize: 12,
              color: AppTheme.textMuted,
            ),
          ),
        ],
      ),
    );
  }
}

class _ProfileMenuItem extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;
  final Color? iconColor;

  const _ProfileMenuItem({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
    this.iconColor,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            color: AppTheme.surfaceWhite,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: AppTheme.borderLight),
          ),
          child: Row(
            children: [
              Container(
                width: 40,
                height: 40,
                decoration: BoxDecoration(
                  color: (iconColor ?? AppTheme.primaryGreen).withAlpha(25),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(
                  icon,
                  size: 20,
                  color: iconColor ?? AppTheme.primaryGreen,
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
                        fontWeight: FontWeight.w600,
                        color: AppTheme.textDark,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      subtitle,
                      style: const TextStyle(
                        fontSize: 12,
                        color: AppTheme.textMuted,
                      ),
                    ),
                  ],
                ),
              ),
              const Icon(
                Icons.chevron_right,
                size: 20,
                color: AppTheme.textMuted,
              ),
            ],
          ),
        ),
      ),
    );
  }
}
