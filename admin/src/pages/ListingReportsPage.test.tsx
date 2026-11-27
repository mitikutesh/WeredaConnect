import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ListingReportsPage } from "./ListingReportsPage";

/* FUP-67 listing reports page. The hooks are mocked; the English strings are real, so the tests
 * also prove every listingReports.* key the page reads exists. */

type MutateOptions = {
  onSuccess?: (result: unknown) => void;
  onError?: (err: unknown) => void;
  onSettled?: () => void;
};

const mocks = vi.hoisted(() => ({
  reports: {
    data: undefined as unknown,
    isLoading: false,
    isError: false,
    hasNextPage: false,
    isFetchingNextPage: false,
    fetchNextPage: vi.fn(),
  },
  statuses: [] as string[],
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useListingReports: (status: string) => {
    mocks.statuses.push(status);
    return mocks.reports;
  },
  useReviewListingReports: () => ({ mutate: mocks.mutate, isPending: false }),
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

function report(overrides: Record<string, unknown> = {}) {
  return {
    listingId: "crop-1",
    listingType: "crop",
    ownerId: "farmer-1",
    reporterId: "buyer-1",
    reason: "contact_info",
    status: "open",
    createdAt: "2026-11-07T09:30:15.123Z",
    ...overrides,
  };
}

const cropListing = {
  listingId: "crop-1",
  listingType: "crop",
  found: true,
  status: "available",
  ownerName: "Abebe",
  cropType: "teff",
  description: "White teff, call me",
  audioNoteRef: "audio/farmer-1/crop-1.m4a",
};

/* useListingReports is an infinite query: the page reads data.pages. */
function pages(...list: Record<string, unknown>[]) {
  return { pages: list, pageParams: list.map(() => null) };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <ListingReportsPage />
    </MemoryRouter>,
  );
}

describe("ListingReportsPage", () => {
  beforeEach(() => {
    mocks.reports.data = undefined;
    mocks.reports.isLoading = false;
    mocks.reports.isError = false;
    mocks.reports.hasNextPage = false;
    mocks.statuses.length = 0;
    mocks.mutate.mockReset();
  });

  it("groups the reports of one listing, with the listing and its unscreened voice note", () => {
    mocks.reports.data = pages({
      reports: [
        report(),
        report({ reporterId: "buyer-2", reason: "scam" }),
        report({ listingId: "trip-1", listingType: "trip", ownerId: "trucker-1", reason: "misleading" }),
      ],
      listings: [
        cropListing,
        { listingId: "trip-1", listingType: "trip", found: false },
      ],
    });

    renderPage();

    const crop = screen.getByRole("region", { name: "Crop listing crop-1" });
    expect(within(crop).getByText("2 report(s)")).toBeTruthy();
    expect(within(crop).getByText("teff")).toBeTruthy();
    expect(within(crop).getByText("White teff, call me")).toBeTruthy();
    expect(within(crop).getByText("Voice note (not screened): audio/farmer-1/crop-1.m4a")).toBeTruthy();
    expect(within(crop).getByText("Phone number or contact")).toBeTruthy();
    expect(within(crop).getByText("Looks like a scam")).toBeTruthy();
    expect(within(crop).getByRole("link", { name: "Abebe" }).getAttribute("href")).toBe("/users/farmer-1");
    expect(within(crop).getByRole("link", { name: "buyer-2" }).getAttribute("href")).toBe("/users/buyer-2");

    const trip = screen.getByRole("region", { name: "Trip trip-1" });
    expect(within(trip).getByText("This listing no longer exists, or could not be read.")).toBeTruthy();
    expect(within(trip).getByText("1 report(s)")).toBeTruthy();
  });

  it("decides a listing's reports with the note and says how many", async () => {
    mocks.reports.data = pages({ reports: [report(), report({ reporterId: "buyer-2" })], listings: [cropListing] });
    mocks.mutate.mockImplementation((_vars, opts) => {
      opts?.onSuccess?.({ listingId: "crop-1", decision: "actioned", reviewed: 2, reviewedAt: "t" });
      opts?.onSettled?.();
    });
    const user = userEvent.setup();

    renderPage();
    const crop = screen.getByRole("region", { name: "Crop listing crop-1" });
    await user.type(within(crop).getByLabelText("Note for the audit log (optional)"), "Owner suspended");
    await user.click(within(crop).getByRole("button", { name: "Mark actioned" }));

    expect(mocks.mutate).toHaveBeenCalledWith(
      { listingId: "crop-1", decision: "actioned", note: "Owner suspended" },
      expect.anything(),
    );
    expect(screen.getByRole("status").textContent).toBe("2 report(s) marked actioned.");
  });

  it("dismisses without a note, and says when another admin decided first", async () => {
    mocks.reports.data = pages({ reports: [report()], listings: [cropListing] });
    mocks.mutate.mockImplementation((_vars, opts) => {
      opts?.onSuccess?.({ listingId: "crop-1", decision: "dismissed", reviewed: 0, reviewedAt: "t" });
      opts?.onSettled?.();
    });
    const user = userEvent.setup();

    renderPage();
    await user.click(screen.getByRole("button", { name: "Dismiss" }));

    expect(mocks.mutate).toHaveBeenCalledWith(
      { listingId: "crop-1", decision: "dismissed", note: undefined },
      expect.anything(),
    );
    expect(screen.getByRole("status").textContent).toBe("These reports were already decided. Reload the page.");
  });

  it("shows a refused review", async () => {
    mocks.reports.data = pages({ reports: [report()], listings: [cropListing] });
    mocks.mutate.mockImplementation((_vars, opts) => {
      opts?.onError?.({ code: "ADMIN_009" });
      opts?.onSettled?.();
    });
    const user = userEvent.setup();

    renderPage();
    await user.click(screen.getByRole("button", { name: "Dismiss" }));

    expect(screen.getByRole("alert").textContent).toBe("Unknown action.");
  });

  it("reads the decided tabs without review buttons", async () => {
    mocks.reports.data = pages({
      reports: [report({ status: "dismissed", reviewedAt: "2026-11-07T12:00:00.000Z", reviewNote: "Fine" })],
      listings: [cropListing],
    });
    const user = userEvent.setup();

    renderPage();
    await user.click(screen.getByRole("button", { name: "Dismissed" }));

    expect(mocks.statuses).toContain("dismissed");
    expect(screen.queryByRole("button", { name: "Mark actioned" })).toBeNull();
    expect(screen.getByText(/: Fine$/)).toBeTruthy();
  });

  it("shows the empty open queue and the load failure", () => {
    mocks.reports.data = pages({ reports: [], listings: [] });
    const { unmount } = renderPage();
    expect(screen.getByText("No open reports.")).toBeTruthy();
    unmount();

    mocks.reports.data = undefined;
    mocks.reports.isError = true;
    renderPage();
    expect(screen.getByRole("alert").textContent).toBe("The reports could not be loaded. Reload the page.");
  });

  it("loads the next page", async () => {
    mocks.reports.data = pages({ reports: [report()], listings: [cropListing] });
    mocks.reports.hasNextPage = true;
    const user = userEvent.setup();

    renderPage();
    await user.click(screen.getByRole("button", { name: "Load more" }));

    expect(mocks.reports.fetchNextPage).toHaveBeenCalled();
  });
});
