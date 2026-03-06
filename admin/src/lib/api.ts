import { getAccessToken, logout } from "./auth";

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
  const token = getAccessToken();
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
    // Redirect to login on auth failures
    if (response.status === 401 || response.status === 403) {
      logout();
      window.location.href = "/";
    }

    const text = await response.text().catch(() => "");
    const parsed = parseErrorResponse(text);
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
