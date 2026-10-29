import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { formatETB } from "@/lib/i18n";
import type { DeliveryTrail } from "@/lib/trail";
import { DisputeDetailPage } from "./DisputeDetailPage";

/* LT-13: the dispute page and its Trip trail tab. The hooks are mocked; the English strings are
 * real, so the tests also prove the keys exist. */

type TrailQuery = {
  data: unknown;
  isLoading: boolean;
  isError: boolean;
  error: unknown;
  refetch: ReturnType<typeof vi.fn>;
};

const mocks = vi.hoisted(() => ({
  escrow: { data: undefined as unknown, isLoading: false, isError: false, error: null as unknown },
  trail: undefined as unknown as TrailQuery,
  trailIds: [] as string[],
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

type MutateOptions = { onSuccess?: () => void; onError?: (err: unknown) => void; onSettled?: () => void };

vi.mock("@/hooks/useQuery", () => ({
  useResolveDispute: () => ({ mutate: mocks.mutate, isPending: false }),
  useEscrowDetail: () => mocks.escrow,
  useDeliveryTrail: (deliveryId: string) => {
    mocks.trailIds.push(deliveryId);
    return mocks.trail;
  },
}));

vi.mock("@/hooks/useLocale", async () => {
  const { t } = await import("@/lib/i18n");
  return {
    useLocale: () => ({
      locale: "en" as const,
      setLocale: vi.fn(),
      t: (key: string) => t(key, "en"),
    }),
  };
});

function trailQuery(): TrailQuery {
  return { data: undefined, isLoading: false, isError: false, error: null, refetch: vi.fn() };
}

function escrow(deliveryId: string | null = "del-101") {
  return {
    escrow: {
      escrowId: "escrow-1",
      deliveryId: deliveryId ?? "",
      buyerName: "Sara",
      farmerName: "Abebe",
      amount: 30900,
      status: "disputed",
      dispute: { reason: "short_delivery", createdAt: "2026-10-27T09:50:00Z" },
    },
  };
}

function fix(capturedAt: string, latitude: number, extra: Partial<DeliveryTrail["points"][number]> = {}) {
  return {
    capturedAt,
    receivedAt: capturedAt,
    kind: "fix",
    source: "hourly",
    latitude,
    longitude: 38.74,
    accuracyM: 35,
    mocked: false,
    pausedUntil: null,
    coarse: false,
    late: false,
    ...extra,
  };
}

/** A trail with a coarse fix, a pause, a late fix and an impossible jump at the end. */
function trailWithAJump(): DeliveryTrail {
  return {
    deliveryId: "del-101",
    tripId: "trip-101",
    status: "disputed",
    open: false,
    from: "2026-10-27T05:05:00.000Z",
    until: "2026-10-27T08:00:00.000Z",
    asOf: "2026-10-27T09:10:00.000Z",
    pickup: { latitude: 8.1504, longitude: 38.8196, at: "2026-10-27T05:05:00.000Z" },
    dropoff: { latitude: 9.03, longitude: 38.74, at: "2026-10-27T08:00:00.000Z" },
    points: [
      fix("2026-10-27T06:00:00.000Z", 8.1, { source: "scan" }),
      fix("2026-10-27T06:30:00.000Z", 8.2, { accuracyM: 1500, coarse: true }),
      {
        ...fix("2026-10-27T06:40:00.000Z", 0),
        kind: "paused",
        source: null,
        latitude: null,
        longitude: null,
        accuracyM: null,
        mocked: null,
        pausedUntil: "2026-10-27T07:10:00.000Z",
      },
      fix("2026-10-27T07:00:00.000Z", 8.35, { late: true, receivedAt: "2026-10-27T07:40:00.000Z" }),
      fix("2026-10-27T07:20:00.000Z", 9.35, { mocked: true }),
    ],
    legs: [
      {
        fromCapturedAt: "2026-10-27T06:00:00.000Z",
        toCapturedAt: "2026-10-27T07:00:00.000Z",
        distanceKm: 27.8,
        minutes: 60,
        kmh: 27.8,
        jump: false,
      },
      {
        fromCapturedAt: "2026-10-27T07:00:00.000Z",
        toCapturedAt: "2026-10-27T07:20:00.000Z",
        distanceKm: 111.19,
        minutes: 20,
        kmh: 333.6,
        jump: true,
      },
    ],
    pauses: [{ from: "2026-10-27T06:40:00.000Z", until: "2026-10-27T07:10:00.000Z" }],
    gaps: [],
    summary: {
      sharedWholeWay: true,
      declined: false,
      fixes: 4,
      coarseFixes: 1,
      mockedFixes: 1,
      lateFixes: 1,
      jumps: 1,
      gaps: 0,
      distanceKm: 139,
      durationMin: 175,
      maxKmh: 333.6,
    },
  };
}

function emptyTrail(): DeliveryTrail {
  return {
    ...trailWithAJump(),
    points: [],
    legs: [],
    pauses: [],
    gaps: [{ from: "2026-10-27T05:05:00.000Z", until: "2026-10-27T08:00:00.000Z", minutes: 175 }],
    pickup: { latitude: null, longitude: null, at: "2026-10-27T05:05:00.000Z" },
    dropoff: { latitude: null, longitude: null, at: "2026-10-27T08:00:00.000Z" },
    summary: {
      ...trailWithAJump().summary,
      sharedWholeWay: false,
      fixes: 0,
      coarseFixes: 0,
      mockedFixes: 0,
      lateFixes: 0,
      jumps: 0,
      gaps: 1,
      distanceKm: 0,
      maxKmh: null,
    },
  };
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={["/disputes/escrow-1"]}>
      <Routes>
        <Route path="/disputes/:id" element={<DisputeDetailPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("DisputeDetailPage Trip trail tab (LT-13)", () => {
  beforeEach(() => {
    mocks.escrow.data = escrow();
    mocks.escrow.isLoading = false;
    mocks.escrow.isError = false;
    mocks.trail = trailQuery();
    mocks.trailIds.length = 0;
  });

  it("shows the case, opens on the Trip trail tab, and asks for the dispute's delivery trail", () => {
    mocks.trail.isLoading = true;
    renderPage();

    expect(screen.getByRole("heading", { name: "Dispute" })).toBeTruthy();
    expect(screen.getByText("short_delivery")).toBeTruthy();
    const tab = screen.getByRole("tab", { name: "Trip trail" });
    expect(tab.getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tabpanel").getAttribute("aria-labelledby")).toBe(tab.id);
    expect(mocks.trailIds).toContain("del-101");
  });

  it("says the trail is loading", () => {
    mocks.trail.isLoading = true;
    renderPage();

    expect(screen.getByRole("status").textContent).toBe("Loading the trail...");
  });

  it("shows the error with a retry, in the words of the error code", async () => {
    const user = userEvent.setup();
    mocks.trail.isError = true;
    mocks.trail.error = { code: "ADMIN_018" };
    renderPage();

    expect(screen.getByRole("alert").textContent).toBe("Delivery not found.");
    await user.click(screen.getByRole("button", { name: "Retry" }));
    expect(mocks.trail.refetch).toHaveBeenCalledTimes(1);
  });

  it("falls back to a plain message for an error without a known code", () => {
    mocks.trail.isError = true;
    mocks.trail.error = { code: "NETWORK_ERROR" };
    renderPage();

    expect(screen.getByRole("alert").textContent).toBe("The trail could not be loaded.");
  });

  it("an empty trail says there were no check-ins, and that sharing had gaps", () => {
    mocks.trail.data = emptyTrail();
    renderPage();

    expect(screen.getByText("No check-ins between pickup and delivery.")).toBeTruthy();
    expect(screen.getByText("No check-ins in this window")).toBeTruthy();
    expect(screen.getByText(/No check-in from .* \(175 min\)/)).toBeTruthy();
    // Nothing has a position, so there is no map and no table.
    expect(screen.queryByRole("img")).toBeNull();
    expect(screen.queryByRole("table")).toBeNull();
    expect(screen.getByText(/shows where the truck was, not what it carried/)).toBeTruthy();
  });

  it("a trail with an impossible jump flags the leg, the map and the summary", () => {
    mocks.trail.data = trailWithAJump();
    const { container } = renderPage();

    const map = screen.getByRole("img", { name: "Map of the trail between pickup and drop-off (check-ins: 4)" });
    expect(map).toBeTruthy();
    expect(container.querySelectorAll('[data-testid="trail-jump"]')).toHaveLength(1);
    expect(container.querySelectorAll('[data-testid="trail-leg"]')).toHaveLength(1);

    const rows = within(screen.getByRole("table")).getAllByRole("row");
    // The header, four fixes and the pause.
    expect(rows).toHaveLength(6);
    expect(within(rows[1]).getByText("8.1000, 38.7400")).toBeTruthy();
    expect(within(rows[1]).getByText("First precise check-in")).toBeTruthy();
    expect(within(rows[1]).getByText("QR scan")).toBeTruthy();
    expect(within(rows[2]).getByText("Coarse, about 1,500 m")).toBeTruthy();
    expect(within(rows[2]).getByText("Not used for speed")).toBeTruthy();
    expect(within(rows[3]).getByText(/Paused by the driver until/)).toBeTruthy();
    expect(within(rows[4]).getByText("27.8 km in 60 min · 27.8 km/h")).toBeTruthy();
    expect(within(rows[4]).getByText(/Sent late, received/)).toBeTruthy();
    expect(within(rows[5]).getByText("111.19 km in 20 min · 333.6 km/h")).toBeTruthy();
    expect(within(rows[5]).getByText("Impossible jump")).toBeTruthy();
    expect(within(rows[5]).getByText("Mock location")).toBeTruthy();

    expect(screen.getByText("Impossible jumps (over 130 km/h): 1")).toBeTruthy();
    expect(screen.getByText("Shared the whole way (check-ins: 4)")).toBeTruthy();
    expect(screen.getByText("Check-ins flagged as a mock location: 1")).toBeTruthy();
    expect(screen.getByText(/Coarse check-ins .*: 1/)).toBeTruthy();
    expect(screen.getByText("No gaps")).toBeTruthy();
    expect(screen.getByText(/shows where the truck was, not what it carried/)).toBeTruthy();
  });

  it("without a linked delivery there is no trail to ask for", () => {
    mocks.escrow.data = escrow(null);
    renderPage();

    expect(screen.getByText("No delivery is linked to this dispute yet, so there is no trail.")).toBeTruthy();
    expect(mocks.trailIds.every((id) => id === "")).toBe(true);
  });

  it("a self-pickup has no truck trail, and a delivery not picked up has none yet", () => {
    mocks.trail.data = { ...emptyTrail(), tripId: null };
    const { unmount } = renderPage();
    expect(screen.getByText("The buyer collected the goods (self-pickup), so there is no truck trail.")).toBeTruthy();
    unmount();

    mocks.trail.data = { ...emptyTrail(), from: null, until: null };
    renderPage();
    expect(screen.getByText("The goods were not picked up yet. The trail starts at pickup.")).toBeTruthy();
  });
});

/* Web · Admin dispute evidence board: the admin decides beside the evidence, with the same
 * verdict flow as the Disputes list (ADMIN_015 asks what happens to the order). */
describe("DisputeDetailPage Decide", () => {
  beforeEach(() => {
    mocks.escrow.data = escrow();
    mocks.escrow.isLoading = false;
    mocks.escrow.isError = false;
    mocks.trail = trailQuery();
    // The trail loads meanwhile, so the only alert could come from the verdict.
    mocks.trail.isLoading = true;
    mocks.mutate.mockReset();
  });

  function lastMutate() {
    const [vars, opts] = mocks.mutate.mock.calls[mocks.mutate.mock.calls.length - 1];
    return { vars: vars as { id: string; body: Record<string, unknown> }, opts: opts ?? {} };
  }

  it("shows the frozen amount, both verdicts and the audit note", () => {
    renderPage();
    expect(screen.getByText(`${formatETB(30900, "en")} frozen`)).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Decide" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Farmer Wins" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Buyer Wins" })).toBeTruthy();
    expect(screen.getByText("Every verdict is written to the audit log under your admin account.")).toBeTruthy();
  });

  it("sends the verdict, then says it was saved", async () => {
    const user = userEvent.setup();
    renderPage();
    await user.click(screen.getByRole("button", { name: "Farmer Wins" }));

    const { vars, opts } = lastMutate();
    expect(vars).toEqual({ id: "escrow-1", body: { resolution: "farmer_wins", action: "release" } });
    act(() => {
      opts.onSuccess?.();
      opts.onSettled?.();
    });
    expect(screen.getByText("Verdict saved.").getAttribute("role")).toBe("status");
  });

  it("on ADMIN_015 asks what happens to the order instead of failing", async () => {
    const user = userEvent.setup();
    renderPage();
    await user.click(screen.getByRole("button", { name: "Buyer Wins" }));
    act(() => {
      lastMutate().opts.onError?.({ code: "ADMIN_015" });
      lastMutate().opts.onSettled?.();
    });

    expect(screen.getByText("The order cannot follow this verdict as it is")).toBeTruthy();
    expect(screen.getByLabelText(/Return the goods to the farmer/)).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("a resolved dispute has nothing left to decide", () => {
    mocks.escrow.data = { escrow: { ...escrow().escrow, status: "dispute_resolved" } };
    renderPage();
    expect(screen.queryByRole("heading", { name: "Decide" })).toBeNull();
    expect(screen.queryByText(/frozen/)).toBeNull();
  });
});
