import {
  CognitoUserPool,
  CognitoUser,
  AuthenticationDetails,
  CognitoUserSession,
  type IAuthenticationCallback,
} from "amazon-cognito-identity-js";

function getUserPool(): CognitoUserPool {
  // AdminClient belongs to the dedicated AdminUserPool (infra api-stack.ts),
  // not the main user pool, so pair it with the admin pool ID.
  const poolId = import.meta.env.VITE_COGNITO_ADMIN_USER_POOL_ID || "";
  const clientId = import.meta.env.VITE_COGNITO_ADMIN_CLIENT_ID || "";
  if (!poolId || !clientId) {
    throw new Error(
      "Missing Cognito config: set VITE_COGNITO_ADMIN_USER_POOL_ID and VITE_COGNITO_ADMIN_CLIENT_ID in .env",
    );
  }
  return new CognitoUserPool({ UserPoolId: poolId, ClientId: clientId });
}

let currentSession: CognitoUserSession | null = null;

export function getAccessToken(): string | null {
  return currentSession?.getAccessToken().getJwtToken() ?? null;
}

export function getIdToken(): string | null {
  return currentSession?.getIdToken().getJwtToken() ?? null;
}

export function isAdmin(): boolean {
  if (!currentSession) return false;
  const payload = currentSession.getIdToken().decodePayload();
  const groups: string[] = payload["cognito:groups"] || [];
  return groups.includes("admin");
}

export function getCurrentUser(): { phone: string; groups: string[] } | null {
  if (!currentSession) return null;
  const payload = currentSession.getIdToken().decodePayload();
  return {
    phone: payload.phone_number || "",
    groups: payload["cognito:groups"] || [],
  };
}

/** Map Cognito SDK error codes to safe i18n keys. Never expose raw messages. */
function mapCognitoError(code?: string): string {
  switch (code) {
    case "NotAuthorizedException":
      return "invalidCredentials";
    case "UserNotFoundException":
      return "invalidCredentials"; // Don't reveal if user exists
    case "UserNotConfirmedException":
      return "userNotConfirmed";
    case "PasswordResetRequiredException":
      return "passwordResetRequired";
    case "TooManyRequestsException":
    case "LimitExceededException":
      return "rateLimitExceeded";
    case "InvalidParameterException":
      return "invalidInput";
    case "NetworkError":
      return "networkError";
    default:
      // ResourceNotFoundException, InternalErrorException, etc.
      // Never expose raw AWS error messages to the UI
      return "serviceUnavailable";
  }
}

/**
 * Error mapping for the challenge steps (MFA code, MFA setup, new password).
 * During a challenge NotAuthorizedException means the Cognito auth session
 * expired or was used up, so the user has to start over from the password.
 */
function mapChallengeError(code?: string): string {
  switch (code) {
    case "CodeMismatchException":
    case "EnableSoftwareTokenMFAException":
      return "invalidCode";
    case "ExpiredCodeException":
      return "codeExpired";
    case "NotAuthorizedException":
      return "sessionExpired";
    case "InvalidPasswordException":
      return "passwordPolicy";
    default:
      return mapCognitoError(code);
  }
}

export const TOTP_ISSUER = "WeredaConnect Admin";

export type MfaType = "totp" | "sms";

export type LoginResult =
  | { status: "success" }
  | { status: "totpRequired"; mfaType: MfaType }
  | { status: "mfaSetupRequired"; secretCode: string; otpauthUrl: string }
  | { status: "newPasswordRequired" }
  | { status: "error"; error: string };

