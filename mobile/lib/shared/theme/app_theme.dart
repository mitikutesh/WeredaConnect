import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';

/// WeredaConnect design system — "Highland Teal".
///
/// Token rules (see specs design brief, 2026-07-06):
/// - Primary: deep teal (#1A6B5C) — advancing actions, active nav, links
/// - Money: deep terracotta (#A64B1A) — ONLY money-moving buttons and the
///   create FAB; nothing else may use it, so its meaning stays legible
/// - AccentSoft: terracotta (#D4804A) — large icons/illustration only;
///   fails 4.5:1 with white text, so never use it behind text
/// - Status: closed 5-token vocabulary (+ neutral), always paired with an
///   icon and a label — color is never the only signal
/// - Role accents: one decorative hue per signed-in role for nav chrome
///   only ([roleAccent]) — NOT a per-role theme; everything else above
///   is shared by all roles
/// - Dark theme tokens are defined here from day one; `themeMode` stays
///   pinned to light until the raw `Colors.*` sweep makes screens dark-safe
class AppTheme {
  AppTheme._();

  // ── Brand colors ──────────────────────────────────────────────────────
  static const Color primary = Color(0xFF1A6B5C);
  static const Color primaryLight = Color(0xFF2D9B89);
  static const Color primaryDeep = Color(0xFF14524A);
  static const Color money = Color(0xFFA64B1A);
  static const Color accentSoft = Color(0xFFD4804A);
  static const Color accentSoftLight = Color(0xFFE09A6C);
  static const Color backgroundCream = Color(0xFFF5F3F0);
  static const Color surfaceWhite = Color(0xFFFFFFFF);
  static const Color textDark = Color(0xFF1E293B);

  /// Bundled Ethiopic-script font, appended to every text style (theme text
  /// and chip labels) so Amharic never waits on a system fallback font.
  static const List<String> ethiopicFontFallback = ['NotoSansEthiopic'];
  static const Color textMuted = Color(0xFF526071);
  static const Color borderLight = Color(0xFFE2E8F0);

  // ── Role identity accents (decorative only — no status meaning) ──────
  // A single hue cue per signed-in role, used on nav chrome (sidebar logo
  // tile, selected nav item) so users always know which role they're
  // acting as. This is NOT a per-role theme: ColorScheme, buttons, the
  // money FAB, and the status vocabulary stay shared across roles.
  // Farmer reuses the primary teal — already its implicit identity.
  // Hues sit clear of money terracotta and all five status tokens
  // (sky ≠ info royal-blue #1D4ED8; plum ≠ transit violet #6D28D9).
  static const Color roleAccentTrucker = Color(0xFF0369A1); // deep sky
  static const Color roleAccentBuyer = Color(0xFFA21CAF); // plum

  // Dark counterparts — computed ≥3:1 for icon use on darkSurface AND on
  // the always-dark sidebar (primaryDeep #14524A): sky 7.47/4.20,
  // plum 6.50/3.65, teal 7.62/4.28.
  static const Color darkRoleAccentTrucker = Color(0xFF38BDF8);
  static const Color darkRoleAccentBuyer = Color(0xFFE879F9);

  /// Decorative accent for [role] on light grounds (≥5.9:1 on white).
  static Color roleAccent(UserRole role) {
    return switch (role) {
      UserRole.farmer => primary,
      UserRole.trucker => roleAccentTrucker,
      UserRole.buyer => roleAccentBuyer,
    };
  }

  /// Decorative accent for [role] on dark grounds (darkSurface and the
  /// dark-teal sidebar).
  static Color darkRoleAccent(UserRole role) {
    return switch (role) {
      UserRole.farmer => darkPrimary,
      UserRole.trucker => darkRoleAccentTrucker,
      UserRole.buyer => darkRoleAccentBuyer,
    };
  }

