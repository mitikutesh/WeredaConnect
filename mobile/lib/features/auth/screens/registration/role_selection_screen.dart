import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
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
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          JoinTitle(
            title: l10n.selectRole,
            subtitle: l10n.selectRoleDescription,
          ),
          const SizedBox(height: 20),
          for (final role in UserRole.values) ...[
            if (role != UserRole.values.first) const SizedBox(height: 12),
            _roleCard(context, l10n, role),
          ],
          const SizedBox(height: 20),
          JoinNote.plain(text: l10n.registrationRoleNextNote),
        ],
      ),
    );
  }

  /// One role's card: its icon tile wears THAT role's tint and colour,
  /// whichever role is picked.
  Widget _roleCard(
    BuildContext context,
    AppLocalizations l10n,
    UserRole role,
  ) {
    final colors = RoleColors.forRole(role);
    final (title, description, icon) = switch (role) {
      UserRole.farmer => (
          l10n.farmer,
          l10n.farmerDescription,
          FwSprout(size: 26, color: colors.primary) as Widget,
        ),
      UserRole.trucker => (
          l10n.trucker,
          l10n.truckerDescription,
          Icon(Icons.local_shipping_outlined, size: 26, color: colors.primary),
        ),
      UserRole.buyer => (
          l10n.buyer,
          l10n.buyerDescription,
          Icon(Icons.shopping_bag_outlined, size: 26, color: colors.primary),
        ),
    };
    return JoinChoiceCard(
      key: ValueKey('register_role_${role.name}'),
      icon: icon,
      iconBackground: colors.tint,
      title: title,
      description: description,
      selected: selectedRole == role,
      onTap: () => onRoleSelected(role),
    );
  }
}
