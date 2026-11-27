import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const auth = vi.hoisted(() => ({ logout: vi.fn(), getIdToken: () => "id-token" }));
vi.mock("./auth", () => auth);

import { api, ApiError } from "./api";

function respond(status: number, body: unknown) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => new Response(typeof body === "string" ? body : JSON.stringify(body), { status })),
  );
}

describe("api error handling", () => {
  beforeEach(() => {
    auth.logout.mockReset();
    window.history.replaceState(null, "", "/fraud");
  });
  afterEach(() => vi.unstubAllGlobals());

  it("signs out on a 401", async () => {
    respond(401, { message: "Unauthorized" });
    await expect(api.get("/admin/x")).rejects.toBeInstanceOf(ApiError);
    expect(auth.logout).toHaveBeenCalledTimes(1);
  });

  it("signs out on a 403 without a server error code (authorizer refusal)", async () => {
    respond(403, { message: "Forbidden" });
    await expect(api.get("/admin/x")).rejects.toBeInstanceOf(ApiError);
    expect(auth.logout).toHaveBeenCalledTimes(1);
  });

  it("keeps the session on a 403 that carries a server error code", async () => {
    respond(403, { error: { code: "ESCROW_029", message: "The farmer is not verified." } });
    const err = await api.post("/escrow/release", {}).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe("ESCROW_029");
    expect((err as ApiError).status).toBe(403);
    expect(auth.logout).not.toHaveBeenCalled();
  });
});