  // ── Legacy aliases — do not use in new code (removed by the token sweep)
  static const Color primaryGreen = primary;
  static const Color primaryGreenLight = primaryLight;
  static const Color primaryGreenDark = primaryDeep;
  static const Color accentGold = accentSoft;
  static const Color accentGoldLight = accentSoftLight;

  // ── Status colors (inline text/icons on light surfaces) ──────────────
  static const Color statusSuccess = Color(0xFF15803D);
  static const Color statusWarning = Color(0xFFB45309);
  static const Color statusError = Color(0xFFDC2626);
  static const Color statusInfo = Color(0xFF1D4ED8);
  static const Color statusTransit = Color(0xFF6D28D9);

  // ── Status container pairs (chip background / foreground) ────────────
  static const Color statusSuccessContainer = Color(0xFFDCFCE7);
  static const Color onStatusSuccessContainer = Color(0xFF14532D);
  static const Color statusWarningContainer = Color(0xFFFEF3C7);
  static const Color onStatusWarningContainer = Color(0xFF92400E);
  static const Color statusErrorContainer = Color(0xFFFEE2E2);
  static const Color onStatusErrorContainer = Color(0xFF991B1B);
  static const Color statusInfoContainer = Color(0xFFDBEAFE);
  static const Color onStatusInfoContainer = Color(0xFF1E40AF);
  static const Color statusTransitContainer = Color(0xFFEDE9FE);
  static const Color onStatusTransitContainer = Color(0xFF5B21B6);
  static const Color statusNeutralContainer = Color(0xFFF1F5F9);
  static const Color onStatusNeutralContainer = Color(0xFF334155);

  // ── Dark theme tokens ─────────────────────────────────────────────────
  static const Color darkBackground = Color(0xFF0E1513);
  static const Color darkSurface = Color(0xFF182420);
  static const Color darkBorder = Color(0xFF2C3B36);
  static const Color darkTextPrimary = Color(0xFFECF1EF);
  static const Color darkTextMuted = Color(0xFF9BA8A3);
  static const Color darkPrimary = Color(0xFF5FC4AE);
  static const Color darkOnPrimary = Color(0xFF06302A);
  static const Color darkStatusSuccess = Color(0xFF4ADE80);
  static const Color darkStatusWarning = Color(0xFFFBBF24);
  static const Color darkStatusError = Color(0xFFF87171);
  static const Color darkStatusInfo = Color(0xFF60A5FA);
  static const Color darkStatusTransit = Color(0xFFA78BFA);

  /// Light theme — the default.
  static ThemeData light() {
    const colorScheme = ColorScheme(
      brightness: Brightness.light,
      primary: primary,
      onPrimary: Colors.white,
      primaryContainer: Color(0xFFD1EDE8),
      onPrimaryContainer: primaryDeep,
      secondary: accentSoft,
      onSecondary: Colors.white,
      secondaryContainer: Color(0xFFF6E3D4),
      onSecondaryContainer: Color(0xFF7A3814),
      tertiary: statusInfo,
      onTertiary: Colors.white,
      error: statusError,
      onError: Colors.white,
      errorContainer: statusErrorContainer,
      onErrorContainer: onStatusErrorContainer,
      surface: surfaceWhite,
      onSurface: textDark,
      onSurfaceVariant: textMuted,
      outline: borderLight,
      outlineVariant: Color(0xFFF1F5F9),
      shadow: Color(0x1A000000),
      inverseSurface: textDark,
      onInverseSurface: Colors.white,
      inversePrimary: Color(0xFF7DCDB9),
      surfaceContainerHighest: backgroundCream,
    );

    return _build(
      colorScheme: colorScheme,
      scaffoldBackground: backgroundCream,
      surface: surfaceWhite,
      border: borderLight,
      text: textDark,
      muted: textMuted,
      appBarBackground: primary,
      appBarForeground: Colors.white,
      snackBarBackground: textDark,
      snackBarForeground: Colors.white,
    );
  }

