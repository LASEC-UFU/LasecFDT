import 'package:flutter/material.dart';

/// Tema do LasecFDT, com a mesma identidade visual do Gestor-UFU (paleta roxo/ciano, cartões arredondados,
/// escuro por padrão). Diferenças deliberadas para a planta:
///  - fonte do sistema (Segoe UI): nada é baixado da internet em tempo de execução;
///  - "efeitos reduzidos" (padrão em sessão remota/thin client): fundo liso em vez de gradiente e sem animações,
///    o que diminui CPU no servidor e banda do protocolo de área de trabalho remota.
abstract class AppTheme {
  static bool isLight = false;
  static bool reducedEffects = false;

  static const fontFamily = 'Segoe UI';

  static const _darkBg = Color(0xFF0F1123);
  static const _darkSurface = Color(0xFF171932);
  static const _darkCard = Color(0xFF1E2045);
  static const _darkCardAlt = Color(0xFF151730);
  static const _darkPrimary = Color(0xFF6C63FF);
  static const _darkPrimaryLight = Color(0xFF9D97FF);
  static const _darkAccent = Color(0xFF00D4FF);
  static const _darkGreen = Color(0xFF00E676);
  static const _darkDanger = Color(0xFFFF5252);
  static const _darkWarning = Color(0xFFFFAB00);
  static const _darkText = Color(0xFFFFFFFF);
  static const _darkTextSecondary = Color(0xFF9EA2C4);
  static const _darkDivider = Color(0xFF2A2D52);

  static const _lightBg = Color(0xFFF4F6FC);
  static const _lightSurface = Color(0xFFFFFFFF);
  static const _lightCard = Color(0xFFFFFFFF);
  static const _lightCardAlt = Color(0xFFEEF0F8);
  static const _lightPrimary = Color(0xFF584DE0);
  static const _lightAccent = Color(0xFF007E9E);
  static const _lightGreen = Color(0xFF087A49);
  static const _lightDanger = Color(0xFFC62828);
  static const _lightWarning = Color(0xFFB26A00);
  static const _lightText = Color(0xFF1D2033);
  static const _lightTextSecondary = Color(0xFF5E6478);
  static const _lightDivider = Color(0xFFD9DEEB);

  static Color get bg => isLight ? _lightBg : _darkBg;
  static Color get surface => isLight ? _lightSurface : _darkSurface;
  static Color get card => isLight ? _lightCard : _darkCard;
  static Color get cardAlt => isLight ? _lightCardAlt : _darkCardAlt;
  static Color get primary => isLight ? _lightPrimary : _darkPrimary;
  static const Color primaryLight = _darkPrimaryLight;
  static Color get accent => isLight ? _lightAccent : _darkAccent;
  static Color get green => isLight ? _lightGreen : _darkGreen;
  static Color get danger => isLight ? _lightDanger : _darkDanger;
  static Color get warning => isLight ? _lightWarning : _darkWarning;
  static Color get text => isLight ? _lightText : _darkText;
  static Color get textSecondary => isLight ? _lightTextSecondary : _darkTextSecondary;
  static Color get divider => isLight ? _lightDivider : _darkDivider;

  static const LinearGradient primaryGradient = LinearGradient(colors: [_darkPrimary, Color(0xFF3D5AFE)]);

