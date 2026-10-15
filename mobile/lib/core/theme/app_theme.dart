import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

/// The design system.
///
/// Everything visual is defined here as a token, so a screen never picks a raw colour, radius or
/// spacing of its own. Two rules drive the palette:
///
///  * **Calm by default, urgent on purpose.** A normal auction is quiet and trustworthy. Only a
///    live auction earns [live], and only the closing moments earn [finalPhase]. If those colours
///    appeared anywhere else they would stop meaning anything.
///  * **State is never colour alone.** Every status carries an icon and a label too, so the screen
///    still reads correctly to someone who cannot separate red from green.
class AppTheme {
  const AppTheme._();

  // ---------------------------------------------------------------- brand

  /*
   * The palette is the admin console's, value for value, so the phone and the dashboard are
   * recognisably one product. Where the console has a light-only token the dark theme takes the
   * step that survives a near-black surface; nothing here is a second opinion about the colour.
   */

  /// DealGo orange, straight from the logo. Bright: it carries shapes and fills, not small text.
  /// The font that draws the Saudi Riyal sign (U+E900). Any text style that does not inherit
  /// from the theme must list it, or the sign has no font and shows as a box.
  static const riyalFallback = ['RiyalSign'];

  static const brand = Color(0xFFF26A1B);
  static const brandSoft = Color(0xFFFF8A3D);

  /// The same orange, darkened until white text on it is legible. Buttons and filled chips.
  static const accent = Color(0xFFC64D09);

  /// Darker again, for the brand used as *text* on a light ground.
  static const accentText = Color(0xFFA63F06);

  /// The two orange washes: a tinted panel, and the border that frames it.
  static const accentSoft = Color(0xFFFFF1E6);
  static const accentTint = Color(0xFFFFE0C6);

  /// The other half of the logo. Used where the brand needs weight rather than colour.
  static const ink = Color(0xFF1C1F24);
  static const inkSoft = Color(0xFFF1F2F4);

  /// A live auction. Warm, moving, but not yet an alarm.
  // A live auction is the brand at work, so it carries the brand colour rather than a second
  // orange a shade away from it.
  static const live = brand;

  /*
   * The closing phase and an error are the same red.
   *
   * They were two reds a few units apart, which is not a distinction anyone can see; it only made
   * the app harder to keep consistent. One red is enough because state is never colour alone here
   * — the closing phase also carries its own icon, its label and a countdown.
   */
  static const danger = Color(0xFFDD3341);
  static const finalPhase = danger;

  /// Feedback colours, for outcomes rather than for auction state.
  static const success = Color(0xFF0EA15E);
  static const warning = Color(0xFFB38609);
  static const info = Color(0xFF2F6FD0);

  /*
   * Prizes, winnings and shopping credit — the "you gained something" colour.
   *
   * Amber rather than the orange the console uses for shopping credit: on the phone orange now
   * belongs to DealGo itself and to a live auction, so a reward painted in it would read as
   * branding rather than as something won.
   */
  static const gold = warning;

  /// Text and icons drawn on a solid [gold] fill. White reaches only 3.3 there; this reaches 5.
  static const onGold = Color(0xFF2A1D00);

  /*
   * The darker step of a colour, for when that colour has to carry text.
   *
   * A pill paints its label in its own accent on a pale wash of the same accent. The bright step
   * that fills an icon tile reaches only about 3:1 against that wash, which is fine for a shape
   * and not enough for a word. Each meaning therefore has a second, darker step, exactly as the
   * admin console does: the two read as the same colour and only the contrast changes.
   *
   * On a dark surface the relationship inverts — the bright step is already the legible one —
   * so the colour is returned untouched there.
   */
  static Color readable(Color c, Brightness b) =>
      b == Brightness.dark ? c : (_textStep[c.toARGB32()] ?? c);

  // Keyed by the bright step. Colours that are aliases of each other (live is brand, gold is
  // warning, finalPhase is danger) share one entry, because they are one value.
  static const _textStep = <int, Color>{
    0xFFF26A1B: accentText, // brand, and a live auction
    0xFFC64D09: accentText, // accent
    0xFF0EA15E: Color(0xFF06713F), // success
    0xFFB38609: Color(0xFF7A5A05), // warning, and gold
    0xFFDD3341: Color(0xFFA01823), // danger, and the closing phase
    0xFF2F6FD0: Color(0xFF14508F), // info
  };

  // Names the screens were already written against.
  static const seed = brand;
  static const urgent = live;
  static const urgentDeep = finalPhase;

  // ---------------------------------------------------------------- surfaces

