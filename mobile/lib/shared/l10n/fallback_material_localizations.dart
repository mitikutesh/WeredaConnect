import 'package:flutter/cupertino.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/intl.dart' as intl;

import 'package:wereda_connect/core/utils/date_locale.dart';

/// Material localizations for the app's languages: Flutter's own for
/// English and Amharic (the back button, date picker, "OK" and "Cancel"
/// in Amharic), and [MaterialLocalizationOm] for Afaan Oromoo, which
/// Flutter does not ship.
///
/// Loading them also sets the date locale ([applyDateLocale]), so dates
/// follow the app language from the first frame after a language change.
class FallbackMaterialLocalizationsDelegate
    extends LocalizationsDelegate<MaterialLocalizations> {
  const FallbackMaterialLocalizationsDelegate();

  @override
  bool isSupported(Locale locale) {
    // Support all locales - we'll provide fallback for unsupported ones
    return true;
  }

  @override
  Future<MaterialLocalizations> load(Locale locale) async {
    final MaterialLocalizations localizations;
    if (GlobalMaterialLocalizations.delegate.isSupported(locale)) {
      localizations = await GlobalMaterialLocalizations.delegate.load(locale);
    } else {
      // No Flutter translation. The English load brings intl's date data,
      // which the Afaan Oromoo names are added to.
      final english = await GlobalMaterialLocalizations.delegate.load(
        const Locale('en'),
      );
      registerOromoDateData();
      localizations =
          locale.languageCode == 'om' && intl.DateFormat.localeExists('om')
              ? MaterialLocalizationOm()
              : english;
    }
    // After the load above, which brings intl's date data with it.
    applyDateLocale(locale);
    return SynchronousFuture<MaterialLocalizations>(localizations);
  }

  @override
  bool shouldReload(FallbackMaterialLocalizationsDelegate old) => false;
}

/// Afaan Oromoo Material strings (BUG-L4): what the date picker, the back
/// button and the text selection menu show. Month and weekday names come
/// from the `om` date data ([registerOromoDateData], which must run first);
/// every label not overridden here stays English.
class MaterialLocalizationOm extends MaterialLocalizationEn {
  MaterialLocalizationOm()
      : super(
          localeName: 'om',
          fullYearFormat: intl.DateFormat.y('om'),
          compactDateFormat: intl.DateFormat.yMd('om'),
          shortDateFormat: intl.DateFormat.yMMMd('om'),
          mediumDateFormat: intl.DateFormat.MMMEd('om'),
          longDateFormat: intl.DateFormat.yMMMMEEEEd('om'),
          yearMonthFormat: intl.DateFormat.yMMMM('om'),
          shortMonthDayFormat: intl.DateFormat.MMMd('om'),
          decimalFormat: intl.NumberFormat.decimalPattern('om'),
          twoDigitZeroPaddedFormat: intl.NumberFormat('00', 'om'),
        );

  @override
  String get okButtonLabel => 'Tole';

  @override
  String get cancelButtonLabel => 'Dhiisi';

  @override
  String get backButtonTooltip => "Deebi'i";

  @override
  String get closeButtonLabel => 'Cufi';

  @override
  String get closeButtonTooltip => 'Cufi';

  @override
  String get continueButtonLabel => 'Itti fufi';

  @override
  String get saveButtonLabel => "Olkaa'i";

  @override
  String get deleteButtonTooltip => 'Haqi';

  @override
  String get copyButtonLabel => 'Garagalchi';

  @override
  String get cutButtonLabel => 'Muri';

  @override
  String get pasteButtonLabel => 'Maxxansi';

  @override
  String get selectAllButtonLabel => 'Hunda filadhu';

  @override
  String get datePickerHelpText => 'Guyyaa filadhu';

  @override
  String get dateInputLabel => 'Guyyaa galchi';

  @override
  String get currentDateLabel => "Har'a";

  @override
  String get selectedDateLabel => 'Filatame';

  @override
  String get unspecifiedDate => 'Guyyaa';

  @override
  String get nextMonthTooltip => "Ji'a itti aanu";

  @override
  String get previousMonthTooltip => "Ji'a darbe";

  @override
  String get selectYearSemanticsLabel => 'Waggaa filadhu';

  @override
  String get calendarModeButtonLabel => 'Gara kaalaandarii';

  @override
  String get inputDateModeButtonLabel => 'Guyyaa barreessi';

  @override
  String get invalidDateFormatLabel => 'Bifti guyyaa sirrii miti.';

  @override
  String get dateOutOfRangeLabel => 'Guyyaan kun hin filatamu.';
}

/// Cupertino localizations: Flutter's own for English and Amharic, English
/// for Afaan Oromoo.
class FallbackCupertinoLocalizationsDelegate
    extends LocalizationsDelegate<CupertinoLocalizations> {
  const FallbackCupertinoLocalizationsDelegate();

  @override
  bool isSupported(Locale locale) => true;

  @override
  Future<CupertinoLocalizations> load(Locale locale) async {
    if (GlobalCupertinoLocalizations.delegate.isSupported(locale)) {
      return GlobalCupertinoLocalizations.delegate.load(locale);
    }
    // Afaan Oromoo (om): no Flutter translation, English labels.
    return SynchronousFuture<CupertinoLocalizations>(
      await GlobalCupertinoLocalizations.delegate.load(const Locale('en')),
    );
  }

  @override
  bool shouldReload(FallbackCupertinoLocalizationsDelegate old) => false;
}
