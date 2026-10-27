// Minimal Isar type stubs for web compilation.
//
// On web, Isar is not used. These stubs satisfy type references in
// shared code (e.g., AuthBloc) so they compile to JavaScript.

/// Stub for Isar database type.
class Isar {
  static const int autoIncrement = -9007199254740990;

  Future<T> writeTxn<T>(Future<T> Function() callback) {
    throw UnsupportedError('Isar not available on web');
  }

  Future<T> txn<T>(Future<T> Function() callback) {
    throw UnsupportedError('Isar not available on web');
  }

  Future<bool> close() async => true;

  // Explicit collection getters for static analysis.
  // Extensions from .g.dart files are not available on web, so we
  // provide these typed as dynamic to satisfy the compiler.
  // They are never called at runtime (kIsWeb guards prevent it).
  dynamic get cropListings => null;
  dynamic get deliverys => null;
  dynamic get orders => null;
  dynamic get payments => null;
  dynamic get paymentIntents => null;
  dynamic get trips => null;
  dynamic get users => null;
  dynamic get syncOperations => null;
  dynamic get buyerIntents => null;
  dynamic get messages => null;
  dynamic get ratings => null;
  dynamic get conversationReads => null;
  dynamic get appNotifications => null;

  /// Catch-all for any other collection accessors.
  @override
  dynamic noSuchMethod(Invocation invocation) => null;
}

/// Typedef matching Isar's Id type.
typedef Id = int;