  // The console's own surfaces. They were a blue-grey set, which fought the orange.
  static const _lightBg = Color(0xFFF7F8FC);
  static const _lightSurface = Color(0xFFFFFFFF);
  static const _lightSurfaceMuted = Color(0xFFF2F4F9);
  static const _lightBorder = Color(0xFFE2E8F0);
  static const _lightText = Color(0xFF17191D);
  static const _lightMuted = Color(0xFF5C626C);

  // The console is light only, so the dark set is built from the logo's near-black rather than
  // from a navy. Same neutral family, turned inside out.
  static const _darkBg = Color(0xFF0E1013);
  static const _darkSurface = Color(0xFF17191D);
  static const _darkSurfaceMuted = Color(0xFF212429);
  static const _darkBorder = Color(0xFF2D3137);
  static const _darkText = Color(0xFFF3F4F6);
  static const _darkMuted = Color(0xFF9BA1AA);

  // ---------------------------------------------------------------- scale

  static const space1 = 4.0;
  static const space2 = 8.0;
  static const space3 = 12.0;
  static const space4 = 16.0;
  static const space5 = 20.0;
  static const space6 = 24.0;
  static const space8 = 32.0;

  /*
   * Tighter than before, to match the console's crisp panels. Still a step rounder than the web,
   * because a phone card is held in the hand rather than read across a desk.
   */
  static const radiusSm = 8.0;
  static const radiusMd = 12.0;
  static const radiusLg = 16.0;
  static const radiusXl = 22.0;
  static const radiusPill = 999.0;

  /// Minimum comfortable tap target. Anything the user taps during a live auction uses it.
  static const tapTarget = 48.0;

  /// Money, countdowns and ranks. Tabular figures stop the number jittering as it changes —
  /// a price that ticks upward every second must not make the layout twitch.
  static const numeric = <FontFeature>[FontFeature.tabularFigures()];

  /// A soft lift for cards. Deliberately weak: many small shadows read as noise.
  static List<BoxShadow> shadow(Brightness b) => b == Brightness.dark
      ? const [
          BoxShadow(
            color: Color(0x40000000),
            blurRadius: 18,
            offset: Offset(0, 6),
          ),
        ]
      : const [
          BoxShadow(
            color: Color(0x120D1424),
            blurRadius: 18,
            offset: Offset(0, 6),
          ),
        ];

  /// The accent that belongs to an auction in this state.
  static Color statusColor(
    Brightness b, {
    required bool isLive,
    required bool isFinal,
  }) {
    if (isFinal) return finalPhase;
    if (isLive) return live;
    return b == Brightness.dark ? brandSoft : brand;
  }

  static ThemeData light() => _build(Brightness.light);
  static ThemeData dark() => _build(Brightness.dark);

