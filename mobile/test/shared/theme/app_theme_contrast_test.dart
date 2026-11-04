import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// WCAG contrast ratio, from Flutter's own relative luminance.
double _contrast(Color a, Color b) {
  final la = a.computeLuminance();
  final lb = b.computeLuminance();
  final hi = la > lb ? la : lb;
  final lo = la > lb ? lb : la;
  return (hi + 0.05) / (lo + 0.05);
}

/// The theme's colour pairs against WCAG AA: 4.5:1 for text, 3:1 for a
/// control's edge or a meaningful icon. The guideline checks in widget
/// tests do not see borders, so the tokens are checked here.
void main() {
  const grounds = {
    'white': AppTheme.surfaceWhite,
    'chalk': AppTheme.chalkBackground,
  };

  const textTokens = {
    'soilDark': AppTheme.soilDark,
    'mutedSteel': AppTheme.mutedSteel,
    'unselectedNav': AppTheme.unselectedNav,
    'inkSecondary': AppTheme.inkSecondary,
    'actionInk': AppTheme.actionInk,
    'money': AppTheme.money,
    'fieldGreen': AppTheme.fieldGreen,
    'dropoffBlue': AppTheme.dropoffBlue,
    'statusSuccess': AppTheme.statusSuccess,
    'statusWarning': AppTheme.statusWarning,
    'statusError': AppTheme.statusError,
    'statusInfo': AppTheme.statusInfo,
  };

  group('text tokens', () {
    for (final ground in grounds.entries) {
      for (final token in textTokens.entries) {
        test('${token.key} on ${ground.key} is at least 4.5:1', () {
          expect(
            _contrast(token.value, ground.value),
            greaterThanOrEqualTo(4.5),
          );
        });
      }
    }

    test('status chips: each foreground on its container', () {
      const pairs = {
        'success': (
          AppTheme.onStatusSuccessContainer,
          AppTheme.statusSuccessContainer,
        ),
        'warning': (
          AppTheme.onStatusWarningContainer,
          AppTheme.statusWarningContainer,
        ),
        'error': (
          AppTheme.onStatusErrorContainer,
          AppTheme.statusErrorContainer,
        ),
        'info': (AppTheme.onStatusInfoContainer, AppTheme.statusInfoContainer),
        'transit': (
          AppTheme.onStatusTransitContainer,
          AppTheme.statusTransitContainer,
        ),
        'neutral': (
          AppTheme.onStatusNeutralContainer,
          AppTheme.statusNeutralContainer,
        ),
      };
      for (final pair in pairs.entries) {
        expect(
          _contrast(pair.value.$1, pair.value.$2),
          greaterThanOrEqualTo(4.5),
          reason: pair.key,
        );
      }
    });

    test('nav labels: each role accent on its tint and on white', () {
      for (final role in UserRole.values) {
        final accent = AppTheme.roleAccent(role);
        expect(
          _contrast(accent, AppTheme.roleAccentTint(role)),
          greaterThanOrEqualTo(4.5),
          reason: role.name,
        );
        expect(
          _contrast(accent, AppTheme.surfaceWhite),
          greaterThanOrEqualTo(4.5),
          reason: role.name,
        );
      }
    });

    test('create button: label on fill for every role', () {
      for (final role in UserRole.values) {
        expect(
          _contrast(
            AppTheme.createButtonForeground(role),
            AppTheme.createButtonBackground(role),
          ),
          greaterThanOrEqualTo(4.5),
          reason: role.name,
        );
      }
    });

    test('gold is a fill: dark ink on it, never white', () {
      expect(
        _contrast(AppTheme.soilDark, AppTheme.accentSoft),
        greaterThanOrEqualTo(4.5),
      );
      // Why gold never carries white text or sits as text on white.
      expect(_contrast(Colors.white, AppTheme.accentSoft), lessThan(3));
    });
  });

  group('control edges and icons', () {
    for (final ground in grounds.entries) {
      test('fieldBorder on ${ground.key} is at least 3:1', () {
        expect(
          _contrast(AppTheme.fieldBorder, ground.value),
          greaterThanOrEqualTo(3),
        );
      });

      test('statusTransit (icons only) on ${ground.key} is at least 3:1', () {
        expect(
          _contrast(AppTheme.statusTransit, ground.value),
          greaterThanOrEqualTo(3),
        );
      });
    }

    test('the light theme draws input and outlined button edges in it', () {
      final theme = AppTheme.light();
      final input = theme.inputDecorationTheme;
      expect(input.enabledBorder!.borderSide.color, AppTheme.fieldBorder);
      expect(input.border!.borderSide.color, AppTheme.fieldBorder);
      final side = theme.outlinedButtonTheme.style!.side!.resolve({});
      expect(side!.color, AppTheme.fieldBorder);
      // Cards keep the decorative whisper edge.
      final card = theme.cardTheme.shape! as RoundedRectangleBorder;
      expect(card.side.color, AppTheme.whisperBorder);
    });
  });
}
