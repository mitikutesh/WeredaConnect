import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// One colour per role (design canvas page M): the theme is built for the
/// signed-in role and screens read the colours from it.
void main() {
  group('RoleColors', () {
    test('each role has its own colour, the brand is black', () {
      expect(RoleColors.forRole(UserRole.farmer), RoleColors.farmer);
      expect(RoleColors.forRole(UserRole.trucker), RoleColors.trucker);
      expect(RoleColors.forRole(UserRole.buyer), RoleColors.buyer);
      expect(RoleColors.forRole(null), RoleColors.brand);
      expect(RoleColors.farmer.primary, const Color(0xFF9A45A0));
      expect(RoleColors.trucker.primary, const Color(0xFF2B62C4));
      expect(RoleColors.buyer.primary, const Color(0xFF18775F));
      expect(RoleColors.brand.primary, AppTheme.ink);
    });

    test('the theme carries the role and links take its colour', () {
      for (final role in UserRole.values) {
        final theme = AppTheme.light(role: role);
        final colors = RoleColors.forRole(role);
        expect(theme.extension<RoleColors>(), colors);
        expect(theme.colorScheme.primary, colors.primary);
        expect(theme.colorScheme.primaryContainer, colors.tint);
        expect(
          theme.textButtonTheme.style!.foregroundColor!.resolve({}),
          colors.primary,
        );
      }
    });

    test('the one action stays orange with black ink for every role', () {
      for (final role in [...UserRole.values, null]) {
        final style = AppTheme.light(role: role).filledButtonTheme.style!;
        expect(style.backgroundColor!.resolve({}), AppTheme.orange);
        expect(style.foregroundColor!.resolve({}), AppTheme.ink);
      }
    });

    test('never a status colour, so a role tint is never read as a state', () {
      final reserved = {AppTheme.statusError, AppTheme.orange};
      for (final role in UserRole.values) {
        expect(reserved.contains(RoleColors.forRole(role).primary), isFalse);
      }
    });

    testWidgets('context.roleColors reads the theme, brand without one',
        (tester) async {
      late RoleColors themed;
      late RoleColors bare;
      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light(role: UserRole.trucker),
          home: Builder(
            builder: (context) {
              themed = context.roleColors;
              return Theme(
                data: ThemeData(),
                child: Builder(
                  builder: (context) {
                    bare = context.roleColors;
                    return const SizedBox();
                  },
                ),
              );
            },
          ),
        ),
      );
      expect(themed, RoleColors.trucker);
      expect(bare, RoleColors.brand);
    });

    test('dark: the role colour light enough for icons on the dark surface',
        () {
      for (final role in UserRole.values) {
        final primary = AppTheme.dark(role: role).colorScheme.primary;
        final l1 = primary.computeLuminance();
        final l2 = AppTheme.darkSurface.computeLuminance();
        expect((l1 + 0.05) / (l2 + 0.05), greaterThanOrEqualTo(3.0));
      }
    });
  });
}
