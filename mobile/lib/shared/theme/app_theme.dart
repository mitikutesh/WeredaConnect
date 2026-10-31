import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';

/// WeredaConnect design system: "Fieldway" (the design canvas boards in
/// specs/design-canvas, canvas K6DUM33R6apPwa2jmdsLax).
///
/// Token rules:
/// - Primary: field green (#1E5B3E): advancing actions, active nav, links
/// - Ink: soil dark (#15201A) for text and the dark create button, on a
///   chalk ground (#F5F4EE) with whisper borders (#E3E1D8)
/// - Money: harvest brown (#8A5410): amounts, money links and the
///   trucker's accents; gold (#D9922B) only for fills (load bars, the
///   trucker's create button), never behind small text
/// - Status: closed 5-token vocabulary (+ neutral), always paired with an
///   icon and a label, color is never the only signal
/// - Role accents: nav chrome only ([roleAccent], [roleAccentTint]): green
///   for farmers and buyers, brown on gold for truckers, as on the boards
/// - Dark theme tokens are defined here from day one; `themeMode` stays
///   pinned to light until the raw `Colors.*` sweep makes screens dark-safe
class AppTheme {
  AppTheme._();

  // ── Brand colors ──────────────────────────────────────────────────────
  static const Color primary = Color(0xFF1E5B3E);
  static const Color primaryLight = Color(0xFF3C7A5A);
  static const Color primaryDeep = Color(0xFF15402B);
  static const Color money = Color(0xFF8A5410);
  static const Color accentSoft = Color(0xFFD9922B);
  static const Color accentSoftLight = Color(0xFFFAEED8);
  static const Color backgroundCream = Color(0xFFF5F4EE);
  static const Color surfaceWhite = Color(0xFFFFFFFF);
  static const Color textDark = Color(0xFF15201A);

  /// The Fieldway boards' Latin face (bundled, see pubspec.yaml).
  static const String fontFamily = 'Manrope';

  /// Bundled Ethiopic-script font, appended to every text style (theme text
  /// and chip labels) so Amharic never waits on a system fallback font.
  static const List<String> ethiopicFontFallback = ['NotoSansEthiopic'];
  static const Color textMuted = Color(0xFF5E6862);
  static const Color borderLight = Color(0xFFE3E1D8);

  // ── Fieldway Design Canvas tokens (specs/features/live-trip.md) ───────
  static const Color fieldGreen = Color(0xFF1E5B3E);
  static const Color fieldGreenTint = Color(0xFFE3EEE6);
  static const Color soilDark = Color(0xFF15201A);
  static const Color chalkBackground = Color(0xFFF5F4EE);
  static const Color whisperBorder = Color(0xFFE3E1D8);
  static const Color mutedSteel = Color(0xFF5E6862);
  static const Color unselectedNav = Color(0xFF3F4843);

  /// Secondary ink on cards (board #4A544E) and the action-needed card's
  /// cream ground and dark brown ink.
  static const Color inkSecondary = Color(0xFF4A544E);
  static const Color actionCream = Color(0xFFFAEED8);
  static const Color actionInk = Color(0xFF6B400B);

  /// The segmented switch track and a neutral pill.
  static const Color segmentTrack = Color(0xFFECEAE2);

  /// The buyer's drop-off blue and its tint (pins, avatars, info notes).
  static const Color dropoffBlue = Color(0xFF2E4C7D);
  static const Color dropoffBlueTint = Color(0xFFE6ECF5);

  /// The trucker home's dark header and the card set into it.
  static const Color soilCard = Color(0xFF22302A);
  static const Color soilMuted = Color(0xFFB9C2BD);
  static const Color soilTrack = Color(0xFF33433B);
  static const Color soilGreen = Color(0xFF8FC5A4);

  // ── Role identity accents (nav chrome only, no status meaning) ───────
  // The boards' selected nav item: farmers and buyers get field green on a
  // green tint, truckers harvest brown on a gold tint. Buttons, cards and
  // the status vocabulary stay shared by every role.
  static const Color roleAccentTrucker = money;
  static const Color roleAccentTruckerTint = accentSoftLight;

  static const Color darkRoleAccentTrucker = Color(0xFFE9B866);

  /// Accent for [role]'s selected nav item on light grounds.
  static Color roleAccent(UserRole role) {
    return switch (role) {
      UserRole.farmer || UserRole.buyer => primary,
      UserRole.trucker => roleAccentTrucker,
    };
  }

