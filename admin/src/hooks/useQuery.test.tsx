import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { useFraudSignals, useHeldEscrows, useManualPayoutClaim, useSettleHeldEscrow } from "./useQuery";

/* FRD-07: what the Fraud Review hooks send. The release and refund of a held escrow go through the
 * escrow routes (fraud-detection-architecture.md section 18.3), so their bodies must carry what those
 * routes validate: escrowId, trigger, a release deliveryId, an idempotency key of four parts, a reason. */

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
}));

vi.mock("@/lib/api", () => ({ api }));

function wrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("Fraud Review hooks", () => {
  beforeEach(() => {
    api.get.mockReset();
    api.post.mockReset();
    api.get.mockResolvedValue({ signals: [], users: [], escrows: [] });
    api.post.mockResolvedValue({ status: "completed" });
  });

  it("reads one status of the fraud signals, 50 a page, and passes the next token on", async () => {
    api.get.mockResolvedValueOnce({ signals: [], users: [], nextToken: "tok/1+" });
    const { result } = renderHook(() => useFraudSignals("reviewed"), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.get).toHaveBeenLastCalledWith("/admin/fraud-signals?status=reviewed&limit=50");

    await result.current.fetchNextPage();
    expect(api.get).toHaveBeenLastCalledWith("/admin/fraud-signals?status=reviewed&limit=50&nextToken=tok%2F1%2B");
  });

  it("reads the held queue from its own route", async () => {
    const { result } = renderHook(() => useHeldEscrows(), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.get).toHaveBeenLastCalledWith("/admin/escrows/held?limit=50");
  });

  it("releases a held escrow through /escrow/release as an admin override with the reason", async () => {
    const { result } = renderHook(() => useSettleHeldEscrow(), { wrapper: wrapper() });
    await result.current.mutateAsync({
      escrow: { escrowId: "escrow-order-ord-9", deliveryId: "del-9", orderId: "ord-9" },
      op: "release",
      reason: "Checked with the buyer.",
    });

    const [path, body] = api.post.mock.calls[0];
    expect(path).toBe("/escrow/release");
    expect(body).toMatchObject({
      escrowId: "escrow-order-ord-9",
      trigger: "admin_override",
      deliveryId: "del-9",
      reason: "Checked with the buyer.",
    });
    expect(body.idempotencyKey).toMatch(/^admin:escrow-order-ord-9:release:\d+$/);
  });

  it("a release of an escrow without a delivery still sends a delivery reference", async () => {
    const { result } = renderHook(() => useSettleHeldEscrow(), { wrapper: wrapper() });
    await result.current.mutateAsync({
      escrow: { escrowId: "escrow-order-ord-9", deliveryId: "", orderId: "ord-9" },
      op: "release",
      reason: "Self-pickup collected.",
    });

    expect(api.post.mock.calls[0][1].deliveryId).toBe("ord-9");
  });

  it("refunds a held escrow through /escrow/refund with the reason and no delivery", async () => {
    const { result } = renderHook(() => useSettleHeldEscrow(), { wrapper: wrapper() });
    await result.current.mutateAsync({
      escrow: { escrowId: "escrow-order-ord-9", deliveryId: "del-9" },
      op: "refund",
      reason: "Goods never sent.",
    });

    const [path, body] = api.post.mock.calls[0];
    expect(path).toBe("/escrow/refund");
    expect(body).toEqual({
      escrowId: "escrow-order-ord-9",
      trigger: "admin_override",
      idempotencyKey: expect.stringMatching(/^admin:escrow-order-ord-9:refund:\d+$/),
      reason: "Goods never sent.",
    });
  });
});

describe("Manual payout claim hook (FUP-05)", () => {
  beforeEach(() => {
    api.post.mockReset();
    api.post.mockResolvedValue({ claimed: true });
  });

  it.each(["claim", "unclaim"] as const)("posts %s to the leg's route, a ledger ref URL-encoded, with no body", async (action) => {
    const { result } = renderHook(() => useManualPayoutClaim(), { wrapper: wrapper() });
    await result.current.mutateAsync({ escrowId: "escrow-order-ord-9", ref: "release#farmer", action });

    expect(api.post).toHaveBeenCalledWith(`/admin/payouts/escrow-order-ord-9/release%23farmer/${action}`);
  });
});
