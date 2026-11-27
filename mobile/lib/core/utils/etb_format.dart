import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Birr the way the Fieldway boards write it: grouped thousands and no
/// decimals unless the amount has some ("68,400", "1,234.56"). Pair it with
/// `l10n.etbCurrency` for the unit.
String formatEtbAmount(num amount) {
  final whole = amount == amount.roundToDouble();
  return (whole ? _wholeFormat : _centsFormat).format(amount);
}

final NumberFormat _wholeFormat = NumberFormat('#,##0', 'en_US');
final NumberFormat _centsFormat = NumberFormat('#,##0.00', 'en_US');
final NumberFormat _kgFormat = NumberFormat('#,##0.##', 'en_US');

/// A whole quantity with grouped thousands ("1,500" kg).
String wholeNumber(num value) => _wholeFormat.format(value);

/// What a list shows in place of an amount, a quantity or a name that
/// cannot be real (SIM-33): a dash, the mark the home tiles show for a
/// count that failed. Never a 0, a negative sum or an id.
const noValueMark = '-';

/// The largest order the server takes, in kg (`MaxOrderKg` in
/// SyncFunction.cs: 10,000 quintals).
const maxOrderKg = 1000000;

/// Whether [amount] can be a real birr amount: finite and not below zero.
bool isRealEtbAmount(num amount) => amount.isFinite && amount >= 0;

/// Whether [kg] can be a real order quantity: finite, above zero and no
/// more than [maxOrderKg].
bool isRealKgQuantity(num kg) => kg.isFinite && kg > 0 && kg <= maxOrderKg;

/// "68,400 ETB" for a real amount ([isRealEtbAmount]), else [noValueMark].
String etbAmountText(num amount, AppLocalizations l10n) =>
    isRealEtbAmount(amount)
        ? l10n.etbCurrency(formatEtbAmount(amount))
        : noValueMark;

/// "1,500 kg" for a real order quantity ([isRealKgQuantity]), else
/// [noValueMark]. A part of a kilo keeps its decimals ("0.5 kg"), so a
/// real order never reads "0 kg".
String kgQuantityText(num kg, AppLocalizations l10n) =>
    isRealKgQuantity(kg) ? l10n.quantityKg(_kgFormat.format(kg)) : noValueMark;

/// "(28 quintals)" for [kg] kilograms: the boards lead with kilograms and
/// put the quintals after them in parentheses, smaller and muted (design
/// canvas page M). Part of a quintal keeps up to two decimals ("(0.5
/// quintals)"). Empty for a quantity that cannot be real
/// ([isRealKgQuantity]), so nothing trails a [noValueMark].
String quintalsInParensText(num kg, AppLocalizations l10n) =>
    isRealKgQuantity(kg) ? quintalsCountInParens(kg / kgPerQuintal, l10n) : '';

/// A quintal amount beside its kilograms: grouped thousands and up to two
/// decimals ("1.25", "2,500"), so 125 kg never reads "1.3 quintals". The
/// one format for every quintal figure that follows a kilogram figure.
String quintalsNumberText(num quintals) => _kgFormat.format(quintals);

/// "(28 quintals)" for [quintals] as stored (a listing's stock, a trip's
/// capacity), "(1 quintal)" for exactly one and "(0 quintals)" for none.
String quintalsCountInParens(num quintals, AppLocalizations l10n) =>
    quintals == 1
        ? l10n.oneQuintalInParens
        : l10n.quintalsInParens(quintalsNumberText(quintals));

/// "(12.5 of 50 quintals)" for stored quintal amounts, in the format of
/// [quintalsNumberText].
String quintalsOfInParensText(num part, num total, AppLocalizations l10n) =>
    l10n.quintalsOfInParens(
      quintalsNumberText(part),
      quintalsNumberText(total),
    );

/// The room left on a trip, in kilograms: "1,500 kg", and "0 kg" for a
/// full trip (never the [noValueMark]: no room is a real answer).
String kgRoomText(num kg, AppLocalizations l10n) =>
    kg.isFinite && kg <= 0 ? l10n.quantityKg('0') : kgQuantityText(kg, l10n);
