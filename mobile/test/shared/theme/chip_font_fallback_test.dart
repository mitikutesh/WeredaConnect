import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
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

  // The selected chip is filled with the text colour. A FilterChip has no
  // selected label style of its own, so the label colour follows the state.
  group('a selected chip reads on its fill', () {
    for (final entry in {
      'light': (AppTheme.light(), AppTheme.ink, AppTheme.surfaceWhite),
      'dark': (
        AppTheme.dark(),
        AppTheme.darkTextPrimary,
        AppTheme.darkSurface,
      ),
    }.entries) {
      final (theme, text, surface) = entry.value;

      test('${entry.key} theme: the label colour by state', () {
        final chip = theme.chipTheme;
        expect(chip.selectedColor, text);
        final label = chip.labelStyle!.color! as WidgetStateColor;
        expect(label.resolve({WidgetState.selected}), surface);
        expect(label.resolve({}), text);
        expect(chip.secondaryLabelStyle!.color, surface);
      });

      for (final choice in [false, true]) {
        testWidgets(
            '${entry.key} theme: a selected '
            '${choice ? 'ChoiceChip' : 'FilterChip'} label', (tester) async {
          await tester.pumpWidget(
            MaterialApp(
              theme: theme,
              home: Scaffold(
                body: Row(
                  children: [
                    for (final selected in [true, false])
                      choice
                          ? ChoiceChip(
                              label: Text(selected ? 'On' : 'Off'),
                              selected: selected,
                              onSelected: (_) {},
                            )
                          : FilterChip(
                              label: Text(selected ? 'On' : 'Off'),
                              selected: selected,
                              onSelected: (_) {},
                            ),
                  ],
                ),
              ),
            ),
          );
          Color? colorOf(String label) => tester
              .renderObject<RenderParagraph>(find.text(label))
              .text
              .style
              ?.color;
          expect(colorOf('On'), surface);
          expect(colorOf('Off'), text);
        });
      }
    }
  });

  // A switch that is off: its pale track has the edge of a control a user
  // must find, in both themes. On, the track is the text colour itself.
  group('a switch that is off has a visible edge', () {
    for (final entry in {
      'light': (AppTheme.light(), AppTheme.fieldBorder, AppTheme.ground),
      'dark': (
        AppTheme.dark(),
        AppTheme.darkTextMuted,
        AppTheme.darkBackground,
      ),
    }.entries) {
      test('${entry.key} theme', () {
        final (theme, edge, page) = entry.value;
        final outline = theme.switchTheme.trackOutlineColor!;
        expect(outline.resolve({}), edge);
        expect(outline.resolve({WidgetState.selected}), Colors.transparent);
        expect(theme.switchTheme.trackOutlineWidth!.resolve({}), 1.5);
        double lum(Color c) => c.computeLuminance();
        final hi = lum(edge) > lum(page) ? lum(edge) : lum(page);
        final lo = lum(edge) > lum(page) ? lum(page) : lum(edge);
        expect((hi + 0.05) / (lo + 0.05), greaterThanOrEqualTo(3));
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

  // Canvas page M: a dialog has the cards' corner (FwCard's 24).
  test('dialogs are the boards\' white card, radius 24', () {
    final shape = AppTheme.light().dialogTheme.shape! as RoundedRectangleBorder;
    expect(shape.borderRadius, BorderRadius.circular(28));
    expect(AppTheme.light().dialogTheme.backgroundColor, AppTheme.surfaceWhite);
  });

  // A screen's own `styleFrom(textStyle: TextStyle(...))` replaces the
  // theme's button text style instead of merging with it, so without the
  // family the label is drawn in the platform face, not Bricolage Grotesque, and loses
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
