import 'package:intl/intl.dart';

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
