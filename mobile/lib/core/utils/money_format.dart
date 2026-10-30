import 'package:intl/intl.dart';

final NumberFormat _amount = NumberFormat('#,##0.##', 'en_US');
final NumberFormat _whole = NumberFormat('#,##0', 'en_US');

/// An amount as the boards write it: thousands grouped, cents only when
/// there are ("68,400", "1,234.5"). Pair it with `l10n.etbCurrency`.
String etbAmount(num amount) => _amount.format(amount);

/// A whole quantity with grouped thousands ("1,500" kg).
String wholeNumber(num value) => _whole.format(value);
