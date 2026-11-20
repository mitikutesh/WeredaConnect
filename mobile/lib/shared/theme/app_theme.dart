import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';

/// The colours that tell a person whose app they are in (design canvas
/// page M, "White, role colours": specs/design-canvas).
///
/// One set per role, plus [brand] for the screens before sign-in. The role
/// colour goes on links, amounts, progress fills, tint pills, the selected
/// tab's dot and the person's own trail on the map. Everything else stays
/// black on white, so the colour always means "this is you".
///
/// Read it from the theme: `context.roleColors.primary`. The app's theme is
/// built for the signed-in role ([AppTheme.light]), so a screen never asks
/// who is signed in to pick a colour.
@immutable
class RoleColors extends ThemeExtension<RoleColors> {
  /// Links, amounts, progress fills, the text on a [tint] pill.
  final Color primary;

  /// A pressed or hovered link, text on [soft].
  final Color dark;

  /// The role colour on a black ground (the nav pill, a dark tile).
  final Color mid;

  /// The pill and icon tile ground behind [primary] text.
  final Color tint;

  /// The login header's pastel.
  final Color soft;

  const RoleColors({
    required this.primary,
    required this.dark,
    required this.mid,
    required this.tint,
    required this.soft,
  });

  /// Coffee plum.
  static const farmer = RoleColors(
    primary: Color(0xFF9A45A0),
    dark: Color(0xFF6B2A69),
    mid: Color(0xFFD4A6D3),
    tint: Color(0xFFF4E6F3),
    soft: Color(0xFFE8CFE7),
  );

  /// Road blue.
  static const trucker = RoleColors(
    primary: Color(0xFF2B62C4),
    dark: Color(0xFF1F4A96),
    mid: Color(0xFFA4BFEE),
    tint: Color(0xFFE4ECFA),
    soft: Color(0xFFCDDCF6),
  );

  /// Highland green.
  static const buyer = RoleColors(
    primary: Color(0xFF18775F),
    dark: Color(0xFF105443),
    mid: Color(0xFF9CD1BF),
    tint: Color(0xFFE3F0EB),
    soft: Color(0xFFCDE5DC),
  );

  /// Before sign-in the brand is black on white.
  static const brand = RoleColors(
    primary: AppTheme.ink,
    dark: Color(0xFF0C0A0A),
    mid: Color(0xFFCAC9C9),
    tint: AppTheme.paper,
    soft: AppTheme.sand,
  );

  /// The set for [role], [brand] when nobody is signed in.
  static RoleColors forRole(UserRole? role) {
    return switch (role) {
      UserRole.farmer => farmer,
      UserRole.trucker => trucker,
      UserRole.buyer => buyer,
      null => brand,
    };
  }

  /// The set of the theme above [context]. A subtree themed without the
  /// app's [AppTheme.light] (a bare test MaterialApp) gets [brand].
  static RoleColors of(BuildContext context) {
    return Theme.of(context).extension<RoleColors>() ?? brand;
  }

  @override
  RoleColors copyWith({
    Color? primary,
    Color? dark,
    Color? mid,
    Color? tint,
    Color? soft,
  }) {
    return RoleColors(
      primary: primary ?? this.primary,
      dark: dark ?? this.dark,
      mid: mid ?? this.mid,
      tint: tint ?? this.tint,
      soft: soft ?? this.soft,
    );
  }

  @override
  RoleColors lerp(ThemeExtension<RoleColors>? other, double t) {
    if (other is! RoleColors) return this;
    return RoleColors(
      primary: Color.lerp(primary, other.primary, t)!,
      dark: Color.lerp(dark, other.dark, t)!,
      mid: Color.lerp(mid, other.mid, t)!,
      tint: Color.lerp(tint, other.tint, t)!,
      soft: Color.lerp(soft, other.soft, t)!,
    );
  }
}

/// `context.roleColors`: the signed-in role's colours (see [RoleColors]).
extension RoleColorsContext on BuildContext {
  RoleColors get roleColors => RoleColors.of(this);
}