  /// Dark theme — tokens live; not user-visible until the `Colors.*`
  /// hardcoding sweep is complete and a theme toggle ships.
  static ThemeData dark() {
    const colorScheme = ColorScheme(
      brightness: Brightness.dark,
      primary: darkPrimary,
      onPrimary: darkOnPrimary,
      primaryContainer: primaryDeep,
      onPrimaryContainer: Color(0xFFD1EDE8),
      secondary: accentSoftLight,
      onSecondary: Color(0xFF3A2415),
      secondaryContainer: Color(0xFF3A2415),
      onSecondaryContainer: accentSoftLight,
      tertiary: darkStatusInfo,
      onTertiary: Color(0xFF0B1D33),
      error: darkStatusError,
      onError: Color(0xFF350D0B),
      errorContainer: Color(0xFF2C1A18),
      onErrorContainer: darkStatusError,
      surface: darkSurface,
      onSurface: darkTextPrimary,
      onSurfaceVariant: darkTextMuted,
      outline: darkBorder,
      outlineVariant: Color(0xFF1F2B27),
      shadow: Color(0x00000000),
      inverseSurface: darkTextPrimary,
      onInverseSurface: textDark,
      inversePrimary: primary,
      surfaceContainerHighest: darkBackground,
    );

    return _build(
      colorScheme: colorScheme,
      scaffoldBackground: darkBackground,
      surface: darkSurface,
      border: darkBorder,
      text: darkTextPrimary,
      muted: darkTextMuted,
      appBarBackground: darkSurface,
      appBarForeground: darkTextPrimary,
      snackBarBackground: darkTextPrimary,
      snackBarForeground: textDark,
    );
  }

