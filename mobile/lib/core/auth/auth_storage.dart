/// Abstract interface for auth token storage.
///
/// Mobile uses FlutterSecureStorage (encrypted SharedPreferences).
/// Web uses browser localStorage/sessionStorage.
abstract class AuthStorage {
  Future<String?> read({required String key});
  Future<void> write({required String key, required String value});
  Future<void> delete({required String key});
}
