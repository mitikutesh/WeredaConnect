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

  group('roleAccent — decorative role identity', () {
    test('every role resolves and all three accents are distinct', () {
      final light = {
        for (final role in UserRole.values) role: AppTheme.roleAccent(role),
      };
      final dark = {
        for (final role in UserRole.values) role: AppTheme.darkRoleAccent(role),
      };
      expect(light.values.toSet().length, UserRole.values.length);
      expect(dark.values.toSet().length, UserRole.values.length);
    });

    test('farmer reuses the shared brand tokens (no third teal)', () {
      expect(AppTheme.roleAccent(UserRole.farmer), AppTheme.primary);
      expect(AppTheme.darkRoleAccent(UserRole.farmer), AppTheme.darkPrimary);
    });

    test('accents never collide with money or status tokens', () {
      final reserved = {
        AppTheme.money,
        AppTheme.accentSoft,
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

    test('light accents hit ≥3:1 (icon use) on white surfaces', () {
      for (final role in UserRole.values) {
        final ratio = _contrast(AppTheme.roleAccent(role), white);
        expect(
          ratio,
          greaterThanOrEqualTo(3.0),
          reason: '${role.name} light accent on white was $ratio',
        );
      }
    });

    test(
        'dark accents hit ≥3:1 (icon use) on darkSurface AND the '
        'always-dark sidebar rail', () {
      for (final role in UserRole.values) {
        final accent = AppTheme.darkRoleAccent(role);
        final onSurface = _contrast(accent, AppTheme.darkSurface);
        final onSidebar = _contrast(accent, AppTheme.primaryDeep);
        expect(
          onSurface,
          greaterThanOrEqualTo(3.0),
          reason: '${role.name} dark accent on darkSurface was $onSurface',
        );
        expect(
          onSidebar,
          greaterThanOrEqualTo(3.0),
          reason: '${role.name} dark accent on sidebar rail was $onSidebar',
        );
      }
    });

    test('sidebar logo glyph (primaryDeep) is legible on every accent tile',
        () {
      for (final role in UserRole.values) {
        final ratio =
            _contrast(AppTheme.primaryDeep, AppTheme.darkRoleAccent(role));
        expect(
          ratio,
          greaterThanOrEqualTo(3.0),
          reason: '${role.name} logo tile glyph contrast was $ratio',
        );
      }
    });
  });
}