/// WeredaConnect design system: white ground, black text, one orange
/// action, one colour per role (design canvas page M, archived in
/// specs/design-canvas).
///
/// Token rules:
/// - Ground and text: white ([ground]) and black ([ink]); secondary text
///   in [slate] and [inkSoft]
/// - The one action: [orange] with black ink, a 54 tall pill. The second
///   strongest button is black with white ink, the third an outlined pill
/// - Surfaces: [paper] for a raised card (with [line] and a soft shadow),
///   [sand] for a flat tile or note
/// - The role colour ([RoleColors], `context.roleColors`): links, amounts,
///   progress fills, tint pills and the person's own trail. Never a static
///   token: it changes with who is signed in
/// - The truck and "needs attention": [orange] on [orangeTint] with
///   [orangeInk] text
/// - Status: closed 5-token vocabulary (+ neutral), always paired with an
///   icon and a label, color is never the only signal
/// - Dark theme tokens are defined here from day one; `themeMode` stays
///   pinned to light until the raw `Colors.*` sweep makes screens dark-safe
class AppTheme {
  AppTheme._();

  // ── Shared colours ────────────────────────────────────────────────────
  /// Text, strong buttons, the nav pill, the brand before sign-in.
  static const Color ink = Color(0xFF1A1516);

  /// The page.
  static const Color ground = Color(0xFFFFFFFF);
  static const Color surfaceWhite = Color(0xFFFFFFFF);

  /// A raised card, a filled input, a round icon button.
  static const Color paper = Color(0xFFF5F3F1);

  /// A flat tile or note.
  static const Color sand = Color(0xFFE4D5C7);

  /// The one action, the truck, "needs attention". Never behind small
  /// white text: its ink is [ink] (7.3:1).
  static const Color orange = Color(0xFFF0873A);
  static const Color orangeTint = Color(0xFFFBE6D6);

  /// Text on [orangeTint] (6.0:1) and warning text on white.
  static const Color orangeInk = Color(0xFF8A4510);

  /// Secondary text, the drop-off pin, information (7.4:1 on white).
  static const Color slate = Color(0xFF5C5450);

  /// Text on [sand] and unselected controls (6.6:1 on sand).
  static const Color inkSoft = Color(0xFF4A4340);

  /// A card's decorative edge.
  static const Color line = Color(0xFFDDD5CE);

  /// The edge of a control a user must find: enabled input borders, radio
  /// rings, outlined buttons and unselected chips. 3.6:1 on white, past
  /// the 3:1 that WCAG asks of a control's boundary. [line] stays for a
  /// card's decorative edge.
  static const Color fieldBorder = Color(0xFF8E857E);

  /// A progress bar's empty track and a step not yet reached.
  static const Color track = Color(0xFFD6CBC3);

  /// The segmented switch track and a neutral pill.
  static const Color segmentTrack = Color(0xFFE5DDD8);

  /// The decorative header sketch's stroke.
  static const Color sketch = Color(0xFFC9BEB5);

  /// The drawn map's ground (live trip, deliveries card, route preview).
  static const Color mapGround = Color(0xFFDCD5C9);

  /// The pale roads drawn over [mapGround] on the route preview.
  static const Color mapRoad = Color(0xFFEFEBE4);

  /// The dashed edge of the halo around a truck's last check-in.
  static const Color haloEdge = Color(0xFFA85518);

  /// The selected entry's ground in the desktop sidebar.
  static const Color sidebarSelected = Color(0xFFEFE3DC);

  /// The soft shadow under a raised card, a floating button and the bar.
  static const Color shadowSoft = Color(0x241A1516);

  /// The Latin face of the boards (bundled, see pubspec.yaml).
  static const String fontFamily = 'BricolageGrotesque';

  /// Bundled Ethiopic-script font, appended to every text style (theme text
  /// and chip labels) so Amharic never waits on a system fallback font.
  static const List<String> ethiopicFontFallback = ['NotoSansEthiopic'];

  /// The drop-off's slate and its tint (pins, avatars, info notes).
  static const Color dropoffSlate = slate;
  static const Color dropoffSlateTint = Color(0xFFEBEAEA);

