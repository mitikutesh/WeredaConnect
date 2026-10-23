import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';

/// Base class for authentication states.
abstract class AuthState extends Equatable {
  const AuthState();

  @override
  List<Object?> get props => [];
}

/// Initial state (before checking authentication status).
class AuthInitial extends AuthState {
  const AuthInitial();
}

/// Loading state (during authentication operations).
class AuthLoading extends AuthState {
  const AuthLoading();
}

/// Authenticated state (user is logged in).
class AuthAuthenticated extends AuthState {
  final String userId;
  final AuthTokens tokens;
  final bool isOfflineAuth;
  final UserRole role;
  final String userName;
  final String? phoneNumber;
  final String? woreda;

  /// True when the session was restored from the cache on app start
  /// (auto-login) rather than by entering a password. The app lock prompts
  /// for device authentication only in this case.
  final bool isRestoredSession;

  const AuthAuthenticated({
    required this.userId,
    required this.tokens,
    required this.role,
    this.isOfflineAuth = false,
    this.userName = '',
    this.phoneNumber,
    this.woreda,
    this.isRestoredSession = false,
  });

  /// Same session with fresh tokens (step-up re-authentication).
  AuthAuthenticated withTokens(AuthTokens newTokens) => AuthAuthenticated(
        userId: userId,
        tokens: newTokens,
        role: role,
        userName: userName,
        phoneNumber: phoneNumber,
        woreda: woreda,
        isRestoredSession: isRestoredSession,
      );

  @override
  List<Object?> get props => [
        userId,
        tokens,
        isOfflineAuth,
        role,
        userName,
        phoneNumber,
        woreda,
        isRestoredSession,
      ];
}

/// Registration (or a login for an unconfirmed account) needs the SMS OTP
/// sent to [phoneNumber]. No tokens exist yet.
class AuthOtpVerificationRequired extends AuthState {
  final String phoneNumber;

  /// When the current code expires, if the server said.
  final DateTime? expiresAt;

  /// Set when this state follows a successful resend, so a repeated state
  /// with the same expiry still counts as a change.
  final DateTime? resentAt;

  const AuthOtpVerificationRequired({
    required this.phoneNumber,
    this.expiresAt,
    this.resentAt,
  });

  @override
  List<Object?> get props => [phoneNumber, expiresAt, resentAt];
}

/// Unauthenticated state (user is not logged in).
class AuthUnauthenticated extends AuthState {
  const AuthUnauthenticated();
}

/// Signed out because the account was deleted: the device no longer holds
/// its data. Still an [AuthUnauthenticated] for every screen and router.
class AuthAccountWiped extends AuthUnauthenticated {
  const AuthAccountWiped();
}

/// Error state (authentication operation failed).
class AuthError extends AuthState {
  final String errorCode;
  final String errorMessage;
  final bool canRetry;
  final int? retryAfterSeconds;

  const AuthError({
    required this.errorCode,
    required this.errorMessage,
    this.canRetry = true,
    this.retryAfterSeconds,
  });

  @override
  List<Object?> get props =>
      [errorCode, errorMessage, canRetry, retryAfterSeconds];
}

/// State when password reset code has been sent.
class AuthPasswordResetCodeSent extends AuthState {
  final String phoneNumber;

  const AuthPasswordResetCodeSent({required this.phoneNumber});

  @override
  List<Object?> get props => [phoneNumber];
}

/// State when password has been successfully reset.
class AuthPasswordResetSuccess extends AuthState {
  const AuthPasswordResetSuccess();
}
