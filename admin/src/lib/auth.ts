import {
  CognitoUserPool,
  CognitoUser,
  AuthenticationDetails,
  CognitoUserSession,
} from "amazon-cognito-identity-js";

function getUserPool(): CognitoUserPool {
  // AdminClient belongs to the dedicated AdminUserPool (infra api-stack.ts),
  // not the main user pool — pair it with the admin pool ID.
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

export async function login(
  phone: string,
  password: string,
): Promise<{ success: boolean; error?: string }> {
  return new Promise((resolve) => {
    let pool;
    try {
      pool = getUserPool();
    } catch {
      resolve({ success: false, error: "serviceUnavailable" });
      return;
    }

    const user = new CognitoUser({
      Username: phone,
      Pool: pool,
    });

    // Use USER_PASSWORD_AUTH instead of default SRP.
    // Admin users are created via CLI (admin-create-user) which doesn't
    // generate proper SRP verifiers, causing SRP auth to fail.
    user.setAuthenticationFlowType("USER_PASSWORD_AUTH");

    const authDetails = new AuthenticationDetails({
      Username: phone,
      Password: password,
    });

    user.authenticateUser(authDetails, {
      onSuccess: (session) => {
        currentSession = session;
        if (!isAdmin()) {
          currentSession = null;
          user.signOut();
          resolve({ success: false, error: "notAdmin" });
          return;
        }
        resolve({ success: true });
      },
      onFailure: (err) => {
        const errorKey = mapCognitoError(err.code);
        resolve({ success: false, error: errorKey });
      },
    });
  });
}

export function logout(): void {
  try {
    const user = getUserPool().getCurrentUser();
    if (user) user.signOut();
  } catch {
    // Cognito not configured — just clear local state
  }
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