  /// Fundo da aplicação: gradiente como no Gestor-UFU, ou liso com efeitos reduzidos.
  static BoxDecoration get background => reducedEffects
      ? BoxDecoration(color: bg)
      : BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topLeft,
            end: Alignment.bottomRight,
            colors: isLight ? const [Color(0xFFF4F6FC), Color(0xFFEDEBFA)] : const [Color(0xFF0F1123), Color(0xFF1A0A33)],
          ),
        );

  static BoxDecoration cardDecoration({Color? border, double radius = 16}) => BoxDecoration(
        color: reducedEffects ? card : null,
        gradient: reducedEffects
            ? null
            : LinearGradient(
                begin: Alignment.topLeft,
                end: Alignment.bottomRight,
                colors: isLight ? const [Color(0xFFFFFFFF), Color(0xFFF5F6FB)] : const [Color(0xFF1E2045), Color(0xFF16183A)],
              ),
        borderRadius: BorderRadius.circular(radius),
        border: Border.all(color: border ?? divider),
      );

  static ThemeData build({required bool light}) {
    final base = light ? ThemeData.light(useMaterial3: true) : ThemeData.dark(useMaterial3: true);
    final primary = light ? _lightPrimary : _darkPrimary;
    final text = light ? _lightText : _darkText;
    final secondaryText = light ? _lightTextSecondary : _darkTextSecondary;
    final divider = light ? _lightDivider : _darkDivider;
    final cardAlt = light ? _lightCardAlt : _darkCardAlt;
    final surface = light ? _lightSurface : _darkSurface;
    return base.copyWith(
      scaffoldBackgroundColor: light ? _lightBg : _darkBg,
      colorScheme: (light ? const ColorScheme.light() : const ColorScheme.dark()).copyWith(
        primary: primary,
        secondary: light ? _lightAccent : _darkAccent,
        surface: light ? _lightCard : _darkCard,
        error: light ? _lightDanger : _darkDanger,
        onPrimary: Colors.white,
      ),
      textTheme: base.textTheme.apply(fontFamily: fontFamily, bodyColor: text, displayColor: text),
      primaryTextTheme: base.primaryTextTheme.apply(fontFamily: fontFamily),
      cardTheme: CardThemeData(
        color: light ? _lightCard : _darkCard,
        elevation: 0,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16), side: BorderSide(color: divider)),
      ),
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          backgroundColor: primary,
          foregroundColor: Colors.white,
          minimumSize: const Size(0, 42),
          padding: const EdgeInsets.symmetric(horizontal: 18),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
          textStyle: const TextStyle(fontFamily: fontFamily, fontSize: 14, fontWeight: FontWeight.w600),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          foregroundColor: primary,
          side: BorderSide(color: primary, width: 1.5),
          minimumSize: const Size(0, 42),
          padding: const EdgeInsets.symmetric(horizontal: 16),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
          textStyle: const TextStyle(fontFamily: fontFamily, fontSize: 14, fontWeight: FontWeight.w600),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: primary,
          textStyle: const TextStyle(fontFamily: fontFamily, fontWeight: FontWeight.w600),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: cardAlt,
        isDense: true,
        contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        border: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide(color: divider)),
        enabledBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide(color: light ? divider : primary.withAlpha(80))),
        focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide(color: primary, width: 2)),
        labelStyle: TextStyle(color: secondaryText),
        hintStyle: TextStyle(color: secondaryText),
        prefixIconColor: secondaryText,
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: surface,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(20)),
      ),
      popupMenuTheme: PopupMenuThemeData(
        color: surface,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12), side: BorderSide(color: divider)),
        textStyle: TextStyle(fontFamily: fontFamily, color: text, fontSize: 14),
      ),
      snackBarTheme: SnackBarThemeData(
        backgroundColor: light ? _lightText : _darkCard,
        contentTextStyle: const TextStyle(fontFamily: fontFamily, color: Colors.white),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
        behavior: SnackBarBehavior.floating,
      ),
      dividerTheme: DividerThemeData(color: divider, thickness: 1, space: 1),
      chipTheme: ChipThemeData(
        backgroundColor: cardAlt,
        selectedColor: primary.withAlpha(light ? 35 : 50),
        labelStyle: TextStyle(fontFamily: fontFamily, fontSize: 12, color: text),
        side: BorderSide(color: divider),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      ),
      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(color: light ? _lightText : _darkCard, borderRadius: BorderRadius.circular(8), border: Border.all(color: divider)),
        textStyle: const TextStyle(fontFamily: fontFamily, color: Colors.white, fontSize: 12),
        waitDuration: const Duration(milliseconds: 500),
      ),
      scrollbarTheme: ScrollbarThemeData(
        thumbColor: WidgetStatePropertyAll(secondaryText.withAlpha(90)),
        radius: const Radius.circular(8),
      ),
      pageTransitionsTheme: const PageTransitionsTheme(builders: {
        TargetPlatform.windows: FadeUpwardsPageTransitionsBuilder(),
      }),
    );
  }
}
