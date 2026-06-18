import 'package:wereda_connect/core/models/buyer_intent.dart';

/// Number of matching listings the buyer has NOT seen yet for [intent].
///
/// Count-delta based: the match count at the buyer's last view is snapshotted
/// into [BuyerIntent.lastSeenMatchCount] (by `updateLastViewed`), and anything
/// beyond that high-water mark is "new". This is deterministic — it never
/// false-fires on a re-sync that added no matches, and clamps to zero when the
/// count drops. A never-viewed intent (`lastSeenMatchCount == 0`) treats all
/// of its matches as new.
int newMatchCount(BuyerIntent intent) {
  final delta = intent.matchCount - intent.lastSeenMatchCount;
  return delta > 0 ? delta : 0;
}

/// Whether [intent] has matches the buyer has not seen yet.
bool hasNewMatches(BuyerIntent intent) => newMatchCount(intent) > 0;
