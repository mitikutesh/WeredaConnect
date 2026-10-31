import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

void main() {
  group('chip labels carry the Ethiopic font fallback (SIM-22)', () {
    for (final entry
        in {'light': AppTheme.light(), 'dark': AppTheme.dark()}.entries) {
      test('${entry.key} theme', () {
        final chip = entry.value.chipTheme;
        for (final style in [chip.labelStyle, chip.secondaryLabelStyle]) {
          expect(style, isNotNull);
          expect(style!.fontFamily, AppTheme.fontFamily);
          expect(style.fontFamilyFallback, contains('NotoSansEthiopic'));
        }
        // Same fallback as the body text, so chips and text render alike.
        expect(
          entry.value.textTheme.bodyMedium?.fontFamilyFallback,
          contains('NotoSansEthiopic'),
        );
      });
    }
  });

  // A component theme's explicit textStyle does not inherit
  // ThemeData.fontFamily, so without it button labels fell back to Roboto.
  group('button labels use the Fieldway face', () {
    for (final entry
        in {'light': AppTheme.light(), 'dark': AppTheme.dark()}.entries) {
      test('${entry.key} theme', () {
        final theme = entry.value;
        final styles = [
          theme.filledButtonTheme.style?.textStyle?.resolve({}),
          theme.elevatedButtonTheme.style?.textStyle?.resolve({}),
          theme.outlinedButtonTheme.style?.textStyle?.resolve({}),
          theme.textButtonTheme.style?.textStyle?.resolve({}),
          theme.floatingActionButtonTheme.extendedTextStyle,
          theme.dialogTheme.titleTextStyle,
        ];
        for (final style in styles) {
          expect(style?.fontFamily, AppTheme.fontFamily);
          expect(style?.fontFamilyFallback, contains('NotoSansEthiopic'));
        }
      });
    }
  });

  test('dialogs are the boards\' white card, radius 18', () {
    final shape = AppTheme.light().dialogTheme.shape! as RoundedRectangleBorder;
    expect(shape.borderRadius, BorderRadius.circular(18));
    expect(AppTheme.light().dialogTheme.backgroundColor, AppTheme.surfaceWhite);
  });

  // A screen's own `styleFrom(textStyle: TextStyle(...))` replaces the
  // theme's button text style instead of merging with it, so without the
  // family the label is drawn in the platform face, not Manrope, and loses
  // the Ethiopic fallback.
  test('every button textStyle override names the app font', () {
    final opener = RegExp(
      r'textStyle:\s*(const\s+)?(WidgetStatePropertyAll(<TextStyle>)?\(\s*)?'
      r'(const\s+)?TextStyle\(',
    );
    final missing = <String>[];
    for (final file in Directory('lib').listSync(recursive: true)) {
      if (file is! File ||
          !file.path.endsWith('.dart') ||
          file.path.endsWith('.g.dart') ||
          file.path.contains('/l10n/')) {
        continue;
      }
      final source = file.readAsStringSync();
      for (final match in opener.allMatches(source)) {
        var depth = 1;
        var i = match.end;
        while (depth > 0 && i < source.length) {
          final c = source[i++];
          if (c == '(') depth++;
          if (c == ')') depth--;
        }
        if (!source.substring(match.start, i).contains('fontFamily')) {
          final line = '\n'.allMatches(source.substring(0, match.start)).length;
          missing.add('${file.path}:${line + 1}');
        }
      }
    }
    expect(missing, isEmpty);
  });
}
