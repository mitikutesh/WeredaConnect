import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// The name the server writes over a deleted account's copied names
/// (orders, deliveries, messages, ...; account-deletion.md).
const deletedUserSentinel = 'Deleted user';

/// [name] for display: the sentinel (exact match only) becomes the localized
/// "Deleted user", anything else is shown as it is.
String partyDisplayName(String? name, AppLocalizations l10n) =>
    name == deletedUserSentinel ? l10n.deletedUser : (name ?? '');
