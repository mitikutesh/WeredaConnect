import 'dart:async';

/// Polls [condition] every 10ms until it holds or [timeout] passes.
///
/// Use this instead of a fixed sleep before asserting on an async side effect
/// (a bloc emission, an Isar write, a stream event). A fixed sleep races the
/// side effect on a loaded CI runner: the assert fails, and a bloc left open
/// by the failed test can then hang the rest of the file. On timeout this
/// falls through instead of failing, so the expect that follows reports what
/// actually happened.
Future<void> waitFor(
  FutureOr<bool> Function() condition, {
  Duration timeout = const Duration(seconds: 10),
}) async {
  final deadline = DateTime.now().add(timeout);
  while (!await condition() && DateTime.now().isBefore(deadline)) {
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
}

/// Waits (bounded, see [waitFor]) until [states] holds at least [count]
/// emissions.
Future<void> waitForStates<T>(List<T> states, int count) =>
    waitFor(() => states.length >= count);
