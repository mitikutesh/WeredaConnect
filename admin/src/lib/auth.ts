import {
  CognitoUserPool,
  CognitoUser,
  AuthenticationDetails,
  CognitoUserSession,
} from "amazon-cognito-identity-js";

function getUserPool(): CognitoUserPool {
  const poolId = import.meta.env.VITE_COGNITO_USER_POOL_ID || "";
  const clientId = import.meta.env.VITE_COGNITO_ADMIN_CLIENT_ID || "";
  if (!poolId || !clientId) {
    throw new Error(
      "Missing Cognito config: set VITE_COGNITO_USER_POOL_ID and VITE_COGNITO_ADMIN_CLIENT_ID in .env",
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

export async function login(
  phone: string,
  password: string,
): Promise<{ success: boolean; error?: string }> {
  return new Promise((resolve) => {
    const user = new CognitoUser({
      Username: phone,
      Pool: getUserPool(),
    });

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
        resolve({
          success: false,
          error: err.code === "NotAuthorizedException"
            ? "invalidCredentials"
            : err.message,
        });
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