/** Build the otpauth:// URL authenticator apps read from a QR code or link. */
export function buildOtpauthUrl(secretCode: string, accountName: string): string {
  const label = `${encodeURIComponent(TOTP_ISSUER)}:${encodeURIComponent(accountName)}`;
  // encodeURIComponent (not URLSearchParams) so spaces become %20: some
  // authenticator apps show a "+" from form encoding literally.
  const query = [
    ["secret", secretCode],
    ["issuer", TOTP_ISSUER],
    ["algorithm", "SHA1"],
    ["digits", "6"],
    ["period", "30"],
  ]
    .map(([k, v]) => `${k}=${encodeURIComponent(v)}`)
    .join("&");
  return `otpauth://totp/${label}?${query}`;
}

/**
 * The CognitoUser that is part-way through a challenge. It holds the Cognito
 * auth session between the password step and the code step, so it lives in
 * module state (memory only, never persisted).
 */
let pendingUser: CognitoUser | null = null;
let pendingPhone = "";
let pendingMfaType: "SOFTWARE_TOKEN_MFA" | "SMS_MFA" = "SOFTWARE_TOKEN_MFA";

function clearPending(): void {
  pendingUser = null;
  pendingPhone = "";
  pendingMfaType = "SOFTWARE_TOKEN_MFA";
}

/** Returns true when a challenge step (code, setup, new password) is in flight. */
export function hasPendingChallenge(): boolean {
  return pendingUser !== null;
}

/** Abandon an in-flight challenge, for example when the user goes back. */
export function cancelChallenge(): void {
  clearPending();
}

/** Admin group gate, shared by every path that ends in a session. */
function finishSignIn(
  user: CognitoUser,
  session: CognitoUserSession,
): LoginResult {
  currentSession = session;
  clearPending();
  if (!isAdmin()) {
    currentSession = null;
    user.signOut();
    return { status: "error", error: "notAdmin" };
  }
  return { status: "success" };
}

/**
 * Callbacks for authenticateUser and every follow-up call that can land on
 * another challenge (a new password can be followed by MFA setup, and so on).
 */
function authCallbacks(
  user: CognitoUser,
  resolve: (r: LoginResult) => void,
  mapError: (code?: string) => string,
): IAuthenticationCallback {
  return {
    onSuccess: (session) => resolve(finishSignIn(user, session)),
    onFailure: (err) => {
      const error = mapError(err?.code);
      if (error === "sessionExpired") clearPending();
      resolve({ status: "error", error });
    },
    totpRequired: () => {
      pendingUser = user;
      pendingMfaType = "SOFTWARE_TOKEN_MFA";
      resolve({ status: "totpRequired", mfaType: "totp" });
    },
    mfaRequired: () => {
      pendingUser = user;
      pendingMfaType = "SMS_MFA";
      resolve({ status: "totpRequired", mfaType: "sms" });
    },
    selectMFAType: () => {
      // Both factors enabled: admins use the authenticator app.
      pendingUser = user;
      user.sendMFASelectionAnswer("SOFTWARE_TOKEN_MFA", {
        onSuccess: (session) => resolve(finishSignIn(user, session)),
        onFailure: (err) =>
          resolve({ status: "error", error: mapChallengeError(err?.code) }),
        totpRequired: () => {
          pendingMfaType = "SOFTWARE_TOKEN_MFA";
          resolve({ status: "totpRequired", mfaType: "totp" });
        },
        mfaRequired: () => {
          pendingMfaType = "SMS_MFA";
          resolve({ status: "totpRequired", mfaType: "sms" });
        },
      });
    },
    mfaSetup: () => {
      pendingUser = user;
      user.associateSoftwareToken({
        associateSecretCode: (secretCode) =>
          resolve({
            status: "mfaSetupRequired",
            secretCode,
            otpauthUrl: buildOtpauthUrl(secretCode, pendingPhone),
          }),
        onFailure: (err) => {
          clearPending();
          resolve({ status: "error", error: mapChallengeError(err?.code) });
        },
      });
    },
    newPasswordRequired: () => {
      pendingUser = user;
      resolve({ status: "newPasswordRequired" });
    },
    customChallenge: () => {
      // The admin pool has no custom auth flow; refuse rather than hang.
      clearPending();
      resolve({ status: "error", error: "serviceUnavailable" });
    },
  };
}

