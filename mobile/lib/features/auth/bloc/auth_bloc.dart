import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

/// BLoC for managing authentication state.
///
/// This BLoC handles:
/// - User registration with OTP verification
/// - Online/offline login
/// - Token refresh
/// - Logout
/// - Session persistence across app restarts
///
/// State transitions:
/// - AuthInitial → AuthLoading → AuthAuthenticated/AuthUnauthenticated/AuthError
/// - AuthUnauthenticated → AuthLoading → AuthOtpVerificationRequired (after registration)
/// - AuthOtpVerificationRequired → AuthLoading → AuthAuthenticated/AuthError
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
  // Temporary storage for registration data (cleared after OTP verification)
  String? _pendingName;
  UserRole? _pendingRole;
  String? _pendingWoreda;
  String? _pendingPassword;
  // SECURITY: _pendingPassword is held temporarily during registration->OTP flow only.
  // It is cleared as soon as OTP verification completes (success or failure),
  // on logout, and when the app is paused (via AppLifecycleObserver).

  static const _secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );
  static const _currentUserPhoneKey = 'current_user_phone';

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
    on<AuthVerifyOtpRequested>(_onAuthVerifyOtpRequested);
    on<AuthResendOtpRequested>(_onAuthResendOtpRequested);
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

  /// Clear the current session from secure storage.
  Future<void> _clearSession() async {
    _currentPhoneNumber = null;
    await _secureStorage.delete(key: _currentUserPhoneKey);
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
      emit(
        AuthAuthenticated(
          userId: user.userId,
          tokens: tokens,
          isOfflineAuth: tokens.isExpired,
          role: user.role,
        ),
      );
    } on Exception {
      emit(const AuthUnauthenticated());
    }
  }

  /// Handle user registration.
  Future<void> _onAuthRegisterRequested(
    AuthRegisterRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    // Store phone number for session tracking (not sensitive)
    _currentPhoneNumber = event.phoneNumber;
    // Store registration data temporarily for use after OTP verification
    _pendingName = event.name;
    _pendingRole = event.role;
    _pendingWoreda = event.woreda;
    _pendingPassword = event.password;

    final result = await _authService.register(
      phoneNumber: event.phoneNumber,
      password: event.password,
      name: event.name,
      role: event.role,
      woreda: event.woreda,
      preferredLanguage: event.preferredLanguage,
    );

    if (result.isSuccess) {
      emit(
        AuthOtpVerificationRequired(
          phoneNumber: event.phoneNumber,
          userId: result.userId!,
        ),
      );
    } else {
      // Clear pending data on failure
      _clearPendingRegistrationData();
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

  /// Clear sensitive in-memory data. Called when app goes to background.
  /// This is a direct method call (not an event) because it must be synchronous
  /// and must not trigger state changes.
  void clearSensitiveData() {
    _clearPendingRegistrationData();
  }

  /// Clear pending registration data after OTP verification or on error.
  void _clearPendingRegistrationData() {
    _pendingName = null;
    _pendingRole = null;
    _pendingWoreda = null;
    _pendingPassword = null;
  }

  /// Handle OTP verification.
  Future<void> _onAuthVerifyOtpRequested(
    AuthVerifyOtpRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    // Verify OTP
    final result = await _authService.verifyOtp(
      phoneNumber: event.phoneNumber,
      otp: event.otp,
    );

    if (result.isSuccess) {
      // Cache full credentials if we have pending registration data
      if (_pendingName != null &&
          _pendingRole != null &&
          _pendingWoreda != null &&
          _pendingPassword != null) {
        await _offlineCache.cacheCredentials(
          phoneNumber: event.phoneNumber,
          password: _pendingPassword!,
          userId: result.userId!,
          role: _pendingRole!,
          name: _pendingName!,
          woreda: _pendingWoreda!,
          tokens: result.tokens!,
        );
        // Clear pending data after successful caching
        _clearPendingRegistrationData();
      } else {
        // Fallback: just cache tokens if no pending data
        if (result.tokens != null) {
          await _offlineCache.updateTokens(event.phoneNumber, result.tokens!);
        }
      }

      await _saveSession(event.phoneNumber);

      // Use pending role from registration, or fetch from Isar
      var role = _pendingRole;
      if (role == null) {
        final user = await _offlineCache.getCachedUser(event.phoneNumber);
        role = user?.role ?? UserRole.farmer;
      }

      emit(
        AuthAuthenticated(
          userId: result.userId!,
          tokens: result.tokens!,
          role: role,
        ),
      );
    } else {
      emit(
        AuthError(
          errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
          errorMessage: result.errorMessage ??
              result.errorCode?.userMessage ??
              'OTP verification failed',
        ),
      );
    }
  }

  /// Handle OTP resend request.
  ///
  /// Re-calls the register endpoint to trigger a new SMS OTP.
  /// Uses the pending registration data stored from the original registration.
  Future<void> _onAuthResendOtpRequested(
    AuthResendOtpRequested event,
    Emitter<AuthState> emit,
  ) async {
    // Only resend if we have pending registration data
    if (_pendingPassword == null ||
        _pendingRole == null ||
        _pendingWoreda == null) {
      return;
    }

    final result = await _authService.register(
      phoneNumber: event.phoneNumber,
      password: _pendingPassword!,
      name: _pendingName ?? '',
      role: _pendingRole!,
      woreda: _pendingWoreda!,
    );

    if (!result.isSuccess) {
      emit(
        AuthError(
          errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
          errorMessage: result.errorMessage ??
              result.errorCode?.userMessage ??
              'Failed to resend OTP',
        ),
      );
      // Re-emit OTP state so OTP screen stays visible
      emit(
        AuthOtpVerificationRequired(
          phoneNumber: event.phoneNumber,
          userId: result.userId ?? '',
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

      final user = await _offlineCache.getCachedUser(event.phoneNumber);

      emit(
        AuthAuthenticated(
          userId: result.userId!,
          tokens: result.tokens!,
          isOfflineAuth: result.isOfflineAuth,
          role: user?.role ?? UserRole.farmer,
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

      final user = await _offlineCache.getCachedUser(event.phoneNumber);

      emit(
        AuthAuthenticated(
          userId: result.userId!,
          tokens: result.tokens!,
          role: user?.role ?? UserRole.farmer,
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

      final user = await _offlineCache.getCachedUser(event.phoneNumber);

      emit(
        AuthAuthenticated(
          userId: result.userId!,
          tokens: result.tokens!,
          isOfflineAuth: true,
          role: user?.role ?? UserRole.farmer,
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
  /// When [clearCache] is true, also clears user-specific business data
  /// from the local Isar database (crop listings, deliveries, payments)
  /// to prevent data leakage on shared devices.
  Future<void> _onAuthLogoutRequested(
    AuthLogoutRequested event,
    Emitter<AuthState> emit,
  ) async {
    final currentState = state;
    _clearPendingRegistrationData();

    if (currentState is AuthAuthenticated) {
      emit(const AuthLoading());

      final userId = currentState.userId;

      await _authService.logout(
        phoneNumber: _currentPhoneNumber ?? '',
        accessToken: currentState.tokens.accessToken,
        clearCache: event.clearCache,
      );

      if (event.clearCache && _isar != null) {
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

  /// Delete business data belonging to [userId] from the local cache.
  ///
  /// Clears: crop listings, deliveries, payments where user is a participant.
  /// Keeps other users' data intact for multi-account devices.
  Future<void> _clearUserBusinessData(Isar isar, String userId) async {
    await isar.writeTxn(() async {
      // Delete user's crop listings
      final listings =
          await isar.cropListings.filter().farmerIdEqualTo(userId).findAll();
      await isar.cropListings.deleteAll(listings.map((l) => l.id).toList());

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
