import 'package:wereda_connect/core/utils/entity_id.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// The name the server writes over a deleted account's copied names
/// (orders, deliveries, messages, ...; account-deletion.md).
const deletedUserSentinel = 'Deleted user';

/// [name] for display: the sentinel (exact match only) becomes the localized
/// "Deleted user", a user id where a name belongs (some records copied the
/// id when the name was missing, SIM-33) is empty like a missing name, and
/// anything else is shown as it is.
String partyDisplayName(String? name, AppLocalizations l10n) {
  if (name == deletedUserSentinel) return l10n.deletedUser;
  if (name == null || looksLikeEntityId(name)) return '';
  return name;
}
