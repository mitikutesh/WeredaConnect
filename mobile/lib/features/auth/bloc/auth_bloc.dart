import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:isar/isar.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_stub.dart';
import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart'
    if (dart.library.html) 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/auth/session_signals.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/conversation_read.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/payment_intent.dart';
import 'package:wereda_connect/core/models/rating.dart';
import 'package:wereda_connect/core/models/sync_operation.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/data/repositories/web_message_repository.dart';

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
/// - Register without matching Fayda → AuthOtpVerificationRequired →
///   AuthVerifyOtpRequested → AuthAuthenticated
///
/// Ethiopian context:
/// - Supports offline authentication with cached credentials
/// - Auto-detects connectivity and chooses appropriate auth method
/// - Expired tokens still emit AuthAuthenticated (offline-first: lazy refresh)
class AuthBloc extends Bloc<AuthEvent, AuthState> {
  final AuthService _authService;
  final OfflineAuthCache _offlineCache;
  final Isar? _isar;
  final Future<String?> Function()? _pushTokenProvider;
  final Future<void> Function()? _pushTokenDeleter;
  final Future<void> Function(String userId) _forgetReadMarkers;
  final Future<void> Function(String userId) _forgetDataUsage;
  final SecurityEvents _securityEvents;
  StreamSubscription<void>? _sessionExpiredSub;
  StreamSubscription<String>? _accountGoneSub;
  String? _currentPhoneNumber;
  bool _cancelled = false;

  // Registration waiting for its SMS OTP. The password stays in memory only
  // (never persisted) so the offline credential cache can be built once the
  // OTP confirms and tokens arrive.
  String? _pendingOtpPhone;
  String? _pendingOtpPassword;
  String? _pendingDrivingLicensePath;
  String? _pendingBuyerType;
  String? _pendingCompanyName;

  /// Budget for the best-effort push-token unregister on logout.
  static const pushUnregisterBudget = Duration(seconds: 3);

  /// Budget for the best-effort server-side token revocation on logout,
  /// counted from when the call starts.
  static const logoutRevokeBudget = Duration(seconds: 5);