  /// A card set into a black ground, and text and tracks on black.
  static const Color inkCard = Color(0xFF2A2526);
  static const Color inkMuted = Color(0xFFCAC9C9);
  static const Color inkTrack = Color(0xFF3D3637);

  // ── Status colors (inline text/icons on light surfaces) ──────────────
  static const Color statusSuccess = Color(0xFF18775F);
  static const Color statusWarning = orangeInk;
  static const Color statusError = Color(0xFFB3261E);
  static const Color statusInfo = slate;
  static const Color statusTransit = orangeInk;

  // ── Status container pairs (chip background / foreground) ────────────
  static const Color statusSuccessContainer = Color(0xFFE3F0EB);
  static const Color onStatusSuccessContainer = Color(0xFF105443);
  static const Color statusWarningContainer = orangeTint;
  static const Color onStatusWarningContainer = orangeInk;
  static const Color statusErrorContainer = Color(0xFFFBE9E7);
  static const Color onStatusErrorContainer = Color(0xFF7A1C14);
  static const Color statusInfoContainer = dropoffSlateTint;
  static const Color onStatusInfoContainer = inkSoft;
  static const Color statusTransitContainer = orangeTint;
  static const Color onStatusTransitContainer = orangeInk;
  static const Color statusNeutralContainer = segmentTrack;
  static const Color onStatusNeutralContainer = inkSoft;

  // ── Dark theme tokens ─────────────────────────────────────────────────
  static const Color darkBackground = Color(0xFF0C0A0A);
  static const Color darkSurface = Color(0xFF1A1516);
  static const Color darkBorder = Color(0xFF3D3637);
  static const Color darkTextPrimary = Color(0xFFF5F3F1);
  static const Color darkTextMuted = Color(0xFFCAC9C9);
  static const Color darkStatusSuccess = Color(0xFF4ADE80);
  static const Color darkStatusWarning = Color(0xFFFBBF24);
  static const Color darkStatusError = Color(0xFFF87171);
  static const Color darkStatusInfo = Color(0xFF60A5FA);
  static const Color darkStatusTransit = Color(0xFFA78BFA);

  /// Light theme, the default, in [role]'s colours ([RoleColors.brand]
  /// before sign-in).
  static ThemeData light({UserRole? role}) {
    final colors = RoleColors.forRole(role);
    final colorScheme = ColorScheme(
      brightness: Brightness.light,
      primary: colors.primary,
      onPrimary: Colors.white,
      primaryContainer: colors.tint,
      onPrimaryContainer: colors.dark,
      secondary: orange,
      onSecondary: ink,
      secondaryContainer: orangeTint,
      onSecondaryContainer: orangeInk,
      tertiary: statusInfo,
      onTertiary: Colors.white,
      error: statusError,
      onError: Colors.white,
      errorContainer: statusErrorContainer,
      onErrorContainer: onStatusErrorContainer,
      surface: surfaceWhite,
      onSurface: ink,
      onSurfaceVariant: slate,
      outline: line,
      outlineVariant: segmentTrack,
      shadow: shadowSoft,
      inverseSurface: ink,
      onInverseSurface: Colors.white,
      inversePrimary: colors.mid,
      surfaceContainerHighest: paper,
    );

    // Every board puts a screen's title on the page itself: white ground,
    // black text, no colored bar.
    return _build(
      colorScheme: colorScheme,
      roleColors: colors,
      scaffoldBackground: ground,
      surface: surfaceWhite,
      inputFill: paper,
      border: line,
      fieldBorder: fieldBorder,
      text: ink,
      muted: slate,
      appBarBackground: ground,
      appBarForeground: ink,
      snackBarBackground: ink,
      snackBarForeground: Colors.white,
    );
  }

