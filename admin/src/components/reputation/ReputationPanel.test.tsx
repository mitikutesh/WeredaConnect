import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { dayOffset, endOfDayIso } from "@/lib/reputation";
import { ReputationPanel } from "./ReputationPanel";

/* ESC-32: the reputation panel of the user page and its action dialog. The hooks are mocked; the
 * English strings are real, so the tests also prove the keys the panel reads exist. */

type MutateOptions = {
  onSuccess?: (result: unknown) => void;
  onError?: (err: unknown) => void;
};

const mocks = vi.hoisted(() => ({
  query: {
    data: undefined as unknown,
    isLoading: false,
    isError: false,
    error: null as unknown,
    hasNextPage: false,
    isFetchingNextPage: false,
    fetchNextPage: vi.fn(),
  },
  userIds: [] as string[],
  decide: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
  act: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useAdminReputation", () => ({
  useUserReputation: (userId: string) => {
    mocks.userIds.push(userId);
    return mocks.query;
  },
  useReputationDecision: () => ({ mutate: mocks.decide, isPending: false }),
  useReputationAction: () => ({ mutate: mocks.act, isPending: false }),
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

function reputation(overrides: Record<string, unknown> = {}) {
  return {
    userId: "trucker-1",
    role: "trucker",
    tier: "low",
    activePoints: 8,
    thresholds: { warning: 3, low: 6, restricted: 10 },
    ratingAverage: 4.8,
    ratingCount: 12,
    ratingDeduction: 0.8,
    effectiveRating: 4.0,
    reliabilityPct: 83,
    completedOrders90d: 10,
    faultCancels90d: 2,
    noShows90d: 0,
    nextPointExpiryAt: "2027-01-30T06:00:00.000Z",
    penaltiesEnabled: true,
    asOf: "2026-11-12T09:00:00.000Z",
    ...overrides,
  };
}

const cancel = {
  eventId: "CANCEL#ord-7f3a",
  code: "LATE_CANCEL",
  party: "trucker",
  points: 4,
  basePoints: 4,
  earnedBack: 0,
  remainingPoints: 4,
  status: "active",
  occurredAt: "2026-11-01T06:00:00.000Z",
  orderId: "ord-7f3a",
  buyerId: "buyer-19",
  appeal: {
    status: "pending",
    canAppeal: false,
    text: "The road to Kombolcha was closed by the flood.",
    appealedAt: "2026-11-02T08:00:00.000Z",
    hasPhoto: true,
    appealPhotoUrl: "https://uploads.test/uploads/appeals/trucker-1/a.webp?sig",
  },
};

const decline = {
  eventId: "REQUEST#ord-91c0",
  code: "DECLINED_REQUEST",
  points: 0,
  basePoints: 1,
  zeroReason: "pair_cap",
  remainingPoints: 0,
  status: "active",
  occurredAt: "2026-10-20T12:00:00.000Z",
  orderId: "ord-91c0",
  buyerId: "buyer-19",
  appeal: { status: "none", canAppeal: false },
};

const tierRow = {
  eventId: "TIER#2026-11-01T06:00:04.000Z#CANCEL#ord-7f3a",
  code: "TIER_CHANGED",
  status: "active",
  occurredAt: "2026-11-01T06:00:04.000Z",
  fromTier: "warning",
  toTier: "low",
};

function page(overrides: Record<string, unknown> = {}) {
  return { reputation: reputation(), restrictions: [], events: [cancel, decline, tierRow], ...overrides };
}

function setPages(...list: Record<string, unknown>[]) {
  mocks.query.data = { pages: list, pageParams: list.map(() => null) };
}

function renderPanel() {
  return render(
    <MemoryRouter>
      <ReputationPanel userId="trucker-1" />
    </MemoryRouter>,
  );
}

describe("ReputationPanel", () => {
  beforeEach(() => {
    mocks.query.data = undefined;
    mocks.query.isLoading = false;
    mocks.query.isError = false;
    mocks.query.error = null;
    mocks.query.hasNextPage = false;
    mocks.query.fetchNextPage.mockReset();
    mocks.userIds.length = 0;
    mocks.decide.mockReset();
    mocks.act.mockReset();
  });

  it("shows a loading state", () => {
    mocks.query.isLoading = true;
    renderPanel();

    expect(screen.getByText("Loading...")).toBeTruthy();
    expect(mocks.userIds).toContain("trucker-1");
  });

  it("says when the account has no reputation record, and when the read failed", () => {
    mocks.query.isError = true;
    mocks.query.error = { status: 404, code: "REPUTATION_NOT_FOUND" };
    const { unmount } = renderPanel();
    expect(screen.getByRole("alert").textContent).toBe("This account has no reputation record.");
    unmount();

    mocks.query.error = { status: 500 };
    renderPanel();
    expect(screen.getByRole("alert").textContent).toBe("The reputation could not be loaded. Reload the page.");
  });

  it("tells a buyer has no reputation and offers no action", () => {
    setPages(page({ reputation: reputation({ role: "buyer", tier: "good", activePoints: 0 }), events: [] }));
    renderPanel();

    expect(screen.getByText(/Buyers have no reputation/)).toBeTruthy();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("shows the points, the tier, the rating, the reliability and the events with their appeal", () => {
    setPages(page());
    renderPanel();

    const panel = screen.getByRole("region", { name: "Reputation" });
    expect(within(panel).getAllByText("Low reputation").length).toBeGreaterThan(0);
    expect(within(panel).getByText("8")).toBeTruthy();
    expect(within(panel).getByText("warning at 3, low at 6, restricted at 10")).toBeTruthy();
    expect(within(panel).getByText("4.0 (4.8 from 12 rating(s), 0.8 deducted)")).toBeTruthy();
    expect(within(panel).getByText("83%")).toBeTruthy();
    expect(within(panel).getByText("None")).toBeTruthy();

    const row = within(panel).getByText("Late cancel").closest("tr") as HTMLElement;
    expect(within(row).getByText("4 of 4")).toBeTruthy();
    expect(within(row).getByText("Appeal pending")).toBeTruthy();
    expect(within(row).getByText("The road to Kombolcha was closed by the flood.")).toBeTruthy();
    // Admin only: the buyer of the order, linked to their page.
    expect(within(row).getByRole("link", { name: "buyer-19" }).getAttribute("href")).toBe("/users/buyer-19");
    // The appeal photo comes through the short-lived link the server signed.
    expect(within(row).getByAltText("Photo sent with the appeal").getAttribute("src")).toBe(
      "https://uploads.test/uploads/appeals/trucker-1/a.webp?sig",
    );

    const capped = within(panel).getByText("Declined a paid order").closest("tr") as HTMLElement;
    expect(within(capped).getByText("0 of 0")).toBeTruthy();
    expect(within(capped).getByText("Same buyer within 30 days")).toBeTruthy();
    expect(within(capped).getByText("1 before caps")).toBeTruthy();

    // A system row is history: no points, no decision.
    const tier = within(panel).getByText("Tier changed").closest("tr") as HTMLElement;
    expect(within(tier).getByText("Warning to Low reputation")).toBeTruthy();
    expect(within(tier).queryByRole("button")).toBeNull();
    expect(within(tier).queryByText("Active")).toBeNull();
  });

  it("offers excuse on an active penalty and uphold only on an open appeal", () => {
    setPages(page());
    renderPanel();

    expect(screen.getByRole("button", { name: "Excuse CANCEL#ord-7f3a" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Uphold CANCEL#ord-7f3a" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Excuse REQUEST#ord-91c0" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Uphold REQUEST#ord-91c0" })).toBeNull();
  });

  it("shows the restriction, the tag and the history, and offers lift instead of restrict", () => {
    setPages(
      page({
        reputation: reputation({
          tier: "restricted",
          restriction: { cause: "admin", since: "2026-11-10T09:00:00.000Z", until: "2026-12-10T09:00:00.000Z" },
          override: { tag: "low", until: "2026-12-01T12:00:00.000Z", reason: "Complaints from buyers.", by: "admin-1" },
        }),
        restrictions: [
          {
            eventId: "RESTRICTION#2026-11-10T09:00:00.000Z",
            cause: "admin",
            status: "active",
            since: "2026-11-10T09:00:00.000Z",
            until: "2026-12-10T09:00:00.000Z",
            reason: "Three no-shows reported.",
            by: "admin-1",
          },
          {
            eventId: "RESTRICTION#2026-08-01T09:00:00.000Z",
            cause: "points",
            status: "lifted",
            since: "2026-08-01T09:00:00.000Z",
            until: "2026-08-15T09:00:00.000Z",
            liftedAt: "2026-08-03T09:00:00.000Z",
            liftedBy: "admin-2",
          },
        ],
      }),
    );
    renderPanel();

    expect(screen.getByText("since 10 Nov 2026, until 10 Dec 2026", { selector: "span.ml-2" })).toBeTruthy();
    expect(screen.getByText("Low reputation tag set by an admin")).toBeTruthy();
    expect(screen.getByText("until 01 Dec 2026, by admin-1: Complaints from buyers.")).toBeTruthy();
    expect(screen.getByText("Three no-shows reported.")).toBeTruthy();
    expect(screen.getByText(/lifted 03 Aug 2026.* by admin-2/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Lift restriction" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Restrict account" })).toBeNull();
  });

  it("says a review restriction has no end until an admin decides", () => {
    setPages(
      page({
        reputation: reputation({
          tier: "restricted",
          restriction: { cause: "delivery_overdue", since: "2026-11-10T09:00:00.000Z" },
        }),
      }),
    );
    renderPanel();

    expect(screen.getByText("Overdue delivery (in review)")).toBeTruthy();
    expect(screen.getByText("since 10 Nov 2026, until an admin decides the review")).toBeTruthy();
  });

  it("is read only while the penalties flag is off", () => {
    setPages(page({ reputation: reputation({ penaltiesEnabled: false }) }));
    renderPanel();

    expect(screen.getByRole("status").textContent).toMatch(/switched off in this environment/);
    for (const button of screen.getAllByRole("button")) {
      expect((button as HTMLButtonElement).disabled).toBe(true);
    }
  });

  it("excuses an event with the note the admin wrote, then says so", async () => {
    const user = userEvent.setup();
    setPages(page());
    renderPanel();

    await user.click(screen.getByRole("button", { name: "Excuse CANCEL#ord-7f3a" }));
    const dialog = screen.getByRole("dialog", { name: "Excuse this event: Late cancel" });
    // No end date on an excuse.
    expect(within(dialog).queryByLabelText(/Restriction ends/)).toBeNull();

    // A note is required: nothing is sent without one.
    await user.click(within(dialog).getByRole("button", { name: "Excuse" }));
    expect(within(dialog).getByRole("alert").textContent).toBe("Write a reason of 3 to 500 characters.");
    expect(mocks.decide).not.toHaveBeenCalled();

    await user.type(within(dialog).getByLabelText(/Note to the account holder/), "  Flood confirmed with the buyer.  ");
    await user.click(within(dialog).getByRole("button", { name: "Excuse" }));

    expect(mocks.decide).toHaveBeenCalledTimes(1);
    const [vars, opts] = mocks.decide.mock.calls[0];
    expect(vars).toEqual({
      userId: "trucker-1",
      eventId: "CANCEL#ord-7f3a",
      decision: "excuse",
      note: "Flood confirmed with the buyer.",
      until: undefined,
    });

    act(() => opts?.onSuccess?.({ alreadyDecided: false }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByText("Event excused.")).toBeTruthy();
  });

  it("keeps the dialog open with the server's refusal, and says when nothing changed", async () => {
    const user = userEvent.setup();
    setPages(page());
    renderPanel();

    await user.click(screen.getByRole("button", { name: "Uphold CANCEL#ord-7f3a" }));
    const dialog = screen.getByRole("dialog", { name: "Uphold this event: Late cancel" });
    await user.type(within(dialog).getByLabelText(/Note to the account holder/), "The buyer confirmed the cancel.");
    await user.click(within(dialog).getByRole("button", { name: "Uphold" }));

    const [, opts] = mocks.decide.mock.calls[0];
    act(() => opts?.onError?.({ code: "REPUTATION_EVENT_DECIDED", status: 409 }));
    expect(within(screen.getByRole("dialog")).getByText(/Another admin already decided this event/)).toBeTruthy();

    act(() => opts?.onError?.({ status: 500 }));
    expect(within(screen.getByRole("dialog")).getByText("Action failed. Please try again.")).toBeTruthy();

    act(() => opts?.onSuccess?.({ alreadyDecided: true }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByText("This was already done. Nothing changed.")).toBeTruthy();
  });

  it("lets the uphold of an overdue delivery review set the restriction's end, or leave it empty", async () => {
    const user = userEvent.setup();
    setPages(
      page({
        events: [
          {
            eventId: "OVERDUE#ord-5",
            code: "DELIVERY_OVERDUE",
            points: 5,
            remainingPoints: 5,
            status: "active",
            occurredAt: "2026-11-10T09:00:00.000Z",
            orderId: "ord-5",
            appeal: { status: "review", canAppeal: true },
          },
        ],
      }),
    );
    renderPanel();

    await user.click(screen.getByRole("button", { name: "Uphold OVERDUE#ord-5" }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText(/14 days from now if you leave it empty/)).toBeTruthy();
    expect(within(dialog).getByLabelText(/Restriction ends \(optional\)/)).toBeTruthy();

    await user.type(within(dialog).getByLabelText(/Note to the account holder/), "No delivery was made.");
    await user.click(within(dialog).getByRole("button", { name: "Uphold" }));

    expect(mocks.decide.mock.calls[0][0]).toMatchObject({ eventId: "OVERDUE#ord-5", decision: "uphold", until: undefined });
  });

  it("tags with a required reason and end date", async () => {
    const user = userEvent.setup();
    setPages(page());
    renderPanel();

    await user.click(screen.getByRole("button", { name: "Tag as Low reputation" }));
    const dialog = screen.getByRole("dialog", { name: "Tag as Low reputation" });
    await user.type(within(dialog).getByLabelText(/Reason/), "Repeated complaints from three buyers.");

    // The end date is required.
    await user.click(within(dialog).getByRole("button", { name: "Tag as Low reputation" }));
    expect(within(dialog).getByRole("alert").textContent).toMatch(/Choose a date after today and at most 90 days ahead/);
    expect(mocks.act).not.toHaveBeenCalled();

    const tomorrow = dayOffset(new Date(), 1);
    const until = within(dialog).getByLabelText(/Until/) as HTMLInputElement;
    await user.type(until, tomorrow);
    await user.click(within(dialog).getByRole("button", { name: "Tag as Low reputation" }));

    expect(mocks.act).toHaveBeenCalledTimes(1);
    const [vars, opts] = mocks.act.mock.calls[0];
    expect(vars).toEqual({
      userId: "trucker-1",
      action: "tag",
      reason: "Repeated complaints from three buyers.",
      until: endOfDayIso(tomorrow),
    });

    act(() => opts?.onSuccess?.({ alreadyApplied: false }));
    expect(screen.getByText("Low reputation tag set.")).toBeTruthy();
  });

  it("lifts with a reason and no date, and shows a refusal", async () => {
    const user = userEvent.setup();
    setPages(
      page({
        reputation: reputation({
          tier: "restricted",
          restriction: { cause: "points", since: "2026-11-10T09:00:00.000Z", until: "2026-11-24T09:00:00.000Z" },
        }),
      }),
    );
    renderPanel();

    await user.click(screen.getByRole("button", { name: "Lift restriction" }));
    const dialog = screen.getByRole("dialog", { name: "Lift restriction" });
    expect(within(dialog).queryByLabelText(/Until/)).toBeNull();
    await user.type(within(dialog).getByLabelText(/Reason/), "The buyers withdrew their complaints.");
    await user.click(within(dialog).getByRole("button", { name: "Lift restriction" }));

    const [vars, opts] = mocks.act.mock.calls[0];
    expect(vars).toEqual({
      userId: "trucker-1",
      action: "lift",
      reason: "The buyers withdrew their complaints.",
      until: undefined,
    });
    act(() => opts?.onError?.({ code: "REPUTATION_NOT_RESTRICTED", status: 409 }));
    expect(within(screen.getByRole("dialog")).getByText(/no restriction to lift/)).toBeTruthy();

    // Cancel closes it and sends nothing more.
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(mocks.act).toHaveBeenCalledTimes(1);
  });

  it("says when there are no events and loads more on demand", async () => {
    const user = userEvent.setup();
    setPages(page({ events: [] }));
    const { unmount } = renderPanel();
    expect(screen.getByText("No events.")).toBeTruthy();
    unmount();

    // The same event on two pages (a retried page) is listed once.
    setPages(page({ events: [cancel] }), page({ events: [cancel, decline] }));
    mocks.query.hasNextPage = true;
    renderPanel();
    expect(screen.getAllByText("Late cancel")).toHaveLength(1);
    await user.click(screen.getByRole("button", { name: "Load more events" }));
    expect(mocks.query.fetchNextPage).toHaveBeenCalledTimes(1);
  });

  it("shows a hint instead of a broken image when the photo link is missing", () => {
    setPages(page({ events: [{ ...cancel, appeal: { ...cancel.appeal, appealPhotoUrl: undefined } }] }));
    renderPanel();

    expect(screen.getByText("The photo link has expired or is missing. Reload the page.")).toBeTruthy();
    expect(screen.queryByAltText("Photo sent with the appeal")).toBeNull();
  });
});
