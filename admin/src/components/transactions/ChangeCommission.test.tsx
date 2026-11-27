import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { CommissionChangeResult } from "@/hooks/useQuery";

import { ChangeCommission } from "./ChangeCommission";

/* The admin commission change of one unpaid order (order-lifecycle.md 2.26.0). The mutation hook is mocked; the
 * English strings are real, so the tests also prove the commission.* keys the form reads exist. */

type MutateOptions = {
  onSuccess?: (result: CommissionChangeResult) => void;
  onError?: (err: unknown) => void;
};

const mocks = vi.hoisted(() => ({
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useChangeCommission: () => ({ mutate: mocks.mutate, isPending: false }),
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

const REASON = "Agreed with the buyer on the phone: a large first order.";
const RATE_LABEL = "New commission rate (percent)";
const REASON_LABEL = "Why the commission changes";

type Props = Parameters<typeof ChangeCommission>[0];

function renderIt(props: Partial<Props> = {}) {
  render(
    <ChangeCommission
      orderId="ord-7"
      version={4}
      cropCost={1000}
      transportCost={200}
      commissionRate={0.05}
      commissionAmount={50}
      orderTotal={1250}
      {...props}
    />,
  );
}

async function fillForm(user: ReturnType<typeof userEvent.setup>, rate = "2.5", reason = REASON) {
  await user.click(screen.getByRole("button", { name: "Change commission" }));
  await user.type(screen.getByLabelText(RATE_LABEL), rate);
  await user.type(screen.getByLabelText(REASON_LABEL), reason);
}

describe("ChangeCommission", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
  });

  it("previews the new commission and total, confirms, and sends the rate fraction, the trimmed reason and the version", async () => {
    mocks.mutate.mockImplementation((_vars, opts) =>
      opts?.onSuccess?.({ alreadyApplied: false, commissionRate: 0.025, commissionAmount: 25, orderTotal: 1225 }),
    );
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Change commission" }));
    expect(screen.getByText("Now: 5%, commission 50.00 Birr, order total 1,250.00 Birr")).toBeTruthy();
    await user.type(screen.getByLabelText(RATE_LABEL), "2.5");
    expect(screen.getByText("New commission 25.00 Birr, new order total 1,225.00 Birr")).toBeTruthy();
    await user.type(screen.getByLabelText(REASON_LABEL), `  ${REASON}  `);
    await user.click(screen.getByRole("button", { name: "Review" }));

    expect(
      screen.getByText("Change the commission from 5% to 2.5%? The order total goes from 1,250.00 Birr to 1,225.00 Birr."),
    ).toBeTruthy();
    expect(screen.getByText(`Why the commission changes: ${REASON}`)).toBeTruthy();
    expect(screen.getByText(/The buyer is told in the app inbox and by a push message/)).toBeTruthy();
    expect(mocks.mutate).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Yes, change it" }));

    expect(mocks.mutate).toHaveBeenCalledWith(
      { orderId: "ord-7", commissionRate: 0.025, reason: REASON, expectedVersion: 4 },
      expect.anything(),
    );
    expect(screen.getByRole("status").textContent).toBe(
      "The commission is now 2.5% and the order total 1,225.00 Birr. The buyer is told.",
    );
    expect(screen.getByRole("button", { name: "Change commission" })).toBeTruthy();
  });

  it("rounds the preview as the server does (18.99 x 170 kg at 5%)", async () => {
    const user = userEvent.setup();
    renderIt({ cropCost: 18.99 * 170, transportCost: 150, commissionRate: 0.025, commissionAmount: 80.71, orderTotal: 3459.01 });

    await user.click(screen.getByRole("button", { name: "Change commission" }));
    await user.type(screen.getByLabelText(RATE_LABEL), "5");

    expect(screen.getByText("New commission 161.42 Birr, new order total 3,539.72 Birr")).toBeTruthy();
  });

  it("sends a rate of 0 and a rate of 18.99 as exact fractions", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({}));
    const user = userEvent.setup();
    renderIt();
    await fillForm(user, "0");
    expect(screen.getByText("New commission 0.00 Birr, new order total 1,200.00 Birr")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, change it" }));
    expect(mocks.mutate).toHaveBeenLastCalledWith(expect.objectContaining({ commissionRate: 0 }), expect.anything());

    await fillForm(user, "18.99");
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, change it" }));
    expect(mocks.mutate).toHaveBeenLastCalledWith(expect.objectContaining({ commissionRate: 0.1899 }), expect.anything());
    // Without the server's numbers the notice uses the rate sent and the previewed total.
    expect(screen.getByRole("status").textContent).toBe(
      "The commission is now 18.99% and the order total 1,389.90 Birr. The buyer is told.",
    );
  });

  it("keeps Review disabled until the rate is a new valid percent and the reason is 10 to 500 characters", async () => {
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Change commission" }));
    const review = screen.getByRole("button", { name: "Review" });
    const rate = screen.getByLabelText(RATE_LABEL);
    await user.type(screen.getByLabelText(REASON_LABEL), REASON);
    expect(review).toHaveProperty("disabled", true);

    for (const text of ["25", "20.01", "2.555", "-1", "abc"]) {
      await user.clear(rate);
      await user.type(rate, text);
      expect(screen.getByText("Type a number from 0 to 20 with at most 2 decimals.")).toBeTruthy();
      expect(rate.getAttribute("aria-invalid")).toBe("true");
      expect(review).toHaveProperty("disabled", true);
    }

    await user.clear(rate);
    await user.type(rate, "5");
    expect(screen.getByText("This is the rate the order has already.")).toBeTruthy();
    expect(review).toHaveProperty("disabled", true);

    await user.clear(rate);
    await user.type(rate, "20");
    expect(rate.getAttribute("aria-invalid")).toBe("false");
    expect(review).toHaveProperty("disabled", false);

    await user.clear(screen.getByLabelText(REASON_LABEL));
    await user.type(screen.getByLabelText(REASON_LABEL), "  too short ");
    expect(review).toHaveProperty("disabled", true);
    await user.type(screen.getByLabelText(REASON_LABEL), " and now long enough");
    expect(review).toHaveProperty("disabled", false);
  });

  it("says a replay was already applied", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ alreadyApplied: true, commissionRate: 0.025 }));
    const user = userEvent.setup();
    renderIt();

    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, change it" }));

    expect(screen.getByRole("status").textContent).toBe("This change was already applied.");
  });

  it.each([
    ["COMMISSION_CHANGE_NOT_ALLOWED", "This order cannot get another commission now"],
    ["COMMISSION_CHANGE_CHANGED", "The order changed since the page was loaded, so nothing was changed."],
    ["COMMISSION_CHANGE_INVALID", "Check the form: the rate must be 0 to 20 percent"],
    ["ADMIN_012", "Transaction not found."],
    ["SOMETHING_ELSE", "Action failed"],
  ])("shows %s in words and keeps the form as it was", async (code, text) => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onError?.({ code, message: "server text" }));
    const user = userEvent.setup();
    renderIt();

    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, change it" }));

    expect(screen.getByRole("alert").textContent).toContain(text);
    expect(screen.getByLabelText(RATE_LABEL)).toHaveProperty("value", "2.5");
    expect(screen.getByLabelText(REASON_LABEL)).toHaveProperty("value", REASON);
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("goes back from the confirm with the values kept, and Cancel clears them", async () => {
    const user = userEvent.setup();
    renderIt();

    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Back" }));
    expect(screen.getByLabelText(RATE_LABEL)).toHaveProperty("value", "2.5");

    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(mocks.mutate).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Change commission" }));
    expect(screen.getByLabelText(RATE_LABEL)).toHaveProperty("value", "");
    expect(screen.getByLabelText(REASON_LABEL)).toHaveProperty("value", "");
  });

  it("works from the keyboard, moving focus to each step", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ commissionRate: 0.025, orderTotal: 1225 }));
    const user = userEvent.setup();
    renderIt();

    await user.tab();
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Change commission" }));
    await user.keyboard("{Enter}");
    const rate = screen.getByLabelText(RATE_LABEL);
    expect(rate.getAttribute("inputmode")).toBe("decimal");
    expect(document.activeElement).toBe(rate);
    await user.keyboard("2.5");
    await user.tab();
    await user.keyboard(REASON);
    await user.tab();
    await user.keyboard("{Enter}");

    expect(document.activeElement?.textContent).toContain("Change the commission from 5% to 2.5%?");
    await user.tab();
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Yes, change it" }));
    await user.keyboard("{Enter}");

    expect(mocks.mutate).toHaveBeenCalledTimes(1);
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Change commission" }));
  });

  it("says when the order carries no rate", async () => {
    const user = userEvent.setup();
    renderIt({ commissionRate: null });

    await user.click(screen.getByRole("button", { name: "Change commission" }));

    expect(screen.getByText("Now: N/A, commission 50.00 Birr, order total 1,250.00 Birr")).toBeTruthy();
  });
});
