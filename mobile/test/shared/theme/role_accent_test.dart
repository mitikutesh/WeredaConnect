import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// WCAG 2.x relative luminance.
double _luminance(Color c) {
  double lin(double channel) => channel <= 0.04045
      ? channel / 12.92
      : math.pow((channel + 0.055) / 1.055, 2.4).toDouble();
  return 0.2126 * lin(c.r) + 0.7152 * lin(c.g) + 0.0722 * lin(c.b);
}

/// WCAG contrast ratio between two colors.
double _contrast(Color a, Color b) {
  final la = _luminance(a);
  final lb = _luminance(b);
  final hi = math.max(la, lb);
  final lo = math.min(la, lb);
  return (hi + 0.05) / (lo + 0.05);
}

void main() {
  const white = Color(0xFFFFFFFF);

  group('roleAccent: the boards\' selected nav item', () {
    test('farmers and buyers share field green, truckers get brown', () {
      expect(AppTheme.roleAccent(UserRole.farmer), AppTheme.fieldGreen);
      expect(AppTheme.roleAccent(UserRole.buyer), AppTheme.fieldGreen);
      expect(AppTheme.roleAccent(UserRole.trucker), AppTheme.money);
      expect(AppTheme.roleAccentTint(UserRole.farmer), AppTheme.fieldGreenTint);
      expect(AppTheme.roleAccentTint(UserRole.buyer), AppTheme.fieldGreenTint);
      expect(
        AppTheme.roleAccentTint(UserRole.trucker),
        AppTheme.accentSoftLight,
      );
    });

    test('farmer and buyer reuse the shared brand tokens', () {
      expect(AppTheme.roleAccent(UserRole.farmer), AppTheme.primary);
      expect(AppTheme.darkRoleAccent(UserRole.buyer), AppTheme.darkPrimary);
    });

    test('accents never collide with status tokens', () {
      final reserved = {
        AppTheme.statusSuccess,
        AppTheme.statusWarning,
        AppTheme.statusError,
        AppTheme.statusInfo,
        AppTheme.statusTransit,
      };
      for (final role in UserRole.values) {
        expect(reserved.contains(AppTheme.roleAccent(role)), isFalse);
        expect(reserved.contains(AppTheme.darkRoleAccent(role)), isFalse);
      }
    });

    test('the selected label reads at 4.5:1 on its pill and on white', () {
      for (final role in UserRole.values) {
        final accent = AppTheme.roleAccent(role);
        for (final ground in [white, AppTheme.roleAccentTint(role)]) {
          final ratio = _contrast(accent, ground);
          expect(
            ratio,
            greaterThanOrEqualTo(4.5),
            reason: '${role.name} accent on $ground was $ratio',
          );
        }
      }
    });

    test('dark accents hit 3:1 (icon use) on darkSurface', () {
      for (final role in UserRole.values) {
        final ratio =
            _contrast(AppTheme.darkRoleAccent(role), AppTheme.darkSurface);
        expect(
          ratio,
          greaterThanOrEqualTo(3.0),
          reason: '${role.name} dark accent on darkSurface was $ratio',
        );
      }
    });

    test('the create button label reads at 4.5:1 for every role', () {
      for (final role in UserRole.values) {
        final ratio = _contrast(
          AppTheme.createButtonForeground(role),
          AppTheme.createButtonBackground(role),
        );
        expect(
          ratio,
          greaterThanOrEqualTo(4.5),
          reason: '${role.name} create button contrast was $ratio',
        );
      }
    });
  });
}