  /// Dark theme: tokens live; not user-visible until the `Colors.*`
  /// hardcoding sweep is complete and a theme toggle ships.
  static ThemeData dark({UserRole? role}) {
    final colors = RoleColors.forRole(role);
    final onDark = RoleColors(
      primary: colors.mid,
      dark: colors.tint,
      mid: colors.mid,
      tint: colors.dark,
      soft: colors.dark,
    );
    final colorScheme = ColorScheme(
      brightness: Brightness.dark,
      primary: onDark.primary,
      onPrimary: ink,
      primaryContainer: colors.dark,
      onPrimaryContainer: colors.tint,
      secondary: orange,
      onSecondary: ink,
      secondaryContainer: const Color(0xFF3A2415),
      onSecondaryContainer: orangeTint,
      tertiary: darkStatusInfo,
      onTertiary: const Color(0xFF0B1D33),
      error: darkStatusError,
      onError: const Color(0xFF350D0B),
      errorContainer: const Color(0xFF2C1A18),
      onErrorContainer: darkStatusError,
      surface: darkSurface,
      onSurface: darkTextPrimary,
      onSurfaceVariant: darkTextMuted,
      outline: darkBorder,
      outlineVariant: inkCard,
      shadow: const Color(0x00000000),
      inverseSurface: darkTextPrimary,
      onInverseSurface: ink,
      inversePrimary: colors.primary,
      surfaceContainerHighest: darkBackground,
    );

    return _build(
      colorScheme: colorScheme,
      roleColors: onDark,
      scaffoldBackground: darkBackground,
      surface: darkSurface,
      inputFill: inkCard,
      border: darkBorder,
      fieldBorder: darkTextMuted,
      text: darkTextPrimary,
      muted: darkTextMuted,
      appBarBackground: darkSurface,
      appBarForeground: darkTextPrimary,
      snackBarBackground: darkTextPrimary,
      snackBarForeground: ink,
    );
  }

  static ThemeData _build({
    required ColorScheme colorScheme,
    required RoleColors roleColors,
    required Color scaffoldBackground,
    required Color surface,
    required Color inputFill,
    required Color border,
    required Color fieldBorder,
    required Color text,
    required Color muted,
    required Color appBarBackground,
    required Color appBarForeground,
    required Color snackBarBackground,
    required Color snackBarForeground,
  }) {
    final isDark = colorScheme.brightness == Brightness.dark;
    // The pieces that have no slot in the colour scheme.
    final handle = isDark ? darkBorder : sketch;
    final emptyTrack = isDark ? inkTrack : track;
    final selection = isDark ? inkCard : sand;
    return ThemeData(
      useMaterial3: true,
      colorScheme: colorScheme,
      extensions: [roleColors],
      scaffoldBackgroundColor: scaffoldBackground,
      // Bundled families: Latin in Bricolage Grotesque, Ethiopic glyphs fall back to
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
        color: inputFill,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(24),
          side: BorderSide(color: border),
        ),
        margin: EdgeInsets.zero,
      ),