  static const _secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );
  static const _currentUserPhoneKey = 'current_user_phone';

  /// Secure storage key for the current user's ID.
  /// Exposed for use by SyncExecutor wiring in main.dart.
  static const currentUserIdKey = 'current_user_id';
  static const _activeAccessTokenKey = 'access_token';
  static const _activeRefreshTokenKey = 'refresh_token';

  /// Storage key for the Cognito ID token. This is the token the
  /// AuthInterceptor attaches as the API bearer, because `custom:role`
  /// (required for server-side write authorization) only exists on the
  /// ID token, not the access token.
  static const _activeIdTokenKey = 'id_token';

  /// [pushTokenProvider] returns this device's current FCM token (mobile
  /// only) so logout can ask the server to stop pushing to it.
  /// [pushTokenDeleter] drops that token on this device once the account is
  /// deleted (mobile only).
  /// [forgetReadMarkers] removes the web read markers of a deleted account,
  /// [forgetDataUsage] the data usage counters of one that is not signed in.
  /// [sessionExpired] fires when the server rejects the refresh token,
  /// [accountGone] when it refuses a request with `410 ACCOUNT_004`.
  /// [securityEvents] defaults to the app-wide bus (tests inject one).
  AuthBloc({
    AuthService? authService,
    OfflineAuthCache? offlineCache,
    Isar? isar,
    Future<String?> Function()? pushTokenProvider,
    Future<void> Function()? pushTokenDeleter,
    Future<void> Function(String userId)? forgetReadMarkers,
    Future<void> Function(String userId)? forgetDataUsage,
    Stream<void>? sessionExpired,
    Stream<String>? accountGone,
    SecurityEvents? securityEvents,
  })  : _authService = authService ?? AuthService(),
        _offlineCache = offlineCache ?? OfflineAuthCache(),
        _isar = isar,
        _pushTokenProvider = pushTokenProvider,
        _pushTokenDeleter = pushTokenDeleter,
        _forgetReadMarkers =
            forgetReadMarkers ?? WebMessageRepository.forgetReadMarkers,
        _forgetDataUsage = forgetDataUsage ?? DataUsageTracker.forgetStoredUser,
        _securityEvents = securityEvents ?? SecurityEvents.instance,
        super(const AuthInitial()) {
    // Register event handlers
    on<AuthCheckRequested>(_onAuthCheckRequested);
    on<AuthRegisterRequested>(_onAuthRegisterRequested);
    on<AuthLoginRequested>(_onAuthLoginRequested);
    on<AuthLoginOnlineRequested>(_onAuthLoginOnlineRequested);
    on<AuthLoginOfflineRequested>(_onAuthLoginOfflineRequested);
    on<AuthRefreshTokenRequested>(_onAuthRefreshTokenRequested);
    on<AuthCancelRequested>(_onAuthCancelRequested);
    on<AuthOtpAbandoned>(_onOtpAbandoned);
    on<AuthLogoutRequested>(_onAuthLogoutRequested);
    on<AuthAccountDeleted>(_onAccountDeleted);
    on<AuthAccountGone>(_onAccountGone);
    on<AuthForgotPasswordRequested>(_onForgotPassword);
    on<AuthResetPasswordRequested>(_onResetPassword);
    on<AuthVerifyOtpRequested>(_onVerifyOtp);
    on<AuthResendOtpRequested>(_onResendOtp);
    on<AuthStepUpSucceeded>(_onStepUpSucceeded);
    on<AuthSessionExpired>(_onSessionExpired);

    _sessionExpiredSub =
        (sessionExpired ?? SessionSignals.instance.onSessionExpired)
            .listen((_) => add(const AuthSessionExpired()));
    _accountGoneSub = (accountGone ?? SessionSignals.instance.onAccountGone)
        .listen((state) => add(AuthAccountGone(deletionState: state)));
  }

  /// The auth service, for flows that talk to it directly (step-up dialog).
  AuthService get authService => _authService;

  @override
  Future<void> close() async {
    await _sessionExpiredSub?.cancel();
    await _accountGoneSub?.cancel();
    return super.close();
  }

  /// The stored ID token, when it is [userId]'s own (its `sub`) and [userId]
  /// is signed in here and in the stored session: the bearer a request that
  /// must only ever act for [userId] sends itself. Null otherwise; on web the
  /// stored session is shared by every tab.
  Future<String?> bearerFor(String userId) async {
    if (!await isSignedInAs(userId)) return null;
    try {
      final token = await _secureStorage.read(key: _activeIdTokenKey);
      if (token == null || jwtSubject(token) != userId) return null;
      return token;
    } on Object {
      return null;
    }
  }

  /// Whether [userId] is signed in, here and in the stored session the API
  /// client sends. On web every tab shares that storage, so a tab still
  /// showing one account may hold another account's session.
  Future<bool> isSignedInAs(String userId) async {
    final current = state;
    if (current is! AuthAuthenticated || current.userId != userId) {
      return false;
    }
    try {
      return await _secureStorage.read(key: currentUserIdKey) == userId;
    } on Object {
      return false;
    }
  }

  void _clearPendingOtp() {
    _pendingOtpPhone = null;
    _pendingOtpPassword = null;
    _pendingDrivingLicensePath = null;
    _pendingBuyerType = null;
    _pendingCompanyName = null;
  }

  void _onOtpAbandoned(AuthOtpAbandoned event, Emitter<AuthState> emit) =>
      _clearPendingOtp();

  AuthError _errorFrom(AuthResult result, String fallback) => AuthError(
        errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
        errorMessage:
            result.errorMessage ?? result.errorCode?.userMessage ?? fallback,
        canRetry: result.errorCode?.isRecoverable ?? true,
        retryAfterSeconds: result.retryAfterSeconds,
      );

  /// A login for an account whose phone was never confirmed (AUTH_016):
  /// send a fresh OTP and move to the OTP screen instead of failing.
  /// Returns true when the OTP state was emitted.
  Future<bool> _redirectUnconfirmedLogin(
    AuthResult result,
    String phoneNumber,
    String password,
    Emitter<AuthState> emit,
  ) async {
    if (result.errorCode != AuthErrorCode.userNotConfirmed) return false;
    final resend = await _authService.resendOtp(phoneNumber: phoneNumber);
    if (_cancelled || !resend.isSuccess) return false;
    _clearPendingOtp();
    _pendingOtpPhone = phoneNumber;
    _pendingOtpPassword = password;
    emit(
      AuthOtpVerificationRequired(
        phoneNumber: phoneNumber,
        expiresAt: resend.otpExpiresAt,
      ),
    );
    return true;
  }

  /// Save the current session phone number and user ID to secure storage.
  Future<void> _saveSession(String phoneNumber, String userId) async {
    _currentPhoneNumber = phoneNumber;
    await _secureStorage.write(
      key: _currentUserPhoneKey,
      value: phoneNumber,
    );
    await _secureStorage.write(
      key: currentUserIdKey,
      value: userId,
    );
  }

  /// Write active tokens to well-known keys for the AuthInterceptor.
  ///
  /// The AuthInterceptor reads the ID token to inject the Authorization
  /// header and the refresh token when a 401 is received. The access token
  /// is still persisted for server-side token revocation on logout.
  Future<void> _saveActiveTokens(AuthTokens tokens) async {
    await _secureStorage.write(
      key: _activeAccessTokenKey,
      value: tokens.accessToken,
    );
    await _secureStorage.write(
      key: _activeIdTokenKey,
      value: tokens.idToken,
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
    await _secureStorage.delete(key: currentUserIdKey);
    await _secureStorage.delete(key: _activeAccessTokenKey);
    await _secureStorage.delete(key: _activeIdTokenKey);
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
      // Ensure current_user_id is set for per-user sync filtering
      // (may be missing on upgrade from older version without this key).
      await _secureStorage.write(key: currentUserIdKey, value: user.userId);
      emit(
        AuthAuthenticated(
          userId: user.userId,
          tokens: tokens,
          isOfflineAuth: tokens.isExpired,
          role: user.role,
          userName: user.name,
          phoneNumber: phone,
          woreda: user.woreda,
          isRestoredSession: true,
        ),
      );
    } on Exception {
      emit(const AuthUnauthenticated());
    }
  }

  /// Cancel an in-flight auth operation.
  ///
  /// Sets a cancellation flag so that any pending register/login handler
  /// will silently discard its result instead of emitting a new state.
  Future<void> _onAuthCancelRequested(
    AuthCancelRequested event,
    Emitter<AuthState> emit,
  ) async {
    _cancelled = true;
    emit(const AuthUnauthenticated());
  }

  /// Handle user registration with Fayda verification token.
  ///
  /// Server validates the Fayda token and creates the user account,
  /// returning auth tokens directly (no OTP step).
  Future<void> _onAuthRegisterRequested(
    AuthRegisterRequested event,
    Emitter<AuthState> emit,
  ) async {
    _cancelled = false;
    emit(const AuthLoading());

    _currentPhoneNumber = event.phoneNumber;

    try {
      final result = await _authService.register(
        phoneNumber: event.phoneNumber,
        password: event.password,
        name: event.name,
        role: event.role,
        woreda: event.woreda,
        faydaVerificationToken: event.faydaVerificationToken,
        preferredLanguage: event.preferredLanguage,
        drivingLicensePath: event.drivingLicensePath,
        buyerType: event.buyerType,
        companyName: event.companyName,
      );

      // User navigated away — discard the result
      if (_cancelled) return;

      if (result.isSuccess && result.otpRequired) {
        // 202: the phone must be confirmed by SMS before tokens are issued.
        _clearPendingOtp();
        _pendingOtpPhone = event.phoneNumber;
        _pendingOtpPassword = event.password;
        _pendingDrivingLicensePath = event.drivingLicensePath;
        _pendingBuyerType = event.buyerType;
        _pendingCompanyName = event.companyName;
        emit(
          AuthOtpVerificationRequired(
            phoneNumber: event.phoneNumber,
            expiresAt: result.otpExpiresAt,
          ),
        );
      } else if (result.isSuccess) {
        await _saveSession(event.phoneNumber, result.userId!);
        await _saveActiveTokens(result.tokens!);

        emit(
          AuthAuthenticated(
            userId: result.userId!,
            tokens: result.tokens!,
            role: event.role,
            userName: event.name,
            phoneNumber: event.phoneNumber,
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
    } on Object catch (e) {
      if (_cancelled) return;
      debugPrint('[AuthBloc] Registration failed unexpectedly: $e');
      emit(
        const AuthError(
          errorCode: AuthErrorCode.internalServerError,
          errorMessage: 'Something went wrong. Please try again.',
        ),
      );
    }
  }

  /// Handle login (auto-detect online/offline).
  Future<void> _onAuthLoginRequested(
    AuthLoginRequested event,
    Emitter<AuthState> emit,
  ) async {
    _cancelled = false;
    emit(const AuthLoading());

    try {
      final result = await _authService.login(
        phoneNumber: event.phoneNumber,
        password: event.password,
      );

      // User navigated away — discard the result
      if (_cancelled) return;

      if (result.isSuccess) {
        // F-34: an authenticated session MUST carry an authoritative role from
        // the login result; fail closed rather than silently defaulting to
        // farmer (which mis-scoped truckers/buyers on a cache miss).
        if (result.role == null) {
          emit(
            const AuthError(
              errorCode: AuthErrorCode.internalServerError,
              errorMessage:
                  'Could not determine your account role. Please log in again.',
            ),
          );
          return;
        }

        await _saveSession(event.phoneNumber, result.userId!);
        await _saveActiveTokens(result.tokens!);

        emit(
          AuthAuthenticated(
            userId: result.userId!,
            tokens: result.tokens!,
            isOfflineAuth: result.isOfflineAuth,
            role: result.role!,
            userName: result.userName ?? '',
            phoneNumber: event.phoneNumber,
            woreda: result.woreda,
          ),
        );
      } else {
        if (await _redirectUnconfirmedLogin(
          result,
          event.phoneNumber,
          event.password,
          emit,
        )) {
          return;
        }
        emit(_errorFrom(result, 'Login failed'));
      }
    } on Object catch (e) {
      if (_cancelled) return;
      debugPrint('[AuthBloc] Login failed unexpectedly: $e');
      emit(
        const AuthError(
          errorCode: AuthErrorCode.internalServerError,
          errorMessage: 'Something went wrong. Please try again.',
        ),
      );
    }
  }

  /// Handle online login.
  Future<void> _onAuthLoginOnlineRequested(
    AuthLoginOnlineRequested event,
    Emitter<AuthState> emit,
  ) async {
    _cancelled = false;
    emit(const AuthLoading());

    try {
      final result = await _authService.loginOnline(
        phoneNumber: event.phoneNumber,
        password: event.password,
      );

      // User navigated away — discard the result
      if (_cancelled) return;

      if (result.isSuccess) {
        // F-34: require an authoritative role from the login result; never
        // silently default to farmer on a cache miss.
        if (result.role == null) {
          emit(
            const AuthError(
              errorCode: AuthErrorCode.internalServerError,
              errorMessage:
                  'Could not determine your account role. Please log in again.',
            ),
          );
          return;
        }

        await _saveSession(event.phoneNumber, result.userId!);
        await _saveActiveTokens(result.tokens!);

        emit(
          AuthAuthenticated(
            userId: result.userId!,
            tokens: result.tokens!,
            role: result.role!,
            userName: result.userName ?? '',
            phoneNumber: event.phoneNumber,
            woreda: result.woreda,
          ),
        );
      } else {
        if (await _redirectUnconfirmedLogin(
          result,
          event.phoneNumber,
          event.password,
          emit,
        )) {
          return;
        }
        emit(_errorFrom(result, 'Login failed'));
      }
    } on Object catch (e) {
      if (_cancelled) return;
      debugPrint('[AuthBloc] Online login failed unexpectedly: $e');
      emit(
        const AuthError(
          errorCode: AuthErrorCode.internalServerError,
          errorMessage: 'Something went wrong. Please try again.',
        ),
      );
    }
  }

  /// Handle offline login.
  Future<void> _onAuthLoginOfflineRequested(
    AuthLoginOfflineRequested event,
    Emitter<AuthState> emit,
  ) async {
    _cancelled = false;
    emit(const AuthLoading());

    try {
      final result = await _authService.loginOffline(
        phoneNumber: event.phoneNumber,
        password: event.password,
      );

      // User navigated away — discard the result
      if (_cancelled) return;

      if (result.isSuccess) {
        // F-34: require an authoritative role from the login result; never
        // silently default to farmer on a cache miss.
        if (result.role == null) {
          emit(
            const AuthError(
              errorCode: AuthErrorCode.internalServerError,
              errorMessage:
                  'Could not determine your account role. Please log in again.',
            ),
          );
          return;
        }

        await _saveSession(event.phoneNumber, result.userId!);
        await _saveActiveTokens(result.tokens!);

        emit(
          AuthAuthenticated(
            userId: result.userId!,
            tokens: result.tokens!,
            isOfflineAuth: true,
            role: result.role!,
            userName: result.userName ?? '',
            phoneNumber: event.phoneNumber,
            woreda: result.woreda,
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
    } on Object catch (e) {
      if (_cancelled) return;
      debugPrint('[AuthBloc] Offline login failed unexpectedly: $e');
      emit(
        const AuthError(
          errorCode: AuthErrorCode.internalServerError,
          errorMessage: 'Something went wrong. Please try again.',
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
            phoneNumber: currentState.phoneNumber,
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
  /// Honors [AuthLogoutRequested.clearCache] (default true) for the offline
  /// credential cache: an explicit logout clears the cached tokens + PBKDF2
  /// password hash (auth-api.md invariant 7), while callers that want to keep
  /// offline re-login can pass clearCache: false.
  ///
  /// The server-side revocation is STARTED before the local clear, with the
  /// live stored tokens (SIM-55: sent after the clear, with the state's copy
  /// from login, it carried a dead access token and got a 401, and the
  /// interceptor could no longer refresh). It is not awaited before the
  /// clear: logout must never depend on the network. A revocation call that
  /// hung once left the session in storage and the UI stuck on
  /// [AuthLoading], and the next page load silently restored the previous
  /// account. The call needs nothing from storage, so clearing it is safe,
  /// and it is bounded by [logoutRevokeBudget].
  Future<void> _onAuthLogoutRequested(
    AuthLogoutRequested event,
    Emitter<AuthState> emit,
  ) async {
    final currentState = state;
    // Never keep an abandoned registration's password past a logout.
    _clearPendingOtp();

    if (currentState is AuthAuthenticated) {
      emit(const AuthLoading());

      final userId = currentState.userId;
      // _clearSession() nulls _currentPhoneNumber — capture it first.
      final phoneNumber = _currentPhoneNumber ?? currentState.phoneNumber ?? '';
      final tokens = currentState.tokens;

      // Stop pushes to this device for the leaving user. Runs first, while
      // the bearer token is still in storage, and is bounded so a slow
      // network never holds up logout.
      await _unregisterPushToken();

      // Revoke the refresh token server-side, in parallel with the local
      // clear below. clearCache: false because the bloc clears credentials
      // itself; the service's deferred clear could wipe a re-login's cache.
      final revocation = _revokeServerSession(
        phoneNumber,
        await _liveTokens(userId: userId, fallback: tokens),
      );

      // Always clear business data to prevent data leakage on shared devices
      if (_isar != null) {
        // SECURITY: Cancel pending payment intents for this user before logout
        // to prevent the next user's session from processing them.
        await _cancelUserPendingIntents(_isar, userId);
        await _clearUserBusinessData(_isar, userId);
      }
      // Sync refusals kept for a listener belong to the leaving user.
      _securityEvents.clearKeptRejections();

      // Clear offline credentials by default (event.clearCache). Hardcoding
      // false left the password hash + tokens on the device after an
      // explicit logout.
      if (event.clearCache) {
        await _offlineCache.clearCredentials(phoneNumber);
      }

      await _clearSession();

      emit(const AuthUnauthenticated());

      await revocation;
    }
  }

  /// The access and refresh tokens to revoke. The interceptor rewrites the
  /// stored slots on every refresh, so they are fresher than the state's
  /// copy from login ([fallback]). They are used only while the stored
  /// current user is still [userId]: on web, localStorage is shared by every
  /// tab, and a stale tab of one user must never revoke the session another
  /// user has signed into since. Otherwise, or when storage cannot be read,
  /// the state's copy is used; it also fills an empty slot.
  Future<({String accessToken, String refreshToken})> _liveTokens({
    required String userId,
    required AuthTokens fallback,
  }) async {
    final stateCopy = (
      accessToken: fallback.accessToken,
      refreshToken: fallback.refreshToken,
    );
    try {
      if (await _secureStorage.read(key: currentUserIdKey) != userId) {
        return stateCopy;
      }
      final access = await _secureStorage.read(key: _activeAccessTokenKey);
      final refresh = await _secureStorage.read(key: _activeRefreshTokenKey);
      return (
        accessToken:
            access != null && access.isNotEmpty ? access : fallback.accessToken,
        refreshToken: refresh != null && refresh.isNotEmpty
            ? refresh
            : fallback.refreshToken,
      );
    } on Object {
      return stateCopy;
    }
  }

  /// Best-effort POST /auth/logout, bounded by [logoutRevokeBudget] from the
  /// moment it is called. Never throws: server-side tokens expire on their
  /// own.
  Future<void> _revokeServerSession(
    String phoneNumber,
    ({String accessToken, String refreshToken}) tokens,
  ) async {
    try {
      await _authService
          .logout(
            phoneNumber: phoneNumber,
            accessToken: tokens.accessToken,
            refreshToken: tokens.refreshToken,
            clearCache: false,
          )
          .timeout(logoutRevokeBudget);
    } on Object catch (e) {
      debugPrint('[AuthBloc] server-side logout skipped: $e');
    }
  }

  /// Best-effort DELETE /device-tokens for this device's FCM token.
  Future<void> _unregisterPushToken() async {
    final provider = _pushTokenProvider;
    if (provider == null) return;
    try {
      await () async {
        final token = await provider();
        if (token != null && token.isNotEmpty) {
          await _authService.unregisterDeviceToken(token);
        }
      }()
          .timeout(pushUnregisterBudget);
    } on Object catch (e) {
      debugPrint('[AuthBloc] push token unregister skipped: $e');
    }
  }

  /// The account was deleted on the server: remove everything this device
  /// holds for it (account-deletion.md, client obligations).
  ///
  /// Reuses the logout wipe and adds what logout keeps: the user's own
  /// listings, trips, buyer requests, payment intents and read markers, the
  /// users row and the offline-login counter. Other accounts on a shared
  /// phone keep their data and queued work, so the database stays open
  /// instead of going through IsarService.clearAllData. No server call: the
  /// server already ended every session and removed the device tokens.
  /// Every step is best effort, so one failure never keeps the user signed
  /// in.
  ///
  /// When [userId] is signed in, the session ends with [AuthAccountWiped]
  /// and the data usage counters go with it (see bindDataUsageToAuth). When
  /// it is not (the answer came after it signed out, or after another
  /// account signed in), only its local data goes: the current session, and
  /// the push token it may now use, are left alone.
  Future<void> _wipeDeletedAccount(
    Emitter<AuthState> emit, {
    required String userId,
    String? phoneNumber,
  }) async {
    final current = state;
    final session = current is AuthAuthenticated && current.userId == userId
        ? current
        : null;
    final signedIn = session != null;
    final phone = session != null
        ? _currentPhoneNumber ?? session.phoneNumber ?? phoneNumber
        : phoneNumber;

    Future<void> step(String what, Future<void> Function() run) async {
      try {
        await run();
      } on Object catch (e) {
        debugPrint('[AuthBloc] account wipe: $what failed: $e');
      }
    }

    if (signedIn) {
      _clearPendingOtp();
      emit(const AuthLoading());
      final deleter = _pushTokenDeleter;
      if (deleter != null) {
        await step(
          'push token',
          () => deleter().timeout(pushUnregisterBudget),
        );
      }
    }
    final isar = _isar;
    if (isar != null) {
      await step('business data', () => _clearUserBusinessData(isar, userId));
      await step('account data', () => _clearAccountData(isar, userId));
    }
    if (phone != null && phone.isNotEmpty) {
      await step('offline cache', () => _offlineCache.forgetAccount(phone));
    }
    await step('read markers', () => _forgetReadMarkers(userId));
    if (!signedIn) {
      await step('data usage', () => _forgetDataUsage(userId));
      return;
    }
    _securityEvents.clearKeptRejections();
    await step('session', _clearSession);

    emit(const AuthAccountWiped());
  }

  Future<void> _onAccountDeleted(
    AuthAccountDeleted event,
    Emitter<AuthState> emit,
  ) =>
      _wipeDeletedAccount(
        emit,
        userId: event.userId,
        phoneNumber: event.phoneNumber,
      );

  /// `410 ACCOUNT_004`: wipe the signed-in account unless its deletion is
  /// still `in_progress` (it can abort, and Profile > Delete account resumes
  /// it). Nothing happens when the stored session belongs to another
  /// account: on web another tab may have signed it in, and the refusal was
  /// about that one.
  Future<void> _onAccountGone(
    AuthAccountGone event,
    Emitter<AuthState> emit,
  ) async {
    if (event.deletionState == deletionInProgressState) return;
    final current = state;
    if (current is! AuthAuthenticated) return;
    if (!await isSignedInAs(current.userId)) return;
    await _wipeDeletedAccount(emit, userId: current.userId);
  }

  /// Confirm the registration OTP; on success the session starts exactly
  /// like a login.
  Future<void> _onVerifyOtp(
    AuthVerifyOtpRequested event,
    Emitter<AuthState> emit,
  ) async {
    final phone = event.phoneNumber ?? _pendingOtpPhone;
    if (phone == null || phone.isEmpty) {
      emit(
        const AuthError(
          errorCode: AuthErrorCode.otpInvalidOrExpired,
          errorMessage: 'No verification in progress. Please register again.',
        ),
      );
      return;
    }
    _cancelled = false;
    emit(const AuthLoading());

    try {
      final result = await _authService.verifyOtp(
        phoneNumber: phone,
        otpCode: event.otpCode,
        password: phone == _pendingOtpPhone ? _pendingOtpPassword : null,
        drivingLicensePath:
            phone == _pendingOtpPhone ? _pendingDrivingLicensePath : null,
        buyerType: phone == _pendingOtpPhone ? _pendingBuyerType : null,
        companyName: phone == _pendingOtpPhone ? _pendingCompanyName : null,
      );
      if (_cancelled) return;

      if (!result.isSuccess) {
        emit(_errorFrom(result, 'Verification failed'));
        return;
      }
      if (result.role == null || result.tokens == null) {
        emit(
          const AuthError(
            errorCode: AuthErrorCode.internalServerError,
            errorMessage:
                'Could not determine your account role. Please log in again.',
          ),
        );
        return;
      }

      _clearPendingOtp();
      await _saveSession(phone, result.userId!);
      await _saveActiveTokens(result.tokens!);
      emit(
        AuthAuthenticated(
          userId: result.userId!,
          tokens: result.tokens!,
          role: result.role!,
          userName: result.userName ?? '',
          phoneNumber: phone,
          woreda: result.woreda,
        ),
      );
    } on Object catch (e) {
      if (_cancelled) return;
      debugPrint('[AuthBloc] OTP verification failed unexpectedly: $e');
      emit(
        const AuthError(
          errorCode: AuthErrorCode.internalServerError,
          errorMessage: 'Something went wrong. Please try again.',
        ),
      );
    }
  }

  /// Send a fresh OTP and re-emit the OTP state with the new expiry.
  Future<void> _onResendOtp(
    AuthResendOtpRequested event,
    Emitter<AuthState> emit,
  ) async {
    final phone = event.phoneNumber ?? _pendingOtpPhone;
    if (phone == null || phone.isEmpty) return;
    emit(const AuthLoading());
    try {
      final result = await _authService.resendOtp(phoneNumber: phone);
      if (result.isSuccess) {
        emit(
          AuthOtpVerificationRequired(
            phoneNumber: phone,
            expiresAt: result.otpExpiresAt,
            resentAt: DateTime.now().toUtc(),
          ),
        );
      } else {
        emit(_errorFrom(result, 'Failed to resend the code'));
      }
    } on Object catch (e) {
      debugPrint('[AuthBloc] OTP resend failed unexpectedly: $e');
      emit(
        const AuthError(
          errorCode: AuthErrorCode.internalServerError,
          errorMessage: 'Failed to resend the code. Please try again.',
        ),
      );
    }
  }

  /// Step-up re-authentication: keep the session and all local data, only
  /// swap in the fresh tokens (whose auth_time satisfies the server).
  Future<void> _onStepUpSucceeded(
    AuthStepUpSucceeded event,
    Emitter<AuthState> emit,
  ) async {
    final current = state;
    if (current is! AuthAuthenticated) return;
    await _saveActiveTokens(event.tokens);
    emit(current.withTokens(event.tokens));
  }

  /// The refresh token was rejected: the interceptor already wiped the
  /// active tokens, so sign out locally instead of leaving the UI "logged
  /// in" with no credentials. The offline cache for this phone is cleared
  /// (its tokens are dead), but business data and the sync queue are kept
  /// so unsynced work goes up after the user signs in again.
  Future<void> _onSessionExpired(
    AuthSessionExpired event,
    Emitter<AuthState> emit,
  ) async {
    final current = state;
    if (current is! AuthAuthenticated) return;
    final phone = _currentPhoneNumber ?? current.phoneNumber;
    if (phone != null && phone.isNotEmpty) {
      try {
        await _offlineCache.clearCredentials(phone);
      } on Object catch (e) {
        debugPrint('[AuthBloc] clearing expired session cache failed: $e');
      }
    }
    await _clearSession();
    emit(const AuthUnauthenticated());
  }

  /// Handle forgot password request (sends verification code).
  Future<void> _onForgotPassword(
    AuthForgotPasswordRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());
    try {
      final result = await _authService.forgotPassword(
        phoneNumber: event.phoneNumber,
      );
      if (result.isSuccess) {
        emit(AuthPasswordResetCodeSent(phoneNumber: event.phoneNumber));
      } else {
        emit(
          AuthError(
            errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
            errorMessage: result.errorMessage ??
                result.errorCode?.userMessage ??
                'Failed to send reset code',
            canRetry: result.errorCode?.isRecoverable ?? true,
            retryAfterSeconds: result.retryAfterSeconds,
          ),
        );
      }
    } on Object catch (e) {
      debugPrint('[AuthBloc] ForgotPassword failed unexpectedly: $e');
      emit(
        const AuthError(
          errorCode: AuthErrorCode.internalServerError,
          errorMessage: 'Failed to send reset code. Please try again.',
        ),
      );
    }
  }

  /// Handle password reset with verification code.
  Future<void> _onResetPassword(
    AuthResetPasswordRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());
    try {
      final result = await _authService.resetPassword(
        phoneNumber: event.phoneNumber,
        confirmationCode: event.confirmationCode,
        newPassword: event.newPassword,
      );
      if (result.isSuccess) {
        emit(const AuthPasswordResetSuccess());
      } else {
        emit(
          AuthError(
            errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
            errorMessage: result.errorMessage ??
                result.errorCode?.userMessage ??
                'Failed to reset password',
            canRetry: result.errorCode?.isRecoverable ?? true,
            retryAfterSeconds: result.retryAfterSeconds,
          ),
        );
      }
    } on Object catch (e) {
      debugPrint('[AuthBloc] ResetPassword failed unexpectedly: $e');
      emit(
        const AuthError(
          errorCode: AuthErrorCode.internalServerError,
          errorMessage: 'Failed to reset password. Please try again.',
        ),
      );
    }
  }

  /// Cancel pending payment intents belonging to [userId].
  ///
  /// Only cancels intents where [buyerId] matches the logging-out user,
  /// preserving other users' queued payments on shared devices.
  Future<void> _cancelUserPendingIntents(Isar isar, String userId) async {
    if (kIsWeb) return;
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
    if (kIsWeb) return;
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

      // Delete orders where user is a participant
      final buyerOrders =
          await isar.orders.filter().buyerIdEqualTo(userId).findAll();
      final farmerOrders =
          await isar.orders.filter().farmerIdEqualTo(userId).findAll();
      final orderIds = <int>{};
      for (final o in [...buyerOrders, ...farmerOrders]) {
        orderIds.add(o.id);
      }
      await isar.orders.deleteAll(orderIds.toList());

      // Delete chat messages the user sent or received.
      final sent =
          await isar.messages.filter().senderIdEqualTo(userId).findAll();
      final received =
          await isar.messages.filter().recipientIdEqualTo(userId).findAll();
      final messageIds = <int>{
        for (final m in [...sent, ...received]) m.id,
      };
      await isar.messages.deleteAll(messageIds.toList());

      // Delete ratings the user gave or received.
      final given =
          await isar.ratings.filter().raterIdEqualTo(userId).findAll();
      final got = await isar.ratings.filter().rateeIdEqualTo(userId).findAll();
      final ratingIds = <int>{
        for (final r in [...given, ...got]) r.id,
      };
      await isar.ratings.deleteAll(ratingIds.toList());

      // Delete the user's queued sync operations (idempotency keys are
      // `{userId}:...`) so the next person on a shared device can never
      // send them, matching the payment-intent cancellation above.
      final ops = await isar.syncOperations
          .filter()
          .idempotencyKeyStartsWith('$userId:')
          .findAll();
      await isar.syncOperations.deleteAll(ops.map((o) => o.id).toList());
    });
  }

  /// Delete what a deleted account leaves after [_clearUserBusinessData]:
  /// its own listings, trips, buyer requests and payment intents, the orders
  /// it carried as trucker, its read markers and its users row. Listings of
  /// other users stay (public marketplace data).
  Future<void> _clearAccountData(Isar isar, String userId) async {
    if (kIsWeb) return;
    await isar.writeTxn(() async {
      final listings =
          await isar.cropListings.filter().farmerIdEqualTo(userId).findAll();
      await isar.cropListings.deleteAll(listings.map((l) => l.id).toList());

      final trips =
          await isar.trips.filter().truckerIdEqualTo(userId).findAll();
      await isar.trips.deleteAll(trips.map((t) => t.id).toList());

      final intents =
          await isar.buyerIntents.filter().buyerIdEqualTo(userId).findAll();
      await isar.buyerIntents.deleteAll(intents.map((i) => i.id).toList());

      final asBuyer =
          await isar.paymentIntents.filter().buyerIdEqualTo(userId).findAll();
      final asFarmer =
          await isar.paymentIntents.filter().farmerIdEqualTo(userId).findAll();
      final paymentIds = <int>{
        for (final p in [...asBuyer, ...asFarmer]) p.id,
      };
      await isar.paymentIntents.deleteAll(paymentIds.toList());

      final hauled =
          await isar.orders.filter().truckerIdEqualTo(userId).findAll();
      await isar.orders.deleteAll(hauled.map((o) => o.id).toList());

      // `{contextType}:{contextId}:{userA}:{userB}`
      final reads = await isar.conversationReads.where().findAll();
      await isar.conversationReads.deleteAll([
        for (final r in reads)
          if (r.conversationId.split(':').skip(2).contains(userId)) r.id,
      ]);

      final users = await isar.users.filter().userIdEqualTo(userId).findAll();
      await isar.users.deleteAll(users.map((u) => u.id).toList());
    });
  }
}
