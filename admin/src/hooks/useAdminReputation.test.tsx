import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { useReputationAction, useReputationAppeals, useReputationDecision, useUserReputation } from "./useAdminReputation";

/* ESC-32 (reputation-api.md 1.4.0 section 9): what the admin reputation hooks send, and that a
 * refused uphold (the appeal changed since the page loaded) reloads the lists. */

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
}));

vi.mock("@/lib/api", () => ({ api }));

function setup() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(client, "invalidateQueries");
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return { wrapper, invalidate };
}

describe("admin reputation hooks", () => {
  beforeEach(() => {
    api.get.mockReset();
    api.post.mockReset();
    api.get.mockResolvedValue({ appeals: [], events: [], reputation: {} });
    api.post.mockResolvedValue({});
  });

  it("reads a user's reputation 50 events a page and passes the next token on", async () => {
    api.get.mockResolvedValueOnce({ reputation: {}, events: [], nextToken: "tok/1+" });
    const { wrapper } = setup();
    const { result } = renderHook(() => useUserReputation("farmer 1"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.get).toHaveBeenLastCalledWith("/admin/users/farmer%201/reputation?limit=50");

    await result.current.fetchNextPage();
    expect(api.get).toHaveBeenLastCalledWith("/admin/users/farmer%201/reputation?limit=50&nextToken=tok%2F1%2B");
  });

  it("reads one status of the appeals queue", async () => {
    const { wrapper } = setup();
    const { result } = renderHook(() => useReputationAppeals("review"), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.get).toHaveBeenLastCalledWith("/admin/reputation/appeals?status=review&limit=50");
  });

  it("an uphold carries what the page showed of the appeal; an excuse does not", async () => {
    const { wrapper } = setup();
    const { result } = renderHook(() => useReputationDecision(), { wrapper });

    await result.current.mutateAsync({
      userId: "trucker-1",
      eventId: "CANCEL#ord-1",
      decision: "uphold",
      note: "The buyer confirmed.",
      seenAppealTextAt: "2026-11-02T08:00:00.000Z",
    });
    expect(api.post.mock.calls[0]).toEqual([
      "/admin/users/trucker-1/reputation/events/CANCEL%23ord-1/decision",
      { decision: "uphold", note: "The buyer confirmed.", seenAppealTextAt: "2026-11-02T08:00:00.000Z" },
    ]);

    // The page showed no text: the field is left out, which the server reads as "no text shown".
    await result.current.mutateAsync({
      userId: "trucker-1",
      eventId: "OVERDUE#ord-2",
      decision: "uphold",
      note: "No delivery.",
      until: "2026-12-01T20:59:59.000Z",
    });
    expect(api.post.mock.calls[1][1]).toEqual({ decision: "uphold", note: "No delivery.", until: "2026-12-01T20:59:59.000Z" });

    await result.current.mutateAsync({
      userId: "trucker-1",
      eventId: "CANCEL#ord-1",
      decision: "excuse",
      note: "Flood confirmed.",
      seenAppealTextAt: "2026-11-02T08:00:00.000Z",
    });
    expect(api.post.mock.calls[2][1]).toEqual({ decision: "excuse", note: "Flood confirmed." });
  });

  it("reloads the reputation lists when the server says the appeal changed, and not on another refusal", async () => {
    const { wrapper, invalidate } = setup();
    const { result } = renderHook(() => useReputationDecision(), { wrapper });
    const vars = { userId: "trucker-1", eventId: "OVERDUE#ord-2", decision: "uphold" as const, note: "No delivery." };

    api.post.mockRejectedValueOnce({ status: 409, code: "REPUTATION_EVENT_DECIDED" });
    await expect(result.current.mutateAsync(vars)).rejects.toMatchObject({ code: "REPUTATION_EVENT_DECIDED" });
    expect(invalidate).not.toHaveBeenCalled();

    api.post.mockRejectedValueOnce({ status: 409, code: "REPUTATION_APPEAL_CHANGED" });
    await expect(result.current.mutateAsync(vars)).rejects.toMatchObject({ code: "REPUTATION_APPEAL_CHANGED" });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["admin", "reputation"] });
  });

  it("sends an action with its reason, and the end date only when there is one", async () => {
    const { wrapper, invalidate } = setup();
    const { result } = renderHook(() => useReputationAction(), { wrapper });

    await result.current.mutateAsync({ userId: "farmer-1", action: "restrict", reason: "No-shows.", until: "2026-12-01T20:59:59.000Z" });
    await result.current.mutateAsync({ userId: "farmer-1", action: "lift", reason: "Withdrawn." });

    expect(api.post.mock.calls[0]).toEqual([
      "/admin/users/farmer-1/reputation",
      { action: "restrict", reason: "No-shows.", until: "2026-12-01T20:59:59.000Z" },
    ]);
    expect(api.post.mock.calls[1][1]).toEqual({ action: "lift", reason: "Withdrawn." });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["admin", "reputation"] });
  });
});