      // ── Buttons ───────────────────────────────────────────────────
      // The boards' one action: an orange pill, 54 tall, black bold label.
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: colorScheme.secondary,
          foregroundColor: colorScheme.onSecondary,
          minimumSize: const Size.fromHeight(54),
          shape: const StadiumBorder(),
          textStyle: const TextStyle(
            fontFamily: fontFamily,
            fontFamilyFallback: ethiopicFontFallback,
            fontSize: 16,
            fontWeight: FontWeight.w800,
          ),
        ),
      ),
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          backgroundColor: colorScheme.secondary,
          foregroundColor: colorScheme.onSecondary,
          elevation: 0,
          minimumSize: const Size.fromHeight(54),
          shape: const StadiumBorder(),
          textStyle: const TextStyle(
            fontFamily: fontFamily,
            fontFamilyFallback: ethiopicFontFallback,
            fontSize: 16,
            fontWeight: FontWeight.w800,
          ),
        ),
      ),
      // The boards' quiet button: an outlined pill with dark ink.
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          foregroundColor: text,
          backgroundColor: Colors.transparent,
          minimumSize: const Size.fromHeight(54),
          shape: const StadiumBorder(),
          textStyle: const TextStyle(
            fontFamily: fontFamily,
            fontFamilyFallback: ethiopicFontFallback,
            fontSize: 15,
            fontWeight: FontWeight.w700,
          ),
        ).copyWith(
          // A disabled button keeps the quiet card edge.
          side: WidgetStateProperty.resolveWith(
            (states) => BorderSide(
              color:
                  states.contains(WidgetState.disabled) ? border : fieldBorder,
            ),
          ),
        ),
      ),
      // A link: the role's colour.
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
      // The role's create button: the orange pill inside the nav bar.
      floatingActionButtonTheme: const FloatingActionButtonThemeData(
        backgroundColor: orange,
        foregroundColor: ink,
        elevation: 4,
        extendedTextStyle: TextStyle(
          fontSize: 15,
          fontWeight: FontWeight.w800,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        shape: StadiumBorder(),
      ),

      // ── Inputs ────────────────────────────────────────────────────
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: inputFill,
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(16),
          borderSide: BorderSide(color: fieldBorder),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(16),
          borderSide: BorderSide(color: fieldBorder),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(16),
          borderSide: BorderSide(color: colorScheme.primary, width: 2),
        ),
        errorBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(16),
          borderSide: BorderSide(color: colorScheme.error),
        ),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 15,
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
        backgroundColor: Colors.transparent,
        selectedColor: text,
        checkmarkColor: surface,
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
          color: surface,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(999),
          side: BorderSide(color: fieldBorder),
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
        actionTextColor: orange,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(18),
        ),
        behavior: SnackBarBehavior.floating,
      ),

      // Bottom sheets
      // The boards' sheet: white, 28 px top corners, a 40 by 4 handle.
      bottomSheetTheme: BottomSheetThemeData(
        backgroundColor: surface,
        modalBackgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        modalElevation: 0,
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
        ),
        clipBehavior: Clip.antiAlias,
        dragHandleColor: handle,
        dragHandleSize: const Size(40, 4),
      ),

      // Dialog (replaces the current dialogTheme)
      dialogTheme: DialogThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(28),
        ),
        titleTextStyle: TextStyle(
          fontSize: 22,
          fontWeight: FontWeight.w800,
          letterSpacing: -0.5,
          height: 1.15,
          color: text,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        contentTextStyle: TextStyle(
          fontSize: 14,
          height: 1.45,
          color: muted,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        actionsPadding: const EdgeInsets.fromLTRB(20, 0, 20, 20),
      ),

      // Selection controls: the chosen one is black
      checkboxTheme: CheckboxThemeData(
        fillColor: WidgetStateProperty.resolveWith(
          (states) =>
              states.contains(WidgetState.selected) ? text : Colors.transparent,
        ),
        checkColor: WidgetStatePropertyAll(surface),
        side: BorderSide(color: fieldBorder, width: 1.5),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6)),
      ),
      radioTheme: RadioThemeData(
        fillColor: WidgetStateProperty.resolveWith(
          (states) =>
              states.contains(WidgetState.selected) ? text : fieldBorder,
        ),
      ),
      switchTheme: SwitchThemeData(
        trackColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected) ? text : emptyTrack,
        ),
        thumbColor: WidgetStatePropertyAll(surface),
        trackOutlineColor: const WidgetStatePropertyAll(Colors.transparent),
      ),

      // Progress: the role's colour on the empty track
      progressIndicatorTheme: ProgressIndicatorThemeData(
        color: colorScheme.primary,
        linearTrackColor: emptyTrack,
        circularTrackColor: Colors.transparent,
        linearMinHeight: 4,
        borderRadius: BorderRadius.circular(999),
      ),

      // Tabs: black label and line, slate for the others
      tabBarTheme: TabBarThemeData(
        labelColor: text,
        unselectedLabelColor: muted,
        indicatorColor: text,
        indicatorSize: TabBarIndicatorSize.label,
        dividerColor: border,
        labelStyle: const TextStyle(
          fontSize: 14,
          fontWeight: FontWeight.w800,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        unselectedLabelStyle: const TextStyle(
          fontSize: 14,
          fontWeight: FontWeight.w500,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
      ),

      // List tiles
      listTileTheme: ListTileThemeData(
        iconColor: text,
        textColor: text,
        titleTextStyle: TextStyle(
          fontSize: 15,
          fontWeight: FontWeight.w700,
          color: text,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        subtitleTextStyle: TextStyle(
          fontSize: 13,
          height: 1.3,
          color: muted,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      ),

      // Tooltip and popup menu
      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: colorScheme.inverseSurface,
          borderRadius: BorderRadius.circular(999),
        ),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
        textStyle: TextStyle(
          fontSize: 13,
          fontWeight: FontWeight.w700,
          color: colorScheme.onInverseSurface,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
      ),
      popupMenuTheme: PopupMenuThemeData(
        color: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 6,
        shadowColor: colorScheme.shadow,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(color: border),
        ),
        textStyle: TextStyle(
          fontSize: 15,
          fontWeight: FontWeight.w700,
          color: text,
          fontFamily: fontFamily,
          fontFamilyFallback: ethiopicFontFallback,
        ),
      ),

      // Text selection: black caret, sand highlight
      textSelectionTheme: TextSelectionThemeData(
        cursorColor: text,
        selectionColor: selection,
        selectionHandleColor: text,
      ),

      // Badge: something new is orange with black ink
      badgeTheme: const BadgeThemeData(
        backgroundColor: orange,
        textColor: ink,
      ),

      // Date and time pickers: the chosen day or hour is black
      datePickerTheme: DatePickerThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(28),
        ),
        headerForegroundColor: text,
        dividerColor: border,
        dayForegroundColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected)
              ? surface
              : states.contains(WidgetState.disabled)
                  ? fieldBorder
                  : text,
        ),
        dayBackgroundColor: WidgetStateProperty.resolveWith(
          (states) =>
              states.contains(WidgetState.selected) ? text : Colors.transparent,
        ),
        todayForegroundColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected) ? surface : text,
        ),
        todayBackgroundColor: WidgetStateProperty.resolveWith(
          (states) =>
              states.contains(WidgetState.selected) ? text : Colors.transparent,
        ),
        todayBorder: BorderSide(color: text),
        yearForegroundColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected) ? surface : text,
        ),
        yearBackgroundColor: WidgetStateProperty.resolveWith(
          (states) =>
              states.contains(WidgetState.selected) ? text : Colors.transparent,
        ),
        cancelButtonStyle: TextButton.styleFrom(foregroundColor: text),
        confirmButtonStyle: TextButton.styleFrom(foregroundColor: text),
      ),
      timePickerTheme: TimePickerThemeData(
        backgroundColor: surface,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(28),
        ),
        dialBackgroundColor: inputFill,
        dialHandColor: text,
        dialTextColor: WidgetStateColor.resolveWith(
          (states) => states.contains(WidgetState.selected) ? surface : text,
        ),
        hourMinuteColor: WidgetStateColor.resolveWith(
          (states) => states.contains(WidgetState.selected) ? text : inputFill,
        ),
        hourMinuteTextColor: WidgetStateColor.resolveWith(
          (states) => states.contains(WidgetState.selected) ? surface : text,
        ),
        dayPeriodColor: WidgetStateColor.resolveWith(
          (states) =>
              states.contains(WidgetState.selected) ? text : Colors.transparent,
        ),
        dayPeriodTextColor: WidgetStateColor.resolveWith(
          (states) => states.contains(WidgetState.selected) ? surface : text,
        ),
        cancelButtonStyle: TextButton.styleFrom(foregroundColor: text),
        confirmButtonStyle: TextButton.styleFrom(foregroundColor: text),
      ),
    );
  }

  static TextTheme _textTheme({required Color text, required Color muted}) {
    return TextTheme(
      displayLarge: TextStyle(
        fontSize: 32,
        fontWeight: FontWeight.w800,
        color: text,
        height: 1.1,
        letterSpacing: -1,
      ),
      displayMedium: TextStyle(
        fontSize: 28,
        fontWeight: FontWeight.w800,
        color: text,
        height: 1.1,
        letterSpacing: -1,
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