  /// The pill behind [role]'s selected nav icon.
  static Color roleAccentTint(UserRole role) {
    return switch (role) {
      UserRole.farmer || UserRole.buyer => fieldGreenTint,
      UserRole.trucker => roleAccentTruckerTint,
    };
  }

  /// Accent for [role] on dark grounds.
  static Color darkRoleAccent(UserRole role) {
    return switch (role) {
      UserRole.farmer || UserRole.buyer => darkPrimary,
      UserRole.trucker => darkRoleAccentTrucker,
    };
  }

  /// The role's create button: soil dark for farmers and buyers, gold with
  /// dark ink for truckers (every Home board).
  static Color createButtonBackground(UserRole role) =>
      role == UserRole.trucker ? accentSoft : soilDark;
  static Color createButtonForeground(UserRole role) =>
      role == UserRole.trucker ? soilDark : Colors.white;

  // ── Legacy aliases — do not use in new code (removed by the token sweep)
  static const Color primaryGreen = primary;
  static const Color primaryGreenLight = primaryLight;
  static const Color primaryGreenDark = primaryDeep;
  static const Color accentGold = accentSoft;
  static const Color accentGoldLight = accentSoftLight;

  // ── Status colors (inline text/icons on light surfaces) ──────────────
  static const Color statusSuccess = Color(0xFF2B6E49);
  static const Color statusWarning = Color(0xFF9A5B0F);
  static const Color statusError = Color(0xFFB3261E);
  static const Color statusInfo = dropoffBlue;
  static const Color statusTransit = Color(0xFFA8640F);

  // ── Status container pairs (chip background / foreground) ────────────
  static const Color statusSuccessContainer = fieldGreenTint;
  static const Color onStatusSuccessContainer = primaryDeep;
  static const Color statusWarningContainer = actionCream;
  static const Color onStatusWarningContainer = actionInk;
  static const Color statusErrorContainer = Color(0xFFF9E3E0);
  static const Color onStatusErrorContainer = Color(0xFF7A1C14);
  static const Color statusInfoContainer = dropoffBlueTint;
  static const Color onStatusInfoContainer = Color(0xFF1F3557);
  static const Color statusTransitContainer = actionCream;
  static const Color onStatusTransitContainer = money;
  static const Color statusNeutralContainer = segmentTrack;
  static const Color onStatusNeutralContainer = unselectedNav;

