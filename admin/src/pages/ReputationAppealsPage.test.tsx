import { act, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { dayOffset, endOfDayIso } from "@/lib/reputation";
import { ReputationAppealsPage } from "./ReputationAppealsPage";

/* ESC-32: the appeals queue. The hooks are mocked; the English strings are real. */

type MutateOptions = {
  onSuccess?: (result: unknown) => void;
  onError?: (err: unknown) => void;
};

const mocks = vi.hoisted(() => ({
  query: {
    data: undefined as unknown,
    isLoading: false,
    isError: false,
    hasNextPage: false,
    isFetchingNextPage: false,
    fetchNextPage: vi.fn(),
  },
  statuses: [] as string[],
  decide: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useAdminReputation", () => ({
  useReputationAppeals: (status: string) => {
    mocks.statuses.push(status);
    return mocks.query;
  },
  useReputationDecision: () => ({ mutate: mocks.decide, isPending: false }),
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

function appeal(overrides: Record<string, unknown> = {}) {
  return {
    userId: "trucker-77",
    role: "trucker",
    displayName: "Kebede A.",
    eventId: "CANCEL#ord-7f3a",
    code: "LATE_CANCEL",
    points: 4,
    occurredAt: "2026-11-01T06:00:00.000Z",
    orderId: "ord-7f3a",
    appealStatus: "pending",
    appealedAt: "2026-11-02T08:00:00.000Z",
    text: "The road to Kombolcha was closed by the flood.",
    appealTextAt: "2026-11-02T08:00:00.000Z",
    hasPhoto: true,
    appealPhotoUrl: "https://uploads.test/uploads/appeals/trucker-77/a.webp?sig",
    ...overrides,
  };
}

function setPages(...list: Record<string, unknown>[]) {
  mocks.query.data = { pages: list, pageParams: list.map(() => null) };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <ReputationAppealsPage />
    </MemoryRouter>,
  );
}

describe("ReputationAppealsPage", () => {
  beforeEach(() => {
    mocks.query.data = undefined;
    mocks.query.isLoading = false;
    mocks.query.isError = false;
    mocks.query.hasNextPage = false;
    mocks.query.fetchNextPage.mockReset();
    mocks.statuses.length = 0;
    mocks.decide.mockReset();
  });

  it("opens on the party appeals and shows loading, then empty", () => {
    mocks.query.isLoading = true;
    const { unmount } = renderPage();
    expect(screen.getByText("Loading...")).toBeTruthy();
    expect(mocks.statuses[0]).toBe("pending");
    unmount();

    mocks.query.isLoading = false;
    setPages({ appeals: [] });
    renderPage();
    expect(screen.getByText("No open appeals.")).toBeTruthy();
  });

  it("says when the queue could not be loaded", () => {
    mocks.query.isError = true;
    renderPage();

    expect(screen.getByRole("alert").textContent).toBe("The appeals could not be loaded. Reload the page.");
    expect(screen.queryByText("No open appeals.")).toBeNull();
  });

  it("shows each appeal with the user, the event, the text and the photo", () => {
    setPages({
      appeals: [
        appeal(),
        appeal({
          userId: "farmer-2",
          role: "farmer",
          displayName: undefined,
          eventId: "TRIP_CANCEL#trip-9",
          code: "TRIP_CANCELLED_WITH_BOOKINGS",
          orderId: undefined,
          tripId: "trip-9",
          text: undefined,
          hasPhoto: false,
          appealPhotoUrl: undefined,
        }),
      ],
    });
    renderPage();

    const first = screen.getByRole("region", { name: "Kebede A. CANCEL#ord-7f3a" });
    expect(within(first).getByText("Late cancel")).toBeTruthy();
    expect(within(first).getByText("4 point(s)")).toBeTruthy();
    expect(within(first).getByText("Trucker")).toBeTruthy();
    expect(within(first).getByRole("link", { name: "Kebede A." }).getAttribute("href")).toBe("/users/trucker-77");
    expect(within(first).getByText("Order ord-7f3a")).toBeTruthy();
    expect(within(first).getByText("The road to Kombolcha was closed by the flood.")).toBeTruthy();
    const photo = within(first).getByAltText("Photo sent with the appeal");
    expect(photo.getAttribute("src")).toBe("https://uploads.test/uploads/appeals/trucker-77/a.webp?sig");
    expect(within(first).getByRole("link", { name: /Open the photo/ }).getAttribute("target")).toBe("_blank");

    // No name on the row: the id stands in. No text, no photo.
    const second = screen.getByRole("region", { name: "farmer-2 TRIP_CANCEL#trip-9" });
    expect(within(second).getByText("Trip trip-9")).toBeTruthy();
    expect(within(second).getByText("The account holder has not written anything.")).toBeTruthy();
    expect(within(second).queryByAltText("Photo sent with the appeal")).toBeNull();
  });

  it("tells the admin to reload when the photo link has run out", () => {
    setPages({ appeals: [appeal()] });
    renderPage();

    fireEvent.error(screen.getByAltText("Photo sent with the appeal"));

    expect(screen.getByText("The photo link has expired or is missing. Reload the page.")).toBeTruthy();
    expect(screen.queryByAltText("Photo sent with the appeal")).toBeNull();
  });

  it("accepts an appeal: excuses the event with the admin's note", async () => {
    const user = userEvent.setup();
    setPages({ appeals: [appeal()] });
    renderPage();

    await user.click(screen.getByRole("button", { name: "Accept and excuse" }));
    const dialog = screen.getByRole("dialog", { name: "Excuse this event: Late cancel" });

    await user.click(within(dialog).getByRole("button", { name: "Accept and excuse" }));
    expect(mocks.decide).not.toHaveBeenCalled();

    await user.type(within(dialog).getByLabelText(/Note to the account holder/), "Flood confirmed with the buyer.");
    await user.click(within(dialog).getByRole("button", { name: "Accept and excuse" }));

    const [vars, opts] = mocks.decide.mock.calls[0];
    expect(vars).toEqual({
      userId: "trucker-77",
      eventId: "CANCEL#ord-7f3a",
      decision: "excuse",
      note: "Flood confirmed with the buyer.",
      until: undefined,
      seenAppealTextAt: "2026-11-02T08:00:00.000Z",
    });

    act(() => opts?.onSuccess?.({ alreadyDecided: false }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByRole("status").textContent).toBe("Event excused.");
  });

  it("upholds a penalty, shows a refusal in the dialog and a replay as nothing changed", async () => {
    const user = userEvent.setup();
    setPages({ appeals: [appeal()] });
    renderPage();

    await user.click(screen.getByRole("button", { name: "Uphold the penalty" }));
    const dialog = screen.getByRole("dialog", { name: "Uphold this event: Late cancel" });
    expect(within(dialog).queryByLabelText(/Restriction ends/)).toBeNull();
    await user.type(within(dialog).getByLabelText(/Note to the account holder/), "The buyer confirmed the cancel.");
    await user.click(within(dialog).getByRole("button", { name: "Uphold the penalty" }));

    const [vars, opts] = mocks.decide.mock.calls[0];
    expect(vars).toMatchObject({ decision: "uphold", note: "The buyer confirmed the cancel." });

    act(() => opts?.onError?.({ code: "REPUTATION_UNAVAILABLE", status: 503 }));
    expect(within(screen.getByRole("dialog")).getByText(/switched off in this environment/)).toBeTruthy();

    act(() => opts?.onSuccess?.({ alreadyDecided: true }));
    expect(screen.getByRole("status").textContent).toBe("This was already done. Nothing changed.");
  });

  it("an uphold from a stale page is refused: the dialog closes and the page says the appeal changed", async () => {
    const user = userEvent.setup();
    // A review the page shows with no text: the trucker's appeal arrives after it loaded.
    setPages({
      appeals: [
        appeal({ eventId: "OVERDUE#ord-5", code: "DELIVERY_OVERDUE", appealStatus: "review", text: undefined, appealTextAt: undefined, hasPhoto: false }),
      ],
    });
    renderPage();

    await user.click(screen.getByRole("button", { name: "Uphold the penalty" }));
    const dialog = screen.getByRole("dialog");
    await user.type(within(dialog).getByLabelText(/Note to the account holder/), "No delivery was made.");
    await user.click(within(dialog).getByRole("button", { name: "Uphold the penalty" }));

    const [vars, opts] = mocks.decide.mock.calls[0];
    expect(vars).toMatchObject({ eventId: "OVERDUE#ord-5", decision: "uphold", seenAppealTextAt: undefined });

    act(() => opts?.onError?.({ code: "REPUTATION_APPEAL_CHANGED", status: 409 }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByRole("alert").textContent).toBe(
      "The appeal changed after this page was loaded. It has been reloaded: read the appeal, then decide again.",
    );
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("switches to the overdue delivery reviews, where an uphold may set the restriction's end", async () => {
    const user = userEvent.setup();
    setPages({ appeals: [] });
    renderPage();

    await user.click(screen.getByRole("button", { name: "Overdue delivery reviews" }));
    expect(mocks.statuses.at(-1)).toBe("review");
    expect(screen.getByText("No open reviews.")).toBeTruthy();

    setPages({
      appeals: [appeal({ eventId: "OVERDUE#ord-5", code: "DELIVERY_OVERDUE", points: 5, appealStatus: "review", text: undefined, hasPhoto: false })],
    });
    await user.click(screen.getByRole("button", { name: "Appeals" }));
    await user.click(screen.getByRole("button", { name: "Overdue delivery reviews" }));

    await user.click(screen.getByRole("button", { name: "Uphold the penalty" }));
    const dialog = screen.getByRole("dialog", { name: "Uphold this event: Delivery overdue" });
    expect(within(dialog).getByLabelText(/Restriction ends \(optional\)/)).toBeTruthy();

    const tomorrow = dayOffset(new Date(), 1);
    await user.type(within(dialog).getByLabelText(/Note to the account holder/), "No delivery was made.");
    await user.type(within(dialog).getByLabelText(/Restriction ends \(optional\)/), tomorrow);
    await user.click(within(dialog).getByRole("button", { name: "Uphold the penalty" }));

    expect(mocks.decide.mock.calls[0][0]).toEqual({
      userId: "trucker-77",
      eventId: "OVERDUE#ord-5",
      decision: "uphold",
      note: "No delivery was made.",
      until: endOfDayIso(tomorrow),
      seenAppealTextAt: "2026-11-02T08:00:00.000Z",
    });
  });

  it("lists an appeal once across pages and loads more on demand", async () => {
    const user = userEvent.setup();
    setPages({ appeals: [appeal()] }, { appeals: [appeal(), appeal({ eventId: "CANCEL#ord-2" })] });
    mocks.query.hasNextPage = true;
    renderPage();

    expect(screen.getAllByRole("region")).toHaveLength(2);
    await user.click(screen.getByRole("button", { name: "Load more" }));
    expect(mocks.query.fetchNextPage).toHaveBeenCalledTimes(1);
  });
});
