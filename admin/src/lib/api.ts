import { getIdToken, logout } from "./auth";

const BASE_URL = import.meta.env.VITE_API_BASE_URL || "";

export class ApiError extends Error {
  status: number;
  code: string | undefined;
  field: string | undefined;
  constructor(status: number, message: string, code?: string, field?: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.field = field;
  }
}

/** Parse the server error envelope: { error: { code, message, field } } */
function parseErrorResponse(text: string): {
  code?: string;
  message: string;
  field?: string;
} {
  try {
    const json = JSON.parse(text);
    if (json?.error?.code) {
      return {
        code: json.error.code,
        message: json.error.message || "An unexpected error occurred",
        field: json.error.field,
      };
    }
  } catch {
    // Not JSON — return raw text (but never show it to the user)
  }
  return { message: "An unexpected error occurred" };
}

async function request<T>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  // Admin routes are guarded by an HttpUserPoolAuthorizer scoped to the admin
  // client, which enforces aud == adminClientId. Cognito access tokens carry
  // no `aud` claim, so they 401 at API Gateway — send the ID token (carries
  // aud + cognito:groups). See memory: api-auth-uses-id-token.
  const token = getIdToken();
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
    ...(options.headers as Record<string, string> || {}),
  };

  let response: Response;
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      ...options,
      headers,
    });
  } catch {
    throw new ApiError(0, "Unable to reach server", "NETWORK_ERROR");
  }

  if (!response.ok) {
    const text = await response.text().catch(() => "");
    const parsed = parseErrorResponse(text);

    // Redirect to login on auth failures. A 403 that carries a server error
    // code (e.g. ESCROW_029 on a release) is a business refusal for a valid
    // session: show it instead of signing the admin out.
    if (response.status === 401 || (response.status === 403 && !parsed.code)) {
      logout();
      window.location.href = "/";
    }

    throw new ApiError(response.status, parsed.message, parsed.code, parsed.field);
  }

  return response.json();
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, {
      method: "POST",
      body: body ? JSON.stringify(body) : undefined,
    }),
};