  // ── Dark theme tokens ─────────────────────────────────────────────────
  static const Color darkBackground = Color(0xFF0E1513);
  static const Color darkSurface = Color(0xFF182420);
  static const Color darkBorder = Color(0xFF2C3B36);
  static const Color darkTextPrimary = Color(0xFFECF1EF);
  static const Color darkTextMuted = Color(0xFF9BA8A3);
  static const Color darkPrimary = Color(0xFF7CC59A);
  static const Color darkOnPrimary = Color(0xFF0B2A1B);
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
      primaryContainer: fieldGreenTint,
      onPrimaryContainer: primaryDeep,
      secondary: money,
      onSecondary: Colors.white,
      secondaryContainer: accentSoftLight,
      onSecondaryContainer: actionInk,
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
      outlineVariant: segmentTrack,
      shadow: Color(0x1A000000),
      inverseSurface: textDark,
      onInverseSurface: Colors.white,
      inversePrimary: darkPrimary,
      surfaceContainerHighest: backgroundCream,
    );

    // Every Fieldway board puts a screen's title on the page itself: chalk
    // ground, dark text, no colored bar.
    return _build(
      colorScheme: colorScheme,
      scaffoldBackground: chalkBackground,
      surface: surfaceWhite,
      border: borderLight,
      text: textDark,
      muted: textMuted,
      appBarBackground: chalkBackground,
      appBarForeground: soilDark,
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
      onPrimaryContainer: fieldGreenTint,
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
      // Bundled families: Latin in Manrope, Ethiopic glyphs fall back to
      // Noto Sans Ethiopic, one consistent face on every device.
      fontFamily: fontFamily,
      textTheme: _textTheme(text: text, muted: muted).apply(
        fontFamily: fontFamily,
        fontFamilyFallback: ethiopicFontFallback,
      ),

      // ── AppBar ────────────────────────────────────────────────────
      appBarTheme: AppBarTheme(
        backgroundColor: appBarBackground,
        foregroundColor: appBarForeground,
        elevation: 0,
        scrolledUnderElevation: 0,
        surfaceTintColor: Colors.transparent,
        centerTitle: false,
        titleTextStyle: TextStyle(
          fontSize: 20,
          fontWeight: FontWeight.w800,
          color: appBarForeground,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
      ),

      // ── Cards ─────────────────────────────────────────────────────
      cardTheme: CardThemeData(
        color: surface,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(18),
          side: BorderSide(color: border),
        ),
        margin: EdgeInsets.zero,
      ),

      // ── Buttons ───────────────────────────────────────────────────
      // The boards' primary button: 56 tall, radius 16, bold label.
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: colorScheme.primary,
          foregroundColor: colorScheme.onPrimary,
          minimumSize: const Size.fromHeight(52),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(16),
          ),
          textStyle: const TextStyle(
            fontFamily: fontFamily,
            fontFamilyFallback: ethiopicFontFallback,
            fontSize: 16,
            fontWeight: FontWeight.w700,
          ),
        ),
      ),
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          backgroundColor: colorScheme.primary,
          foregroundColor: colorScheme.onPrimary,
          elevation: 0,
          minimumSize: const Size.fromHeight(52),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(16),
          ),
          textStyle: const TextStyle(
            fontFamily: fontFamily,
            fontFamilyFallback: ethiopicFontFallback,
            fontSize: 16,
            fontWeight: FontWeight.w700,
          ),
        ),
      ),
      // The boards' secondary button: white, whisper border, dark ink.
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          foregroundColor: text,
          backgroundColor: surface,
          minimumSize: const Size.fromHeight(52),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(16),
          ),
          side: BorderSide(color: border),
          textStyle: const TextStyle(
            fontFamily: fontFamily,
            fontFamilyFallback: ethiopicFontFallback,
            fontSize: 15,
            fontWeight: FontWeight.w700,
          ),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: colorScheme.primary,
          textStyle: const TextStyle(
            fontFamily: fontFamily,
            fontFamilyFallback: ethiopicFontFallback,
            fontSize: 14,
            fontWeight: FontWeight.w700,
          ),
        ),
      ),
      // The role's create button ([createButtonBackground] per role).
      floatingActionButtonTheme: FloatingActionButtonThemeData(
        backgroundColor: soilDark,
        foregroundColor: Colors.white,
        elevation: 4,
        extendedTextStyle: const TextStyle(
          fontSize: 15,
          fontWeight: FontWeight.w800,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
        ),
      ),

      // ── Inputs ────────────────────────────────────────────────────
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: surface,
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(14),
          borderSide: BorderSide(color: border),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(14),
          borderSide: BorderSide(color: border),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(14),
          borderSide: BorderSide(color: colorScheme.primary, width: 2),
        ),
        errorBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(14),
          borderSide: BorderSide(color: colorScheme.error),
        ),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 14,
          vertical: 14,
        ),
        hintStyle: TextStyle(
          color: muted,
          fontWeight: FontWeight.w500,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
      ),

      // ── Chips ─────────────────────────────────────────────────────
      chipTheme: ChipThemeData(
        backgroundColor: surface,
        selectedColor: colorScheme.primary,
        checkmarkColor: colorScheme.onPrimary,
        // Chip labels do not inherit the text theme's font, so without the
        // Ethiopic fallback an Amharic label falls to a late-loading system
        // font on web and the chip is sized to placeholder glyphs (SIM-22).
        labelStyle: TextStyle(
          fontSize: 13,
          fontWeight: FontWeight.w600,
          color: text,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        secondaryLabelStyle: TextStyle(
          fontSize: 13,
          fontWeight: FontWeight.w500,
          color: colorScheme.onPrimary,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(999),
          side: BorderSide(color: border),
        ),
        showCheckmark: false,
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      ),

      // ── Bottom Navigation ─────────────────────────────────────────
      bottomAppBarTheme: BottomAppBarThemeData(
        color: surface,
        elevation: 0,
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
        contentTextStyle: TextStyle(
          color: snackBarForeground,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(14),
        ),
        behavior: SnackBarBehavior.floating,
      ),

      // ── Dialog ────────────────────────────────────────────────────
      dialogTheme: DialogThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(18),
        ),
        titleTextStyle: TextStyle(
          fontSize: 18,
          fontWeight: FontWeight.w800,
          color: text,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
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
