import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

/// BLoC for managing authentication state.
///
/// This BLoC handles:
/// - User registration with Fayda eKYC verification
/// - Online/offline login
/// - Token refresh
/// - Logout
/// - Session persistence across app restarts
///
/// State transitions:
/// - AuthInitial → AuthLoading → AuthAuthenticated/AuthUnauthenticated/AuthError
/// - AuthUnauthenticated → AuthLoading → AuthAuthenticated/AuthError (registration)
///
/// Ethiopian context:
/// - Supports offline authentication with cached credentials
/// - Auto-detects connectivity and chooses appropriate auth method
/// - Expired tokens still emit AuthAuthenticated (offline-first: lazy refresh)
class AuthBloc extends Bloc<AuthEvent, AuthState> {
  final AuthService _authService;
  final OfflineAuthCache _offlineCache;
  final Isar? _isar;
  String? _currentPhoneNumber;

  static const _secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );
  static const _currentUserPhoneKey = 'current_user_phone';
  static const _activeAccessTokenKey = 'access_token';
  static const _activeRefreshTokenKey = 'refresh_token';

  AuthBloc({
    AuthService? authService,
    OfflineAuthCache? offlineCache,
    Isar? isar,
  })  : _authService = authService ?? AuthService(),
        _offlineCache = offlineCache ?? OfflineAuthCache(),
        _isar = isar,
        super(const AuthInitial()) {
    // Register event handlers
    on<AuthCheckRequested>(_onAuthCheckRequested);
    on<AuthRegisterRequested>(_onAuthRegisterRequested);
    on<AuthLoginRequested>(_onAuthLoginRequested);
    on<AuthLoginOnlineRequested>(_onAuthLoginOnlineRequested);
    on<AuthLoginOfflineRequested>(_onAuthLoginOfflineRequested);
    on<AuthRefreshTokenRequested>(_onAuthRefreshTokenRequested);
    on<AuthLogoutRequested>(_onAuthLogoutRequested);
  }

  /// Save the current session phone number to secure storage.
  Future<void> _saveSession(String phoneNumber) async {
    _currentPhoneNumber = phoneNumber;
    await _secureStorage.write(
      key: _currentUserPhoneKey,
      value: phoneNumber,
    );
  }

  /// Write active tokens to well-known keys for the AuthInterceptor.
  ///
  /// The AuthInterceptor reads these keys to inject the Authorization
  /// header and to obtain the refresh token when a 401 is received.
  Future<void> _saveActiveTokens(AuthTokens tokens) async {
    await _secureStorage.write(
      key: _activeAccessTokenKey,
      value: tokens.accessToken,
    );
    await _secureStorage.write(
      key: _activeRefreshTokenKey,
      value: tokens.refreshToken,
    );
  }

  /// Clear the current session from secure storage.
  Future<void> _clearSession() async {
    _currentPhoneNumber = null;
    await _secureStorage.delete(key: _currentUserPhoneKey);
    await _secureStorage.delete(key: _activeAccessTokenKey);
    await _secureStorage.delete(key: _activeRefreshTokenKey);
  }

  /// Check if user is already authenticated.
  ///
  /// Reads stored phone number from secure storage, then checks for
  /// cached tokens and user profile. Emits AuthAuthenticated if a
  /// valid session exists, AuthUnauthenticated otherwise.
  ///
  /// Offline-first: expired tokens still emit AuthAuthenticated.
  /// Token refresh happens lazily on API calls.
  Future<void> _onAuthCheckRequested(
    AuthCheckRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    try {
      // Read stored phone number
      final phone = await _secureStorage.read(key: _currentUserPhoneKey);
      if (phone == null || phone.isEmpty) {
        emit(const AuthUnauthenticated());
        return;
      }

      _currentPhoneNumber = phone;

      // Check for cached tokens
      final tokens = await _offlineCache.getCachedTokens(phone);
      if (tokens == null) {
        emit(const AuthUnauthenticated());
        return;
      }

      // Check for cached user profile
      final user = await _offlineCache.getCachedUser(phone);
      if (user == null) {
        emit(const AuthUnauthenticated());
        return;
      }

      // Offline-first: let user in even with expired tokens.
      // Token refresh happens lazily on API calls.
      await _saveActiveTokens(tokens);
      emit(
        AuthAuthenticated(
          userId: user.userId,
          tokens: tokens,
          isOfflineAuth: tokens.isExpired,
          role: user.role,
          userName: user.name,
          woreda: user.woreda,
        ),
      );
    } on Exception {
      emit(const AuthUnauthenticated());
    }
  }

  /// Handle user registration with Fayda verification token.
  ///
  /// Server validates the Fayda token and creates the user account,
  /// returning auth tokens directly (no OTP step).
  Future<void> _onAuthRegisterRequested(
    AuthRegisterRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    _currentPhoneNumber = event.phoneNumber;

    final result = await _authService.register(
      phoneNumber: event.phoneNumber,
      password: event.password,
      name: event.name,
      role: event.role,
      woreda: event.woreda,
      faydaVerificationToken: event.faydaVerificationToken,
      preferredLanguage: event.preferredLanguage,
    );

    if (result.isSuccess) {
      await _saveSession(event.phoneNumber);
      await _saveActiveTokens(result.tokens!);

      emit(
        AuthAuthenticated(
          userId: result.userId!,
          tokens: result.tokens!,
          role: event.role,
          userName: event.name,
          woreda: event.woreda,
        ),
      );
    } else {
      emit(
        AuthError(
          errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
          errorMessage: result.errorMessage ??
              result.errorCode?.userMessage ??
              'Registration failed',
          canRetry: result.errorCode?.isRecoverable ?? true,
        ),
      );
    }
  }

  /// Handle login (auto-detect online/offline).
  Future<void> _onAuthLoginRequested(
    AuthLoginRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    final result = await _authService.login(
      phoneNumber: event.phoneNumber,
      password: event.password,
    );

    if (result.isSuccess) {
      await _saveSession(event.phoneNumber);
      await _saveActiveTokens(result.tokens!);

      final user = await _offlineCache.getCachedUser(event.phoneNumber);

      emit(
        AuthAuthenticated(
          userId: result.userId!,
          tokens: result.tokens!,
          isOfflineAuth: result.isOfflineAuth,
          role: user?.role ?? UserRole.farmer,
          userName: user?.name ?? '',
          woreda: user?.woreda,
        ),
      );
    } else {
      emit(
        AuthError(
          errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
          errorMessage: result.errorMessage ??
              result.errorCode?.userMessage ??
              'Login failed',
          canRetry: result.errorCode?.isRecoverable ?? true,
          retryAfterSeconds: result.retryAfterSeconds,
        ),
      );
    }
  }

  /// Handle online login.
  Future<void> _onAuthLoginOnlineRequested(
    AuthLoginOnlineRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    final result = await _authService.loginOnline(
      phoneNumber: event.phoneNumber,
      password: event.password,
    );

    if (result.isSuccess) {
      await _saveSession(event.phoneNumber);
      await _saveActiveTokens(result.tokens!);

      final user = await _offlineCache.getCachedUser(event.phoneNumber);

      emit(
        AuthAuthenticated(
          userId: result.userId!,
          tokens: result.tokens!,
          role: user?.role ?? UserRole.farmer,
          userName: user?.name ?? '',
          woreda: user?.woreda,
        ),
      );
    } else {
      emit(
        AuthError(
          errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
          errorMessage: result.errorMessage ??
              result.errorCode?.userMessage ??
              'Login failed',
          canRetry: result.errorCode?.isRecoverable ?? true,
        ),
      );
    }
  }

  /// Handle offline login.
  Future<void> _onAuthLoginOfflineRequested(
    AuthLoginOfflineRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    final result = await _authService.loginOffline(
      phoneNumber: event.phoneNumber,
      password: event.password,
    );

    if (result.isSuccess) {
      await _saveSession(event.phoneNumber);
      await _saveActiveTokens(result.tokens!);

      final user = await _offlineCache.getCachedUser(event.phoneNumber);

      emit(
        AuthAuthenticated(
          userId: result.userId!,
          tokens: result.tokens!,
          isOfflineAuth: true,
          role: user?.role ?? UserRole.farmer,
          userName: user?.name ?? '',
          woreda: user?.woreda,
        ),
      );
    } else {
      emit(
        AuthError(
          errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
          errorMessage: result.errorMessage ??
              result.errorCode?.userMessage ??
              'Offline login failed',
          canRetry: result.errorCode?.isRecoverable ?? true,
        ),
      );
    }
  }

  /// Handle token refresh.
  Future<void> _onAuthRefreshTokenRequested(
    AuthRefreshTokenRequested event,
    Emitter<AuthState> emit,
  ) async {
    // Don't emit loading state for background token refresh
    final currentState = state;

    final result = await _authService.refreshToken(event.refreshToken);

    if (result.isSuccess) {
      // Update tokens in current authenticated state
      if (currentState is AuthAuthenticated) {
        await _saveActiveTokens(result.tokens!);

        // Update cached tokens
        if (_currentPhoneNumber != null) {
          await _offlineCache.updateTokens(
            _currentPhoneNumber!,
            result.tokens!,
          );
        }

        emit(
          AuthAuthenticated(
            userId: currentState.userId,
            tokens: result.tokens!,
            isOfflineAuth: currentState.isOfflineAuth,
            role: currentState.role,
            userName: currentState.userName,
            woreda: currentState.woreda,
          ),
        );
      }
    } else {
      // Token refresh failed - logout user
      emit(const AuthUnauthenticated());
    }
  }

  /// Handle logout.
  ///
  /// Always clears user-specific business data (deliveries, payments, listings)
  /// from the local Isar database to prevent data leakage on shared devices.
  /// Preserves offline credentials (clearCache: false) so the user can re-login
  /// offline — critical for Ethiopian rural connectivity.
  Future<void> _onAuthLogoutRequested(
    AuthLogoutRequested event,
    Emitter<AuthState> emit,
  ) async {
    final currentState = state;

    if (currentState is AuthAuthenticated) {
      emit(const AuthLoading());

      final userId = currentState.userId;

      // Preserve offline credentials (password hash, tokens) so user can
      // re-login offline. Only server-side token invalidation is attempted.
      await _authService.logout(
        phoneNumber: _currentPhoneNumber ?? '',
        accessToken: currentState.tokens.accessToken,
        refreshToken: currentState.tokens.refreshToken,
        clearCache: false,
      );

      // Always clear business data to prevent data leakage on shared devices
      if (_isar != null) {
        // SECURITY: Cancel pending payment intents for this user before logout
        // to prevent the next user's session from processing them.
        await _cancelUserPendingIntents(_isar, userId);
        await _clearUserBusinessData(_isar, userId);
      }

      await _clearSession();

      emit(const AuthUnauthenticated());
    }
  }

  /// Cancel pending payment intents belonging to [userId].
  ///
  /// Only cancels intents where [buyerId] matches the logging-out user,
  /// preserving other users' queued payments on shared devices.
  Future<void> _cancelUserPendingIntents(Isar isar, String userId) async {
    await isar.writeTxn(() async {
      final pending = await isar.paymentIntents
          .filter()
          .statusEqualTo(IntentStatus.pendingSync)
          .buyerIdEqualTo(userId)
          .findAll();

      for (final intent in pending) {
        intent.status = IntentStatus.cancelled;
        await isar.paymentIntents.put(intent);
      }
    });
  }

  /// Delete **private** business data belonging to [userId] from the local cache.
  ///
  /// Clears: deliveries, payments where user is a participant.
  /// Preserves: crop listings and trip listings (public marketplace data that
  /// other users on this device should still be able to browse).
  Future<void> _clearUserBusinessData(Isar isar, String userId) async {
    await isar.writeTxn(() async {
      // NOTE: Crop listings and trip listings are intentionally NOT cleared.
      // They are public marketplace data visible to all users. Clearing them
      // would prevent other users on the same device from browsing listings
      // (especially important in offline/mock mode where data is local-only).

      // Delete deliveries where user is a participant
      final asFarmer =
          await isar.deliverys.filter().farmerIdEqualTo(userId).findAll();
      final asTrucker =
          await isar.deliverys.filter().truckerIdEqualTo(userId).findAll();
      final asBuyer =
          await isar.deliverys.filter().buyerIdEqualTo(userId).findAll();
      final deliveryIds = <int>{};
      for (final d in [...asFarmer, ...asTrucker, ...asBuyer]) {
        deliveryIds.add(d.id);
      }
      await isar.deliverys.deleteAll(deliveryIds.toList());

      // Delete payments where user is a participant
      final farmerPayments =
          await isar.payments.filter().farmerIdEqualTo(userId).findAll();
      final buyerPayments =
          await isar.payments.filter().buyerIdEqualTo(userId).findAll();
      final paymentIds = <int>{};
      for (final p in [...farmerPayments, ...buyerPayments]) {
        paymentIds.add(p.id);
      }
      await isar.payments.deleteAll(paymentIds.toList());
    });
  }
}
