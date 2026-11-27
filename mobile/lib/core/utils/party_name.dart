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

/// [name] where the screen cannot say who the party is by role (a chat
/// title, the inbox): the localized "User" when there is no real name
/// (missing, or an id, FUP-130). Never an id.
String partyNameOrUnnamed(String? name, AppLocalizations l10n) {
  final shown = partyDisplayName(name, l10n).trim();
  return shown.isEmpty ? l10n.unnamedUser : shown;
}

/// [name] as the app may store or send it as a party's name (FUP-130):
/// trimmed, and empty when it is missing or an id. The server writes the
/// real name where it knows one; an id is never a name.
String partyNameToStore(String? name) {
  final trimmed = name?.trim() ?? '';
  return looksLikeEntityId(trimmed) ? '' : trimmed;
}
