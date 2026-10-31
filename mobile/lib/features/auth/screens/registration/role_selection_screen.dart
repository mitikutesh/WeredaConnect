import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';

/// Step 1: Role Selection — pick Farmer, Trucker, or Buyer (the RegisterRole
/// board). The flow above draws the header and the Continue button.
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

    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(20, 22, 20, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          JoinTitle(
            title: l10n.selectRole,
            subtitle: l10n.selectRoleDescription,
          ),
          const SizedBox(height: 22),
          _RoleCard(
            icon: Icons.eco,
            iconBg: AppTheme.fieldGreenTint,
            iconColor: AppTheme.fieldGreen,
            title: l10n.farmer,
            description: l10n.farmerDescription,
            isSelected: selectedRole == UserRole.farmer,
            onTap: () => onRoleSelected(UserRole.farmer),
          ),
          const SizedBox(height: 12),
          _RoleCard(
            icon: Icons.local_shipping_outlined,
            iconBg: AppTheme.actionCream,
            iconColor: AppTheme.money,
            title: l10n.trucker,
            description: l10n.truckerDescription,
            isSelected: selectedRole == UserRole.trucker,
            onTap: () => onRoleSelected(UserRole.trucker),
          ),
          const SizedBox(height: 12),
          _RoleCard(
            icon: Icons.shopping_bag_outlined,
            iconBg: AppTheme.dropoffBlueTint,
            iconColor: AppTheme.dropoffBlue,
            title: l10n.buyer,
            description: l10n.buyerDescription,
            isSelected: selectedRole == UserRole.buyer,
            onTap: () => onRoleSelected(UserRole.buyer),
          ),
          const SizedBox(height: 18),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 4),
            child: Text(
              l10n.registrationRoleNextNote,
              style: const TextStyle(
                fontSize: 13,
                height: 1.5,
                color: AppTheme.mutedSteel,
              ),
            ),
          ),
        ],
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
    return Semantics(
      button: true,
      selected: isSelected,
      inMutuallyExclusiveGroup: true,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(20),
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 150),
            // The selected card's border grows inward so the row stays put.
            padding: EdgeInsets.all(isSelected ? 17 : 18),
            decoration: BoxDecoration(
              color: AppTheme.surfaceWhite,
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
                  child: Icon(icon, size: 26, color: iconColor),
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
                _Radio(selected: isSelected),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The board's radio: a 22 px ring, filled with a dark dot when selected.
class _Radio extends StatelessWidget {
  final bool selected;

  const _Radio({required this.selected});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 22,
      height: 22,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        border: Border.all(
          color: selected ? AppTheme.soilDark : const Color(0xFFB9BDB7),
          width: 2,
        ),
      ),
      child: selected
          ? Container(
              width: 12,
              height: 12,
              decoration: const BoxDecoration(
                shape: BoxShape.circle,
                color: AppTheme.soilDark,
              ),
            )
          : null,
    );
  }
}
