/// Order status wire-format translation.
///
/// An order's `status` crosses the `/sync` wire in **snake_case** — the vocabulary
/// the server's state machine uses (`OrderTransitions` in `SyncFunction.cs`, e.g.
/// `payment_submitted`). The Dart [OrderStatus] enum, however, uses **camelCase**
/// member names (`paymentSubmitted`). Single-word statuses (`created`, `funded`,
/// `accepted`, `rejected`, `cancelled`, `delivered`, `disbursing`, `settled`,
/// `disputed`) are byte-identical in both forms and "just work"; the multi-word
/// ones do not:
///
/// | enum name          | wire token          |
/// |--------------------|---------------------|
/// | pendingPayment     | pending_payment     |
/// | paymentSubmitted   | payment_submitted   |
/// | pickupConfirmed    | pickup_confirmed    |
/// | inTransit          | in_transit          |
/// | paymentFailed      | payment_failed      |
/// | disputeResolved    | dispute_resolved    |
///
/// Before this translation existed, writes sent camelCase (which the server
/// rejected as an invalid transition) and reads matched the raw wire string
/// against `OrderStatus.name`, silently collapsing any snake_case status to the
/// `created` fallback. Mirror of `web_crop_listing_repository.dart`'s
/// `available`↔`active` mapping, but generalized so every status round-trips.
library;

/// Convert a camelCase [OrderStatus] name to its snake_case wire token.
///
/// Used when serializing an order `status` into a `/sync` operation payload.
String orderStatusToWire(String enumName) => enumName.replaceAllMapped(
      RegExp('[A-Z]'),
      (match) => '_${match[0]!.toLowerCase()}',
    );

/// Convert a snake_case wire token to its camelCase [OrderStatus] name.
///
/// Used when parsing an order `status` string received from the server, before
/// matching it against `OrderStatus.values`.
String orderStatusFromWire(String wire) => wire.replaceAllMapped(
      RegExp('_([a-z])'),
      (match) => match[1]!.toUpperCase(),
    );
