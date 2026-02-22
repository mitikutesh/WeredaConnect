import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:isar/isar.dart';
import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Profile screen - user profile and account settings
class ProfileScreen extends StatefulWidget {
  final ValueChanged<int>? onNavigateToTab;

  const ProfileScreen({super.key, this.onNavigateToTab});

  @override
  State<ProfileScreen> createState() => _ProfileScreenState();
}

class _ProfileScreenState extends State<ProfileScreen> {
  /// Key to force FutureBuilder rebuild after edit
  int _rebuildKey = 0;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocBuilder<AuthBloc, AuthState>(
      builder: (context, authState) {
        // User is authenticated - show profile
        if (authState is AuthAuthenticated) {
          return _buildAuthenticatedProfile(
            context,
            authState.userId,
            l10n,
          );
        }

        // User not authenticated - show login prompt
        return _buildUnauthenticatedPrompt(context, l10n);
      },
    );
  }

  Widget _buildAuthenticatedProfile(
    BuildContext context,
    String userId,
    AppLocalizations l10n,
  ) {
    return FutureBuilder<User?>(
      key: ValueKey(_rebuildKey),
      future: _getUserFromDatabase(userId),
      builder: (context, snapshot) {
        if (snapshot.connectionState == ConnectionState.waiting) {
          return const Center(child: CircularProgressIndicator());
        }

        final user = snapshot.data;
        if (user == null) {
          return _buildErrorState(context, l10n);
        }

        return SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Column(
            children: [
              // User avatar
              const SizedBox(height: 32),
              CircleAvatar(
                radius: 60,
                backgroundColor: Theme.of(context).colorScheme.primaryContainer,
                child: Text(
                  user.name.isNotEmpty ? user.name[0].toUpperCase() : '?',
                  style: TextStyle(
                    fontSize: 48,
                    fontWeight: FontWeight.bold,
                    color: Theme.of(context).colorScheme.onPrimaryContainer,
                  ),
                ),
              ),

              const SizedBox(height: 24),

              // User name
              Text(
                user.name,
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                      fontWeight: FontWeight.bold,
                    ),
                textAlign: TextAlign.center,
              ),

              const SizedBox(height: 8),

              // Phone number
              Text(
                user.phoneNumber,
                style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                      color: Theme.of(context)
                          .colorScheme
                          .onSurface
                          .withValues(alpha: 0.6),
                    ),
              ),

              const SizedBox(height: 8),

              // Role chip
              Chip(
                avatar: Icon(
                  _getRoleIcon(user.role),
                  size: 18,
                ),
                label: Text(_getRoleLabel(user.role, l10n)),
                backgroundColor:
                    Theme.of(context).colorScheme.secondaryContainer,
              ),

              const SizedBox(height: 48),

              // Profile actions
              _ProfileActionCard(
                icon: Icons.person_outline,
                title: l10n.edit,
                subtitle: l10n.updateDetails,
                onTap: () => _showEditProfileSheet(context, user, l10n),
              ),

              const SizedBox(height: 12),

              _ProfileActionCard(
                icon: Icons.agriculture_outlined,
                title: l10n.listings,
                subtitle: l10n.viewCropListings,
                onTap: () {
                  widget.onNavigateToTab?.call(1);
                },
              ),

              const SizedBox(height: 12),

              _ProfileActionCard(
                icon: Icons.receipt_long_outlined,
                title: l10n.myOrders,
                subtitle: l10n.viewOrderHistory,
                onTap: () {
                  widget.onNavigateToTab?.call(3);
                },
              ),

              const SizedBox(height: 12),

              _ProfileActionCard(
                icon: Icons.notifications_outlined,
                title: l10n.notifications,
                subtitle: l10n.manageAlerts,
                onTap: () {
                  Navigator.push(
                    context,
                    MaterialPageRoute<void>(
                      builder: (_) => _NotificationsScreen(l10n: l10n),
                    ),
                  );
                },
              ),

              const SizedBox(height: 32),

              // Logout button
              OutlinedButton.icon(
                onPressed: () => _showLogoutConfirmation(context, l10n),
                icon: const Icon(Icons.logout, color: Colors.red),
                label: Text(
                  l10n.logout,
                  style: const TextStyle(color: Colors.red),
                ),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                  side: const BorderSide(color: Colors.red, width: 1.5),
                ),
              ),

              const SizedBox(height: 32),
            ],
          ),
        );
      },
    );
  }

  void _showEditProfileSheet(
    BuildContext context,
    User user,
    AppLocalizations l10n,
  ) {
    final nameController = TextEditingController(text: user.name);
    final weredaController = TextEditingController(text: user.woreda);
    final formKey = GlobalKey<FormState>();

    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (sheetContext) {
        return Padding(
          padding: EdgeInsets.only(
            left: 24,
            right: 24,
            top: 24,
            bottom: MediaQuery.of(sheetContext).viewInsets.bottom + 24,
          ),
          child: Form(
            key: formKey,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                // Handle bar
                Center(
                  child: Container(
                    width: 40,
                    height: 4,
                    decoration: BoxDecoration(
                      color: Theme.of(sheetContext)
                          .colorScheme
                          .onSurface
                          .withValues(alpha: 0.3),
                      borderRadius: BorderRadius.circular(2),
                    ),
                  ),
                ),
                const SizedBox(height: 16),

                Text(
                  l10n.editProfile,
                  style: Theme.of(sheetContext).textTheme.titleLarge,
                ),
                const SizedBox(height: 24),

                TextFormField(
                  controller: nameController,
                  decoration: InputDecoration(
                    labelText: l10n.fullName,
                    border: const OutlineInputBorder(),
                    prefixIcon: const Icon(Icons.person_outline),
                  ),
                  validator: (value) {
                    if (value == null || value.trim().isEmpty) {
                      return l10n.fullName;
                    }
                    return null;
                  },
                ),
                const SizedBox(height: 16),

                TextFormField(
                  controller: weredaController,
                  decoration: InputDecoration(
                    labelText: l10n.wereda,
                    border: const OutlineInputBorder(),
                    prefixIcon: const Icon(Icons.location_on_outlined),
                  ),
                ),
                const SizedBox(height: 24),

                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton(
                        onPressed: () => Navigator.pop(sheetContext),
                        child: Text(l10n.cancel),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: FilledButton(
                        onPressed: () async {
                          if (!formKey.currentState!.validate()) return;

                          await _saveProfile(
                            user,
                            nameController.text.trim(),
                            weredaController.text.trim(),
                          );

                          if (!sheetContext.mounted) return;
                          Navigator.pop(sheetContext);
                          if (!context.mounted) return;
                          setState(() => _rebuildKey++);
                          ScaffoldMessenger.of(context).showSnackBar(
                            SnackBar(content: Text(l10n.profileSaved)),
                          );
                        },
                        child: Text(l10n.save),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        );
      },
    );
  }

  Future<void> _saveProfile(
    User user,
    String name,
    String woreda,
  ) async {
    final isar = IsarService.instance.db;
    await isar.writeTxn(() async {
      user.name = name;
      if (woreda.isNotEmpty) {
        user.woreda = woreda;
      }
      user.updatedAt = DateTime.now().toUtc();
      await isar.users.put(user);
    });
  }

  Widget _buildUnauthenticatedPrompt(
    BuildContext context,
    AppLocalizations l10n,
  ) {
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            Icons.person_off_outlined,
            size: 80,
            color:
                Theme.of(context).colorScheme.onSurface.withValues(alpha: 0.3),
          ),
          const SizedBox(height: 24),
          Text(
            l10n.notLoggedIn,
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 16),
          Text(
            l10n.profileRestartApp,
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: Theme.of(context)
                      .colorScheme
                      .onSurface
                      .withValues(alpha: 0.6),
                ),
          ),
        ],
      ),
    );
  }

  Widget _buildErrorState(BuildContext context, AppLocalizations l10n) {
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            Icons.error_outline,
            size: 80,
            color: Theme.of(context).colorScheme.error.withValues(alpha: 0.5),
          ),
          const SizedBox(height: 24),
          Text(
            l10n.errorOccurred,
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 16),
          Text(
            l10n.profileLoadError,
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: Theme.of(context)
                      .colorScheme
                      .onSurface
                      .withValues(alpha: 0.6),
                ),
          ),
        ],
      ),
    );
  }

  Future<User?> _getUserFromDatabase(String userId) async {
    try {
      final isar = IsarService.instance.db;
      final user = await isar.users.where().userIdEqualTo(userId).findFirst();
      return user;
    } on Exception {
      return null;
    }
  }

  IconData _getRoleIcon(UserRole role) {
    switch (role) {
      case UserRole.farmer:
        return Icons.agriculture;
      case UserRole.trucker:
        return Icons.local_shipping;
      case UserRole.buyer:
        return Icons.shopping_bag;
    }
  }

  String _getRoleLabel(UserRole role, AppLocalizations l10n) {
    switch (role) {
      case UserRole.farmer:
        return l10n.farmer;
      case UserRole.trucker:
        return l10n.trucker;
      case UserRole.buyer:
        return l10n.buyer;
    }
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
              style: TextButton.styleFrom(foregroundColor: Colors.red),
              child: Text(l10n.logout),
            ),
          ],
        );
      },
    );
  }
}

