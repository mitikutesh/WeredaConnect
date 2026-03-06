import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Step 1: Role Selection — pick Farmer, Trucker, or Buyer.
class RoleSelectionScreen extends StatelessWidget {
  final UserRole? selectedRole;
  final ValueChanged<UserRole> onRoleSelected;

  const RoleSelectionScreen({
    required this.onRoleSelected,
    super.key,
    this.selectedRole,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                l10n.selectRole,
                style: const TextStyle(
                  fontSize: 22,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.textDark,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                l10n.selectRoleDescription,
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.textMuted,
                ),
              ),
              const SizedBox(height: 32),
              _RoleCard(
                icon: Icons.agriculture,
                title: l10n.farmer,
                description: l10n.farmerDescription,
                isSelected: selectedRole == UserRole.farmer,
                onTap: () => onRoleSelected(UserRole.farmer),
              ),
              const SizedBox(height: 16),
              _RoleCard(
                icon: Icons.local_shipping,
                title: l10n.trucker,
                description: l10n.truckerDescription,
                isSelected: selectedRole == UserRole.trucker,
                onTap: () => onRoleSelected(UserRole.trucker),
              ),
              const SizedBox(height: 16),
              _RoleCard(
                icon: Icons.shopping_bag,
                title: l10n.buyer,
                description: l10n.buyerDescription,
                isSelected: selectedRole == UserRole.buyer,
                onTap: () => onRoleSelected(UserRole.buyer),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _RoleCard extends StatelessWidget {
  final IconData icon;
  final String title;
  final String description;
  final bool isSelected;
  final VoidCallback onTap;

  const _RoleCard({
    required this.icon,
    required this.title,
    required this.description,
    required this.isSelected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(16),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 200),
          padding: const EdgeInsets.all(20),
          decoration: BoxDecoration(
            color: isSelected
                ? AppTheme.primaryGreen.withAlpha(15)
                : AppTheme.surfaceWhite,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(
              color: isSelected ? AppTheme.primaryGreen : AppTheme.borderLight,
              width: isSelected ? 2 : 1,
            ),
          ),
          child: Row(
            children: [
              Container(
                width: 56,
                height: 56,
                decoration: BoxDecoration(
                  color: isSelected
                      ? AppTheme.primaryGreen.withAlpha(30)
                      : AppTheme.backgroundCream,
                  borderRadius: BorderRadius.circular(14),
                ),
                child: Icon(
                  icon,
                  size: 28,
                  color:
                      isSelected ? AppTheme.primaryGreen : AppTheme.textMuted,
                ),
              ),
              const SizedBox(width: 16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      style: TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w600,
                        color: isSelected
                            ? AppTheme.primaryGreen
                            : AppTheme.textDark,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      description,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.textMuted,
                      ),
                    ),
                  ],
                ),
              ),
              if (isSelected)
                const Icon(
                  Icons.check_circle,
                  color: AppTheme.primaryGreen,
                  size: 24,
                ),
            ],
          ),
        ),
      ),
    );
  }
}
