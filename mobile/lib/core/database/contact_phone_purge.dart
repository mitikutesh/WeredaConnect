import 'package:isar/isar.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/core/models/trip.dart';

/// ESC-48: removes the counterpart phone numbers this device cached before
/// contact moved to the order (order-lifecycle.md 1.18.0). Earlier versions
/// stored every farmer's number on browsed crop listings, every trucker's on
/// browsed trips, and both parties' numbers on every message; the server no
/// longer sends them, but a pull does not rewrite rows it no longer returns
/// (messages are pulled incrementally).
///
/// Mobile only (Isar). Safe to run on every start: it touches only rows that
/// still hold a number, so after the first run it writes nothing. The user's
/// own listings and trips keep their own number (the trip form edits it).
class ContactPhonePurge {
  const ContactPhonePurge._();

  /// Clears the cached numbers and returns how many rows changed. Without a
  /// signed-in user, listings and trips are left for the next pull (which
  /// drops every row that is not the user's own) and only messages are cleared.
  static Future<int> run(Isar isar, {String? currentUserId}) async {
    var changed = 0;
    await isar.writeTxn(() async {
      final messages = await isar.messages
          .filter()
          .senderPhoneIsNotNull()
          .or()
          .recipientPhoneIsNotNull()
          .findAll();
      for (final m in messages) {
        m
          ..senderPhone = null
          ..recipientPhone = null;
      }
      await isar.messages.putAll(messages);
      changed += messages.length;

      final me = currentUserId ?? '';
      if (me.isEmpty) return;

      final crops = await isar.cropListings
          .filter()
          .not()
          .farmerIdEqualTo(me)
          .and()
          .farmerPhoneIsNotEmpty()
          .findAll();
      for (final c in crops) {
        c.farmerPhone = '';
      }
      await isar.cropListings.putAll(crops);
      changed += crops.length;

      final trips = await isar.trips
          .filter()
          .not()
          .truckerIdEqualTo(me)
          .and()
          .truckerPhoneIsNotEmpty()
          .findAll();
      for (final t in trips) {
        t.truckerPhone = '';
      }
      await isar.trips.putAll(trips);
      changed += trips.length;
    });
    return changed;
  }
}
