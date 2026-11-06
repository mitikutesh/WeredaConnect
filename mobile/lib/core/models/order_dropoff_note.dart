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

/// The note as the server stores it: trimmed, null when empty.
String? normalizeDropoffNote(String? text) {
  final trimmed = text?.trim() ?? '';
  return trimmed.isEmpty ? null : trimmed;
}

/// The note's length as the limit counts it (grapheme clusters).
int dropoffNoteLength(String text) => text.trim().characters.length;

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