  static ThemeData _build(Brightness brightness) {
    final isDark = brightness == Brightness.dark;
    final bg = isDark ? _darkBg : _lightBg;
    final surface = isDark ? _darkSurface : _lightSurface;
    final surfaceMuted = isDark ? _darkSurfaceMuted : _lightSurfaceMuted;
    final border = isDark ? _darkBorder : _lightBorder;
    final text = isDark ? _darkText : _lightText;
    final muted = isDark ? _darkMuted : _lightMuted;
    // Light uses the darkened step, because this colour ends up behind white button text.
    final primary = isDark ? brandSoft : accent;
    final scheme = ColorScheme(
      brightness: brightness,
      primary: primary,
      onPrimary: isDark ? ink : Colors.white,
      // The console's own orange wash, and its orange-as-text step on top of it.
      primaryContainer: isDark ? const Color(0xFF5A2A08) : accentSoft,
      onPrimaryContainer: isDark ? accentTint : accentText,
      secondary: gold,
      onSecondary: onGold,
      secondaryContainer: isDark ? const Color(0xFF3A2C08) : const Color(0xFFFDF3D3),
      onSecondaryContainer: isDark ? const Color(0xFFFAE3B4) : const Color(0xFF7A5A05),
      error: danger,
      onError: Colors.white,
      errorContainer: isDark ? const Color(0xFF4B1417) : const Color(0xFFFDEDEE),
      onErrorContainer: isDark ? const Color(0xFFFFD5D7) : const Color(0xFFA01823),
      surface: surface,
      onSurface: text,
      surfaceContainerLowest: bg,
      surfaceContainerLow: isDark ? const Color(0xFF121417) : const Color(0xFFFAFBFD),
      surfaceContainer: surface,
      surfaceContainerHigh: surfaceMuted,
      surfaceContainerHighest: surfaceMuted,
      onSurfaceVariant: muted,
      outline: border,
      outlineVariant: border,
      shadow: Colors.black,
      scrim: Colors.black,
      inverseSurface: isDark ? _lightSurface : ink,
      onInverseSurface: isDark ? _lightText : _darkText,
      inversePrimary: isDark ? brand : brandSoft,
    );
    final base = ThemeData(
      useMaterial3: true,
      colorScheme: scheme,
      brightness: brightness,
      /*
       * Fonts to consult for a character the phone's own font does not have.
       *
       * In practice that is one character: the Saudi Riyal sign, which is too new to be in system
       * fonts. It is written as ordinary text (U+E900) and drawn from the small font shipped with
       * the app, so prices stay selectable and styled like every other word on the screen.
       */
      fontFamilyFallback: riyalFallback,
    );
    return base.copyWith(
      scaffoldBackgroundColor: bg,
      canvasColor: bg,
      splashFactory: InkSparkle.splashFactory,
      textTheme: _text(base.textTheme, text, muted),
      appBarTheme: AppBarTheme(
        // The console puts its mark at the start of the bar, so the app does the same rather
        // than centring a title under it.
        centerTitle: false,
        elevation: 0,
        scrolledUnderElevation: 0,
        // In light mode the bar is pure white, and so is the status bar above it (time, battery):
        // the page grey behind it read as a dull band across the top of every screen. A hairline
        // keeps the white bar apart from the page below it.
        backgroundColor: isDark ? bg : surface,
        shape: isDark ? null : Border(bottom: BorderSide(color: border)),
        surfaceTintColor: Colors.transparent,
        foregroundColor: text,
        titleTextStyle: TextStyle(
          color: text,
          fontSize: 17,
          fontWeight: FontWeight.w700,
          letterSpacing: -0.2,
        ),
        systemOverlayStyle: isDark
            ? SystemUiOverlayStyle.light
            : SystemUiOverlayStyle.dark.copyWith(
                statusBarColor: Colors.white,
                statusBarIconBrightness: Brightness.dark,
                statusBarBrightness: Brightness.light,
              ),
      ),
      cardTheme: CardThemeData(
        elevation: 0,
        margin: EdgeInsets.zero,
        color: surface,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(radiusLg),
          side: BorderSide(color: border),
        ),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          minimumSize: const Size.fromHeight(tapTarget),
          padding: const EdgeInsets.symmetric(horizontal: space5),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(radiusMd)),
          // A button's own text style replaces the theme's, fallback font included, so the
          // Riyal sign is listed again here or every price on a button shows a broken box.
          textStyle: const TextStyle(
            fontSize: 15.5,
            fontWeight: FontWeight.w700,
            letterSpacing: 0.1,
            fontFamilyFallback: riyalFallback,
          ),
          disabledBackgroundColor: surfaceMuted,
          disabledForegroundColor: muted,
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          minimumSize: const Size.fromHeight(tapTarget - 4),
          padding: const EdgeInsets.symmetric(horizontal: space5),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(radiusMd),
          ),
          side: BorderSide(color: border),
          foregroundColor: text,
          textStyle: const TextStyle(
            fontSize: 15,
            fontWeight: FontWeight.w600,
            fontFamilyFallback: riyalFallback,
          ),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: primary,
          textStyle: const TextStyle(
            fontSize: 14.5,
            fontWeight: FontWeight.w600,
            fontFamilyFallback: riyalFallback,
          ),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: surfaceMuted,
        hintStyle: TextStyle(color: muted, fontWeight: FontWeight.w400),
        labelStyle: TextStyle(color: muted),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: space4,
          vertical: space4,
        ),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(radiusMd),
          borderSide: BorderSide.none,
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(radiusMd),
          borderSide: BorderSide(color: border),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(radiusMd),
          borderSide: BorderSide(color: primary, width: 1.6),
        ),
        errorBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(radiusMd),
          borderSide: const BorderSide(color: danger),
        ),
        focusedErrorBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(radiusMd),
          borderSide: const BorderSide(color: danger, width: 1.6),
        ),
      ),
      chipTheme: ChipThemeData(
        backgroundColor: surfaceMuted,
        // A thin frame, as on the console: a chip on a white card is otherwise only a tint.
        side: BorderSide(color: border),
        labelStyle: TextStyle(
          fontSize: 12.5,
          fontWeight: FontWeight.w600,
          color: text,
        ),
        padding: const EdgeInsets.symmetric(
          horizontal: space3,
          vertical: space1,
        ),
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.all(Radius.circular(radiusPill)),
        ),
      ),
      dividerTheme: DividerThemeData(color: border, space: 1, thickness: 1),
      navigationBarTheme: NavigationBarThemeData(
        height: 66,
        elevation: 0,
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        indicatorColor: primary.withValues(alpha: isDark ? 0.22 : 0.12),
        indicatorShape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.all(Radius.circular(radiusPill)),
        ),
        labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
        labelTextStyle: WidgetStateProperty.resolveWith(
          (states) => TextStyle(
            fontSize: 11.5,
            fontWeight: states.contains(WidgetState.selected) ? FontWeight.w700 : FontWeight.w500,
            color: states.contains(WidgetState.selected) ? primary : muted,
          ),
        ),
        iconTheme: WidgetStateProperty.resolveWith(
          (states) => IconThemeData(
            size: 23,
            color: states.contains(WidgetState.selected) ? primary : muted,
          ),
        ),
      ),
      tabBarTheme: TabBarThemeData(
        labelColor: primary,
        unselectedLabelColor: muted,
        dividerColor: border,
        indicatorSize: TabBarIndicatorSize.label,
        labelStyle: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700),
        unselectedLabelStyle: const TextStyle(
          fontSize: 14,
          fontWeight: FontWeight.w500,
        ),
        indicator: UnderlineTabIndicator(
          borderSide: BorderSide(color: primary, width: 2.5),
          insets: const EdgeInsets.symmetric(horizontal: space2),
        ),
      ),
      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        insetPadding: const EdgeInsets.all(space4),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(radiusMd),
        ),
        backgroundColor: isDark ? _lightSurface : ink,
        contentTextStyle: TextStyle(
          color: isDark ? _lightText : Colors.white,
          fontSize: 14,
          fontWeight: FontWeight.w500,
        ),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(radiusXl),
        ),
        titleTextStyle: TextStyle(
          color: text,
          fontSize: 18,
          fontWeight: FontWeight.w700,
          letterSpacing: -0.2,
        ),
      ),
      bottomSheetTheme: BottomSheetThemeData(
        backgroundColor: surface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        showDragHandle: true,
        dragHandleColor: border,
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(radiusXl)),
        ),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(
        color: primary,
        linearTrackColor: surfaceMuted,
        circularTrackColor: Colors.transparent,
        linearMinHeight: 8,
      ),
      listTileTheme: ListTileThemeData(
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(radiusMd)),
        iconColor: muted,
        contentPadding: const EdgeInsets.symmetric(
          horizontal: space4,
          vertical: space1,
        ),
      ),
      badgeTheme: const BadgeThemeData(
        backgroundColor: danger,
        textColor: Colors.white,
      ),
      splashColor: primary.withValues(alpha: 0.08),
      highlightColor: primary.withValues(alpha: 0.05),
    );
  }

  /// Tight, confident headings; calm body text; figures that never shift width.
  static TextTheme _text(TextTheme base, Color text, Color muted) => base.copyWith(
        displayLarge: base.displayLarge?.copyWith(
          color: text,
          fontWeight: FontWeight.w800,
          letterSpacing: -1.5,
          fontFeatures: numeric,
        ),
        displayMedium: base.displayMedium?.copyWith(
          color: text,
          fontWeight: FontWeight.w800,
          letterSpacing: -1.2,
          fontFeatures: numeric,
        ),
        displaySmall: base.displaySmall?.copyWith(
          color: text,
          fontWeight: FontWeight.w800,
          letterSpacing: -1,
          fontFeatures: numeric,
        ),
        headlineLarge: base.headlineLarge?.copyWith(
          color: text,
          fontWeight: FontWeight.w700,
          letterSpacing: -0.6,
        ),
        headlineMedium: base.headlineMedium?.copyWith(
          color: text,
          fontWeight: FontWeight.w700,
          letterSpacing: -0.5,
          fontFeatures: numeric,
        ),
        headlineSmall: base.headlineSmall?.copyWith(
          color: text,
          fontWeight: FontWeight.w700,
          letterSpacing: -0.4,
        ),
        titleLarge: base.titleLarge?.copyWith(
          color: text,
          fontWeight: FontWeight.w700,
          letterSpacing: -0.3,
        ),
        titleMedium: base.titleMedium?.copyWith(
          color: text,
          fontWeight: FontWeight.w600,
          letterSpacing: -0.1,
        ),
        titleSmall: base.titleSmall?.copyWith(
          color: text,
          fontWeight: FontWeight.w600,
        ),
        bodyLarge: base.bodyLarge?.copyWith(color: text, height: 1.45),
        bodyMedium: base.bodyMedium?.copyWith(color: text, height: 1.45),
        bodySmall: base.bodySmall?.copyWith(
          color: muted,
          height: 1.4,
          fontSize: 12.5,
        ),
        labelLarge: base.labelLarge?.copyWith(
          color: text,
          fontWeight: FontWeight.w600,
        ),
        labelMedium: base.labelMedium?.copyWith(
          color: muted,
          fontWeight: FontWeight.w600,
        ),
        labelSmall: base.labelSmall?.copyWith(
          color: muted,
          fontWeight: FontWeight.w500,
        ),
      );
}
