import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Step 1: Role Selection — pick Farmer, Trucker, or Buyer (Fieldway Canvas).
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
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                l10n.selectRole,
                style: const TextStyle(
                  fontSize: 28,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.soilDark,
                  letterSpacing: -0.5,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                l10n.selectRoleDescription,
                style: const TextStyle(
                  fontSize: 15,
                  color: AppTheme.mutedSteel,
                ),
              ),
              const SizedBox(height: 24),
              _RoleCard(
                icon: Icons.eco,
                iconBg: const Color(0xFFE3EEE6),
                iconColor: AppTheme.fieldGreen,
                title: l10n.farmer,
                description: l10n.farmerDescription,
                isSelected: selectedRole == UserRole.farmer,
                onTap: () => onRoleSelected(UserRole.farmer),
              ),
              const SizedBox(height: 12),
              _RoleCard(
                icon: Icons.local_shipping_outlined,
                iconBg: const Color(0xFFFAEED8),
                iconColor: const Color(0xFF8A5410),
                title: l10n.trucker,
                description: l10n.truckerDescription,
                isSelected: selectedRole == UserRole.trucker,
                onTap: () => onRoleSelected(UserRole.trucker),
              ),
              const SizedBox(height: 12),
              _RoleCard(
                icon: Icons.shopping_bag_outlined,
                iconBg: const Color(0xFFE6ECF5),
                iconColor: const Color(0xFF2E4C7D),
                title: l10n.buyer,
                description: l10n.buyerDescription,
                isSelected: selectedRole == UserRole.buyer,
                onTap: () => onRoleSelected(UserRole.buyer),
              ),
              const SizedBox(height: 20),
              const Text(
                'Next you verify with Fayda, then add your details. Farmers skip the role details step.',
                style: TextStyle(
                  fontSize: 13,
                  height: 1.5,
                  color: AppTheme.mutedSteel,
                ),
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
  final Color iconBg;
  final Color iconColor;
  final String title;
  final String description;
  final bool isSelected;
  final VoidCallback onTap;

  const _RoleCard({
    required this.icon,
    required this.iconBg,
    required this.iconColor,
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
        borderRadius: BorderRadius.circular(20),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.all(18),
          decoration: BoxDecoration(
            color: Colors.white,
            borderRadius: BorderRadius.circular(20),
            border: Border.all(
              color: isSelected ? AppTheme.soilDark : AppTheme.whisperBorder,
              width: isSelected ? 2 : 1,
            ),
          ),
          child: Row(
            children: [
              Container(
                width: 52,
                height: 52,
                decoration: BoxDecoration(
                  color: iconBg,
                  borderRadius: BorderRadius.circular(16),
                ),
                child: Icon(
                  icon,
                  size: 26,
                  color: iconColor,
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
                        fontSize: 17,
                        fontWeight: FontWeight.w800,
                        color: AppTheme.soilDark,
                      ),
                    ),
                    const SizedBox(height: 3),
                    Text(
                      description,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.mutedSteel,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              Container(
                width: 22,
                height: 22,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  border: Border.all(
                    color: isSelected ? AppTheme.soilDark : AppTheme.whisperBorder,
                    width: isSelected ? 6 : 2,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