// Helper widget for profile action cards
class _ProfileActionCard extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;

  const _ProfileActionCard({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Card(
      elevation: 0,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(
          color: Theme.of(context).colorScheme.outline.withValues(alpha: 0.5),
        ),
      ),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Icon(icon, size: 28),
              const SizedBox(width: 16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      style: Theme.of(context).textTheme.titleMedium,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      subtitle,
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: Theme.of(context)
                                .colorScheme
                                .onSurface
                                .withValues(alpha: 0.6),
                          ),
                    ),
                  ],
                ),
              ),
              const Icon(Icons.chevron_right),
            ],
          ),
        ),
      ),
    );
  }
}

/// Simple notifications screen with empty state placeholder
class _NotificationsScreen extends StatelessWidget {
  final AppLocalizations l10n;

  const _NotificationsScreen({required this.l10n});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.notifications),
      ),
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              Icons.notifications_none_outlined,
              size: 80,
              color: Theme.of(context)
                  .colorScheme
                  .onSurface
                  .withValues(alpha: 0.3),
            ),
            const SizedBox(height: 24),
            Text(
              l10n.noNotificationsYet,
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 8),
            Text(
              l10n.noNotificationsDescription,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: Theme.of(context)
                        .colorScheme
                        .onSurface
                        .withValues(alpha: 0.6),
                  ),
            ),
          ],
        ),
      ),
    );
  }
}
