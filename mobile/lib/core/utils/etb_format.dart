import 'package:intl/intl.dart';

/// Birr the way the Fieldway boards write it: grouped thousands and no
/// decimals unless the amount has some ("68,400", "1,234.56"). Pair it with
/// `l10n.etbCurrency` for the unit.
String formatEtbAmount(num amount) {
  final whole = amount == amount.roundToDouble();
  return (whole ? _wholeFormat : _centsFormat).format(amount);
}

final NumberFormat _wholeFormat = NumberFormat('#,##0', 'en_US');
final NumberFormat _centsFormat = NumberFormat('#,##0.00', 'en_US');

/// A whole quantity with grouped thousands ("1,500" kg).
String wholeNumber(num value) => _wholeFormat.format(value);
