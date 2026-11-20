import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/trip_capacity.dart';

/// Kilograms at the edge of the listing forms and cards (design canvas
/// page M): a person types and reads kilograms, while a listing's quantity
/// is stored and sent in quintals (crop-listing-api.md: `quantity`, 0.01 to
/// 10,000 quintals). Nothing on the wire changes: the numbers are converted
/// here, where they are shown or typed.

final NumberFormat _kgNumber = NumberFormat('#,##0.##', 'en_US');

/// "3,200" for 3200 kg: grouped thousands, up to two decimals, no unit.
String kgNumberText(num kg) => _kgNumber.format(kg);

/// The smallest and the largest listing a farmer can type, in kg: the
/// server's 0.01 and 10,000 quintals.
const double minListingKg = 1;
const double maxListingKg = 1000000;

/// The kilograms of [quintals], free of float dust (12.3456 quintals is
/// 1,234.56 kg, never 1,234.5600000001).
double kgFromQuintals(double quintals) =>
    (quintals * kgPerQuintal * 100).roundToDouble() / 100;

/// The quintals of [kg] as the server stores them: four decimals at most
/// (the stock can carry four after a funding, crop-listing-api.md rule 10).
double quintalsFromKg(double kg) =>
    (kg / kgPerQuintal * 10000).roundToDouble() / 10000;

/// The kilograms typed into a quantity field, or null when [text] is not a
/// number. Grouping commas and spaces are ignored ("4,000").
double? parseKgEntry(String? text) {
  if (text == null) return null;
  final plain = text.replaceAll(RegExp(r'[,\s]'), '');
  if (plain.isEmpty) return null;
  final kg = double.tryParse(plain);
  return kg == null || !kg.isFinite ? null : kg;
}

/// What a quantity field shows for [quintals]: plain kilograms, no
/// grouping, no trailing zeros, two decimals at most ("4000", "1234.56").
String kgEntryText(double quintals) {
  final fixed = kgFromQuintals(quintals).toStringAsFixed(2);
  return fixed.replaceFirst(RegExp(r'\.?0+$'), '');
}

/// Why typed kilograms cannot be saved, for the form to put into words.
enum KgEntryProblem { missing, tooSmall, tooLarge }

/// What is wrong with the typed [text], or null when it can be saved: a
/// number from [minListingKg] to [maxListingKg].
KgEntryProblem? kgEntryProblem(String? text) {
  final kg = parseKgEntry(text);
  if (kg == null || kg <= 0) return KgEntryProblem.missing;
  if (kg < minListingKg) return KgEntryProblem.tooSmall;
  if (kg > maxListingKg) return KgEntryProblem.tooLarge;
  return null;
}