  static ThemeData _build({
    required ColorScheme colorScheme,
    required Color scaffoldBackground,
    required Color surface,
    required Color border,
    required Color text,
    required Color muted,
    required Color appBarBackground,
    required Color appBarForeground,
    required Color snackBarBackground,
    required Color snackBarForeground,
  }) {
    return ThemeData(
      useMaterial3: true,
      colorScheme: colorScheme,
      scaffoldBackgroundColor: scaffoldBackground,
      // Bundled families: Latin in Noto Sans, Ethiopic glyphs fall back
      // to Noto Sans Ethiopic — one consistent face on every device.
      fontFamily: 'NotoSans',
      textTheme: _textTheme(text: text, muted: muted).apply(
        fontFamily: 'NotoSans',
        fontFamilyFallback: ethiopicFontFallback,
      ),

      // ── AppBar ────────────────────────────────────────────────────
      appBarTheme: AppBarTheme(
        backgroundColor: appBarBackground,
        foregroundColor: appBarForeground,
        elevation: 0,
        centerTitle: false,
      ),

      // ── Cards ─────────────────────────────────────────────────────
      cardTheme: CardThemeData(
        color: surface,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(12),
          side: BorderSide(color: border),
        ),
        margin: EdgeInsets.zero,
      ),

      // ── Buttons ───────────────────────────────────────────────────
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: colorScheme.primary,
          foregroundColor: colorScheme.onPrimary,
          minimumSize: const Size.fromHeight(48),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
          ),
          textStyle: const TextStyle(
            fontSize: 16,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          backgroundColor: colorScheme.primary,
          foregroundColor: colorScheme.onPrimary,
          minimumSize: const Size.fromHeight(48),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
          ),
          textStyle: const TextStyle(
            fontSize: 16,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          foregroundColor: colorScheme.primary,
          minimumSize: const Size.fromHeight(48),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
          ),
          side: BorderSide(color: colorScheme.primary),
        ),
      ),
      // Money color: the FAB is the role's create verb; together with
      // money-moving buttons it is the only place terracotta may appear.
      floatingActionButtonTheme: const FloatingActionButtonThemeData(
        backgroundColor: money,
        foregroundColor: Colors.white,
        elevation: 4,
        shape: CircleBorder(),
      ),

      // ── Inputs ────────────────────────────────────────────────────
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: surface,
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: border),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: border),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: colorScheme.primary, width: 2),
        ),
        errorBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: colorScheme.error),
        ),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 16,
        ),
      ),

      // ── Chips ─────────────────────────────────────────────────────
      chipTheme: ChipThemeData(
        backgroundColor: surface,
        selectedColor: colorScheme.primary,
        // Chip labels do not inherit the text theme's font, so without the
        // Ethiopic fallback an Amharic label falls to a late-loading system
        // font on web and the chip is sized to placeholder glyphs (SIM-22).
        labelStyle: TextStyle(
          fontSize: 13,
          fontWeight: FontWeight.w500,
          color: text,
          fontFamily: 'NotoSans',
          fontFamilyFallback: ethiopicFontFallback,
        ),
        secondaryLabelStyle: TextStyle(
          fontSize: 13,
          fontWeight: FontWeight.w500,
          color: colorScheme.onPrimary,
          fontFamily: 'NotoSans',
          fontFamilyFallback: ethiopicFontFallback,
        ),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(20),
          side: BorderSide(color: border),
        ),
        showCheckmark: false,
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      ),

      // ── Bottom Navigation ─────────────────────────────────────────
      bottomAppBarTheme: BottomAppBarThemeData(
        color: surface,
        elevation: 8,
        surfaceTintColor: Colors.transparent,
      ),

      // ── Divider ───────────────────────────────────────────────────
      dividerTheme: DividerThemeData(
        color: border,
        thickness: 1,
        space: 0,
      ),

      // ── SnackBar ──────────────────────────────────────────────────
      snackBarTheme: SnackBarThemeData(
        backgroundColor: snackBarBackground,
        contentTextStyle: TextStyle(color: snackBarForeground),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(8),
        ),
        behavior: SnackBarBehavior.floating,
      ),
    );
  }

  static TextTheme _textTheme({required Color text, required Color muted}) {
    return TextTheme(
      displayLarge: TextStyle(
        fontSize: 32,
        fontWeight: FontWeight.w700,
        color: text,
        height: 1.2,
      ),
      displayMedium: TextStyle(
        fontSize: 28,
        fontWeight: FontWeight.w700,
        color: text,
        height: 1.2,
      ),
      headlineLarge: TextStyle(
        fontSize: 24,
        fontWeight: FontWeight.w700,
        color: text,
        height: 1.3,
      ),
      headlineMedium: TextStyle(
        fontSize: 20,
        fontWeight: FontWeight.w600,
        color: text,
        height: 1.3,
      ),
      headlineSmall: TextStyle(
        fontSize: 18,
        fontWeight: FontWeight.w600,
        color: text,
        height: 1.3,
      ),
      titleLarge: TextStyle(
        fontSize: 16,
        fontWeight: FontWeight.w600,
        color: text,
      ),
      titleMedium: TextStyle(
        fontSize: 14,
        fontWeight: FontWeight.w600,
        color: text,
      ),
      titleSmall: TextStyle(
        fontSize: 12,
        fontWeight: FontWeight.w600,
        color: text,
      ),
      bodyLarge: TextStyle(
        fontSize: 16,
        fontWeight: FontWeight.w400,
        color: text,
      ),
      bodyMedium: TextStyle(
        fontSize: 14,
        fontWeight: FontWeight.w400,
        color: text,
      ),
      bodySmall: TextStyle(
        fontSize: 12,
        fontWeight: FontWeight.w400,
        color: muted,
      ),
      labelLarge: TextStyle(
        fontSize: 14,
        fontWeight: FontWeight.w600,
        color: text,
      ),
      labelMedium: TextStyle(
        fontSize: 12,
        fontWeight: FontWeight.w500,
        color: text,
      ),
      labelSmall: TextStyle(
        fontSize: 11,
        fontWeight: FontWeight.w500,
        color: muted,
      ),
    );
  }
}