export async function login(
  phone: string,
  password: string,
): Promise<LoginResult> {
  return new Promise((resolve) => {
    let pool;
    try {
      pool = getUserPool();
    } catch {
      resolve({ status: "error", error: "serviceUnavailable" });
      return;
    }

    clearPending();
    const user = new CognitoUser({
      Username: phone,
      Pool: pool,
    });
    pendingPhone = phone;

    // Use USER_PASSWORD_AUTH instead of default SRP.
    // Admin users are created via CLI (admin-create-user) which doesn't
    // generate proper SRP verifiers, causing SRP auth to fail.
    user.setAuthenticationFlowType("USER_PASSWORD_AUTH");

    const authDetails = new AuthenticationDetails({
      Username: phone,
      Password: password,
    });

    user.authenticateUser(authDetails, authCallbacks(user, resolve, mapCognitoError));
  });
}

const SIX_DIGITS = /^\d{6}$/;

/** Answer a SOFTWARE_TOKEN_MFA (or SMS_MFA) challenge with a 6-digit code. */
export async function submitTotp(code: string): Promise<LoginResult> {
  const user = pendingUser;
  if (!user) return { status: "error", error: "sessionExpired" };
  const trimmed = code.trim();
  if (!SIX_DIGITS.test(trimmed)) return { status: "error", error: "invalidCode" };

  return new Promise((resolve) => {
    const callbacks = authCallbacks(user, resolve, mapChallengeError);
    user.sendMFACode(
      trimmed,
      { onSuccess: callbacks.onSuccess, onFailure: callbacks.onFailure },
      pendingMfaType,
    );
  });
}

/**
 * Confirm the first TOTP code from a freshly associated authenticator.
 * During an MFA_SETUP challenge the SDK verifies the token and then answers
 * the challenge, so onSuccess carries the signed-in session.
 */
export async function completeMfaSetup(
  code: string,
  deviceName: string,
): Promise<LoginResult> {
  const user = pendingUser;
  if (!user) return { status: "error", error: "sessionExpired" };
  const trimmed = code.trim();
  if (!SIX_DIGITS.test(trimmed)) return { status: "error", error: "invalidCode" };

  return new Promise((resolve) => {
    user.verifySoftwareToken(trimmed, deviceName.trim() || TOTP_ISSUER, {
      onSuccess: (session) => resolve(finishSignIn(user, session)),
      onFailure: (err) => {
        const error = mapChallengeError((err as { code?: string })?.code);
        if (error === "sessionExpired") clearPending();
        resolve({ status: "error", error });
      },
    });
  });
}

/** Answer NEW_PASSWORD_REQUIRED for an admin created with a temporary password. */
export async function completeNewPassword(
  newPassword: string,
): Promise<LoginResult> {
  const user = pendingUser;
  if (!user) return { status: "error", error: "sessionExpired" };
  if (!newPassword) return { status: "error", error: "passwordPolicy" };

  return new Promise((resolve) => {
    user.completeNewPasswordChallenge(
      newPassword,
      {},
      authCallbacks(user, resolve, mapChallengeError),
    );
  });
}

export function logout(): void {
  try {
    const user = getUserPool().getCurrentUser();
    if (user) user.signOut();
  } catch {
    // Cognito not configured, so just clear local state
  }
  clearPending();
  currentSession = null;
}

export function restoreSession(): Promise<boolean> {
  return new Promise((resolve) => {
    let user;
    try {
      user = getUserPool().getCurrentUser();
    } catch {
      resolve(false);
      return;
    }
    if (!user) {
      resolve(false);
      return;
    }

    user.getSession(
      (err: Error | null, session: CognitoUserSession | null) => {
        if (err || !session || !session.isValid()) {
          resolve(false);
          return;
        }
        currentSession = session;
        resolve(isAdmin());
      },
    );
  });
}
