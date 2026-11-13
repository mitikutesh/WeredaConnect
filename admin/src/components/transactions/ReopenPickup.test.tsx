import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { ReopenExit } from "@/lib/pickup";

import { ReopenPickup } from "./ReopenPickup";

/* FUP-287: the re-open of a judged pickup. The mutation hook is mocked; the English strings are real, so the tests
 * also prove the pickup.reopen.* keys the form reads exist. */

type MutateOptions = {
  onSuccess?: (result: { alreadyApplied?: boolean; noShowEvent?: string }) => void;
  onError?: (err: unknown) => void;
};

const mocks = vi.hoisted(() => ({
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useReopenPickup: () => ({ mutate: mocks.mutate, isPending: false }),
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

const REASON = "Called both parties: the crops left the farm on time.";

function renderIt(exits: ReopenExit[] = ["pending", "handoff_confirmed"]) {
  render(<ReopenPickup orderId="ord-9" version={7} exits={exits} />);
}

describe("ReopenPickup (FUP-287)", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
  });

  it("re-opens to pending with the chosen deadline and the trimmed reason, after a confirm", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ alreadyApplied: false }));
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Re-open the pickup" }));
    expect(screen.getByRole("radio", { name: /Back to pending with a new deadline/ })).toHaveProperty("checked", true);
    await user.selectOptions(screen.getByLabelText("New deadline"), "48");
    await user.type(screen.getByLabelText("How you reconciled it"), `  ${REASON}  `);
    await user.click(screen.getByRole("button", { name: "Review" }));

    expect(screen.getByText("Re-open the pickup with a new deadline 48 hours from now?")).toBeTruthy();
    expect(screen.getByText(`How you reconciled it: ${REASON}`)).toBeTruthy();
    expect(mocks.mutate).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Yes, re-open" }));

    expect(mocks.mutate).toHaveBeenCalledWith(
      { orderId: "ord-9", exit: "pending", reason: REASON, expectedVersion: 7, deadlineHours: 48 },
      expect.anything(),
    );
    expect(screen.getByRole("status").textContent).toBe("The pickup is open again until the new deadline.");
  });

  it("confirms the handoff without a deadline", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({}));
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Re-open the pickup" }));
    await user.click(screen.getByRole("radio", { name: /Handoff confirmed/ }));
    expect(screen.queryByLabelText("New deadline")).toBeNull();
    await user.type(screen.getByLabelText("How you reconciled it"), REASON);
    await user.click(screen.getByRole("button", { name: "Review" }));
    expect(screen.getByText("Record the handoff as picked up?")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Yes, re-open" }));

    expect(mocks.mutate).toHaveBeenCalledWith(
      { orderId: "ord-9", exit: "handoff_confirmed", reason: REASON, expectedVersion: 7 },
      expect.anything(),
    );
    expect(screen.getByRole("status").textContent).toBe("The handoff is recorded as picked up.");
  });

  it("offers only the exits the server named, and nothing when it named none", async () => {
    const user = userEvent.setup();
    const { unmount } = render(<ReopenPickup orderId="ord-9" version={7} exits={["pending"]} />);
    await user.click(screen.getByRole("button", { name: "Re-open the pickup" }));
    expect(screen.getAllByRole("radio")).toHaveLength(1);
    expect(screen.queryByRole("radio", { name: /Handoff confirmed/ })).toBeNull();
    unmount();

    renderIt([]);
    expect(screen.queryByRole("button", { name: "Re-open the pickup" })).toBeNull();
  });

  it("needs a reason of 10 to 500 characters before the review", async () => {
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Re-open the pickup" }));
    const review = screen.getByRole("button", { name: "Review" });
    expect(review).toHaveProperty("disabled", true);
    await user.type(screen.getByLabelText("How you reconciled it"), "  too short ");
    expect(review).toHaveProperty("disabled", true);
    await user.type(screen.getByLabelText("How you reconciled it"), "  and now long enough");
    expect(review).toHaveProperty("disabled", false);
  });

  it("says a replay was already applied", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ alreadyApplied: true }));
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Re-open the pickup" }));
    await user.type(screen.getByLabelText("How you reconciled it"), REASON);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, re-open" }));

    expect(screen.getByRole("status").textContent).toBe("This re-open was already applied.");
  });

  it.each([
    ["PICKUP_REOPEN_CHANGED", "The order, its delivery or its payment changed since the page was loaded."],
    ["PICKUP_REOPEN_NOT_ALLOWED", "This judgement cannot be re-opened any more."],
    ["PICKUP_REOPEN_MONEY_NOT_HELD", "The payment is no longer simply held"],
    ["SOMETHING_ELSE", "Action failed"],
  ])("shows %s in words and keeps the form", async (code, text) => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onError?.({ code, message: "server text" }));
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Re-open the pickup" }));
    await user.type(screen.getByLabelText("How you reconciled it"), REASON);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, re-open" }));

    expect(screen.getByRole("alert").textContent).toContain(text);
    expect(screen.getByLabelText("How you reconciled it")).toBeTruthy();
  });
});

