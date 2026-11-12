import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/core/utils/contact_in_text.dart';

/// The sync entity that carries a profile edit (sync-protocol.md 1.55.0).
const String userProfileEntityType = 'user';

/// What a profile edit changes (SIM-56): only the fields whose trimmed value
/// differs from the one on the device. Null means "not changed".
@immutable
class ProfileChanges {
  final String? displayName;
  final String? woreda;
  final String? companyName;

  const ProfileChanges({this.displayName, this.woreda, this.companyName});

  /// The fields of the edited form that differ from [current] (trimmed).
  /// [companyName] is only compared when given (a company buyer's form).
  factory ProfileChanges.between({
    required ProfileValues current,
    required String displayName,
    required String woreda,
    String? companyName,
  }) {
    String? changed(String edited, String? before) {
      final value = edited.trim();
      return value == (before ?? '').trim() ? null : value;
    }

    return ProfileChanges(
      displayName: changed(displayName, current.displayName),
      woreda: changed(woreda, current.woreda),
      companyName: companyName == null
          ? null
          : changed(companyName, current.companyName),
    );
  }

  bool get isEmpty =>
      displayName == null && woreda == null && companyName == null;

  /// The `data` of the `user` update operation: only the changed keys.
  Map<String, dynamic> toSyncData() => {
        if (displayName != null) 'displayName': displayName,
        if (woreda != null) 'woreda': woreda,
        if (companyName != null) 'companyName': companyName,
      };

  @override
  bool operator ==(Object other) =>
      other is ProfileChanges &&
      other.displayName == displayName &&
      other.woreda == woreda &&
      other.companyName == companyName;

  @override
  int get hashCode => Object.hash(displayName, woreda, companyName);
}

/// The editable profile fields as the device or the server holds them,
/// with the role and buyer type that decide whether a company name exists.
@immutable
class ProfileValues {
  final String displayName;
  final String woreda;

  /// `farmer`, `trucker` or `buyer` (lowercase, as the server writes it).
  final String? role;
  final String? buyerType;
  final String? companyName;

  const ProfileValues({
    required this.displayName,
    required this.woreda,
    this.role,
    this.buyerType,
    this.companyName,
  });

  /// A buyer registered as a company: the only account with a company name.
  /// The role counts too, as on the server: an account whose role an admin
  /// changed away from buyer keeps its buyer type but has no company name.
  bool get isCompanyBuyer =>
      role?.toLowerCase() == 'buyer' && buyerType?.toLowerCase() == 'company';

  /// [this] with [changes] applied.
  ProfileValues apply(ProfileChanges changes) => ProfileValues(
        displayName: changes.displayName ?? displayName,
        woreda: changes.woreda ?? woreda,
        role: role,
        buyerType: buyerType,
        companyName: changes.companyName ?? companyName,
      );

  /// The `user` object of `GET /user/profile` (or a sign-in), or null when
  /// it is not one or is another account's.
  static ProfileValues? fromServerUser(Object? user, {required String userId}) {
    if (user is! Map || user['userId'] != userId) return null;
    final woreda = user['woreda'];
    if (woreda is! String) return null;
    String? text(String key) {
      final value = user[key];
      return value is String ? value : null;
    }

    return ProfileValues(
      // The marketplace name, else the Fayda name, as listings take it.
      displayName: text('displayName') ?? text('fullName') ?? '',
      woreda: woreda,
      role: text('role'),
      buyerType: text('buyerType'),
      companyName: text('companyName'),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is ProfileValues &&
      other.displayName == displayName &&
      other.woreda == woreda &&
      other.role == role &&
      other.buyerType == buyerType &&
      other.companyName == companyName;

  @override
  int get hashCode =>
      Object.hash(displayName, woreda, role, buyerType, companyName);
}

/// Why a field of the profile form is refused. The server's rules
/// (user-entity.md 2.2.0), checked on the device first so a queued edit is
/// never refused.
enum ProfileFieldError { required, tooLong, contactInText, nameUnavailable }

/// The registration's limit for the name, woreda and company name.
const int profileFieldMaxLength = 100;

/// The name a deleted account's rows carry (`AccountDeletionPolicy`); a live
/// user may not take it.
const String _deletedAccountName = 'deleted user';

/// The rule a profile text field breaks, or null. [isName] adds the
/// deleted-account name check.
ProfileFieldError? profileFieldError(String? value, {bool isName = false}) {
  final text = (value ?? '').trim();
  if (text.isEmpty) return ProfileFieldError.required;
  if (text.length > profileFieldMaxLength) return ProfileFieldError.tooLong;
  if (isName && text.toLowerCase() == _deletedAccountName) {
    return ProfileFieldError.nameUnavailable;
  }
  // FUP-52: the same detector as the server.
  if (containsPhoneNumber(text)) return ProfileFieldError.contactInText;
  return null;
}

/// The fields a refused web save names (`AUTH_027: Remove the phone number
/// from displayName, woreda. ...`), or empty when [message] is no such
/// refusal.
List<String> contactInTextFields(String? message) {
  const prefix = 'AUTH_027: Remove the phone number from ';
  if (message == null || !message.startsWith(prefix)) return const [];
  final end = message.indexOf('.', prefix.length);
  if (end < 0) return const [];
  return message
      .substring(prefix.length, end)
      .split(',')
      .map((f) => f.trim())
      .where((f) => f.isNotEmpty)
      .toList();
}

/// How a saved edit reached the server.
enum ProfileSaveOutcome {
  /// Web: the server took it.
  saved,

  /// Mobile: kept on the phone; the sync queue sends it when online.
  queued,
}

/// SIM-56: the signed-in user's own profile (user-entity.md 2.2.0). The read
/// is `GET /user/profile` on both platforms; mobile queues the edit for the
/// sync queue (offline-first), web sends it straight to /sync.
///
/// Every role edits the same fields: the name and woreda, and a company
/// buyer's company name. Role, phone, verification and Fayda identity are
/// never sent.
abstract class BaseProfileRepository {
  BaseProfileRepository({required ApiClient api}) : _api = api;

  final ApiClient _api;

  /// How long the read may take before the screen keeps the device's copy.
  static const fetchTimeout = Duration(seconds: 8);

  /// The server's copy of [userId]'s profile, or null when it cannot be read
  /// (offline, an error, another account's answer). Never throws.
  Future<ProfileValues?> fetch(String userId) async {
    try {
      final body = await _api
          .get<Map<String, dynamic>>(ApiConfig.userProfileEndpoint)
          .timeout(fetchTimeout);
      return ProfileValues.fromServerUser(body['user'], userId: userId);
    } on Object catch (e) {
      debugPrint('[ProfileRepository] profile read skipped: $e');
      return null;
    }
  }

  /// Sends [changes] to [userId]'s profile. Throws when nothing was saved:
  /// a full queue on mobile, a refusal (`SyncRejectedException`) or no
  /// answer on web.
  Future<ProfileSaveOutcome> save({
    required String userId,
    required ProfileChanges changes,
  });

  /// Whether an edit of [userId]'s profile still waits on this device (a
  /// server read is then older than the device's copy). Always false on web.
  Future<bool> hasUnsentEdit(String userId);
}
