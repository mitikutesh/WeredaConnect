import 'package:flutter/widgets.dart' show StringCharacters;

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';
import 'package:wereda_connect/core/utils/contact_in_text.dart';

/// LT-12 (live-trip.md section 10, order-lifecycle.md 2.5.0): the buyer's
/// optional drop-off landmark on an order ("Merkato, near Anwar Mosque"). The
/// place list has towns, not neighbourhoods, so the driver needs a few words
/// more. The client's mirror of the server's `OrderDropoffNote`: the same
/// limit counted the same way, the same phone number screen and the same edit
/// window, so a form refuses what the server would. Safe on web.

/// Most user-visible characters (grapheme clusters, as `TextField.maxLength`
/// and the server count them): an Amharic syllable such as "ሰ" is one, and
/// so is an emoji with its modifiers.
const int dropoffNoteMaxLength = 80;

/// Order statuses before pickup, the only ones in which the buyer may change
/// the note (the server also asks the order's delivery).
const Set<OrderStatus> dropoffNoteEditableStatuses = {
  ...activeOrderStatuses,
  OrderStatus.paymentFailed,
};

/// Control characters (C0, DEL, C1) and the Unicode line and paragraph
/// separators. The server refuses a note holding any of them (one line,
/// `ORDER_VALIDATION`), a permanent refusal that would remove an order queued
/// offline, so they never leave the phone.
final RegExp _lineBreakOrControl = RegExp(r'[\x00-\x1F\x7F-\x9F\u2028\u2029]');

/// Two or more white space characters in a row.
final RegExp _spaceRun = RegExp(r'\s{2,}');

/// The note as it is sent and stored: every control character and line or
/// paragraph separator becomes a space (a pasted tab, `\r\n` or a line
/// break), runs of spaces become one, and the ends are trimmed; null when
/// nothing is left. Mobile and web send only this.
String? normalizeDropoffNote(String? text) {
  final note = (text ?? '')
      .replaceAll(_lineBreakOrControl, ' ')
      .replaceAll(_spaceRun, ' ')
      .trim();
  return note.isEmpty ? null : note;
}

/// The note's length as the limit counts it (grapheme clusters), after
/// [normalizeDropoffNote].
int dropoffNoteLength(String text) =>
    normalizeDropoffNote(text)?.characters.length ?? 0;

/// Why [text] cannot be sent as a note, or null when it can: over
/// [dropoffNoteMaxLength] characters, or holding a phone number (the farmer
/// reads it before contact opens, ESC-48).
DropoffNoteProblem? dropoffNoteProblem(String? text) {
  final note = normalizeDropoffNote(text);
  if (note == null) return null;
  if (dropoffNoteLength(note) > dropoffNoteMaxLength) {
    return DropoffNoteProblem.tooLong;
  }
  return containsPhoneNumber(note) ? DropoffNoteProblem.phoneNumber : null;
}

/// What is wrong with a drop-off note.
enum DropoffNoteProblem { tooLong, phoneNumber }

/// Whether [userId] may add or change the note of [order] now: its buyer, on
/// a trucked order (a self-pickup order has no drop-off), before pickup.
bool canEditDropoffNote(Order order, String userId) =>
    userId.isNotEmpty &&
    userId == order.buyerId &&
    orderHasTrip(order) &&
    dropoffNoteEditableStatuses.contains(order.status);