/* FUP-330: the same form re-opens a judged trucker no-show through reopen-no-show, says the trip booking is taken
 * back and the no-show excused, and tells what happened to the trucker's NO_SHOW. */
describe("ReopenPickup for a trucker no-show (FUP-330)", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
  });

  function renderNoShow(exits: ReopenExit[] = ["pending", "handoff_confirmed"]) {
    render(<ReopenPickup orderId="ord-9" version={8} exits={exits} kind="noShow" />);
  }

  it("re-opens the no-show with a new window through its own action, and says the no-show is excused", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ alreadyApplied: false, noShowEvent: "excused" }));
    const user = userEvent.setup();
    renderNoShow();

    expect(screen.queryByRole("button", { name: "Re-open the pickup" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Re-open the no-show" }));
    expect(screen.getByText("Re-open this trucker no-show")).toBeTruthy();
    expect(screen.getByText(/the trucker creates a new delivery and scans the farmer's QR/)).toBeTruthy();
    expect(screen.getByText(/check-ins are cleared \(the audit trail keeps them\)/)).toBeTruthy();
    await user.selectOptions(screen.getByLabelText("New deadline"), "72");
    await user.type(screen.getByLabelText("How you reconciled it"), REASON);
    await user.click(screen.getByRole("button", { name: "Review" }));

    expect(
      screen.getByText(
        "Re-open the no-show with a new deadline 72 hours from now, take the trip booking back and excuse the trucker's no-show?",
      ),
    ).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Yes, re-open" }));

    expect(mocks.mutate).toHaveBeenCalledWith(
      { orderId: "ord-9", action: "reopen-no-show", exit: "pending", reason: REASON, expectedVersion: 8, deadlineHours: 72 },
      expect.anything(),
    );
    expect(screen.getByRole("status").textContent).toBe(
      "The no-show is re-opened: the trucker has a new window until the new deadline. The trucker's no-show is excused.",
    );
  });

  it("confirms the handoff, and says when no no-show was recorded", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ noShowEvent: "not_recorded" }));
    const user = userEvent.setup();
    renderNoShow();

    await user.click(screen.getByRole("button", { name: "Re-open the no-show" }));
    await user.click(screen.getByRole("radio", { name: /Handoff confirmed/ }));
    expect(screen.getByText(/the cancelled delivery is marked picked up/)).toBeTruthy();
    await user.type(screen.getByLabelText("How you reconciled it"), REASON);
    await user.click(screen.getByRole("button", { name: "Review" }));
    expect(screen.getByText("Record the handoff as picked up, take the trip booking back and excuse the trucker's no-show?")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Yes, re-open" }));

    expect(mocks.mutate).toHaveBeenCalledWith(
      { orderId: "ord-9", action: "reopen-no-show", exit: "handoff_confirmed", reason: REASON, expectedVersion: 8 },
      expect.anything(),
    );
    expect(screen.getByRole("status").textContent).toContain("The handoff is recorded as picked up and the trip booking is back.");
    expect(screen.getByRole("status").textContent).toContain("No no-show is recorded for this order");
  });

  it("shows a trip that cannot take the order back in words", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onError?.({ code: "PICKUP_REOPEN_TRIP_UNAVAILABLE" }));
    const user = userEvent.setup();
    renderNoShow(["pending"]);

    await user.click(screen.getByRole("button", { name: "Re-open the no-show" }));
    await user.type(screen.getByLabelText("How you reconciled it"), REASON);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, re-open" }));

    expect(screen.getByRole("alert").textContent).toContain("The trip cannot take the order back");
  });

  it("offers nothing once the buyer chose (no exits)", () => {
    renderNoShow([]);
    expect(screen.queryByRole("button", { name: "Re-open the no-show" })).toBeNull();
  });
});
