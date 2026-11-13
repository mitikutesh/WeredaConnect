import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { t as translate, type Locale } from "@/lib/i18n";
import { CancelDelivery } from "./CancelDelivery";

/* FUP-19 (spec E9): the admin's cancel of a delivery after pickup. The mutation hook is mocked; the English strings
 * are real, so the tests also prove every cancelDelivery.* key exists. */

type MutateOptions = {
  onSuccess?: (result: { alreadyCancelled?: boolean }) => void;
  onError?: (err: unknown) => void;
};

const mocks = vi.hoisted(() => ({
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useCancelDelivery: () => ({ mutate: mocks.mutate, isPending: false }),
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

const REASON = "Truck broke down; the farmer took the goods back.";

function renderIt(status = "inTransit", escrowStatus: string | null = "refunded") {
  render(<CancelDelivery deliveryId="del-19" status={status} version={3} escrowStatus={escrowStatus} />);
}

describe("CancelDelivery (FUP-19)", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
  });

  it("asks for the reason, confirms before sending, then sends it with the version the page read", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ alreadyCancelled: false }));
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Cancel delivery" }));
    const review = screen.getByRole("button", { name: "Review" }) as HTMLButtonElement;
    expect(review.disabled).toBe(true);
    await user.type(screen.getByLabelText("Why the delivery is cancelled"), `  ${REASON}  `);
    await user.click(review);

    expect(screen.getByText("Cancel this delivery? This cannot be undone.")).toBeTruthy();
    expect(screen.getByText(`Why the delivery is cancelled: ${REASON}`)).toBeTruthy();
    expect(mocks.mutate).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Yes, cancel the delivery" }));

    expect(mocks.mutate).toHaveBeenCalledWith({ deliveryId: "del-19", reason: REASON, expectedVersion: 3 }, expect.anything());
    expect(screen.getByRole("status").textContent).toBe("Delivery cancelled.");
  });

  it("says when the delivery was already cancelled by the same action", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ alreadyCancelled: true }));
    const user = userEvent.setup();
    renderIt("pickedUp");

    await user.click(screen.getByRole("button", { name: "Cancel delivery" }));
    await user.type(screen.getByLabelText("Why the delivery is cancelled"), REASON);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, cancel the delivery" }));

    expect(screen.getByRole("status").textContent).toBe("This delivery was already cancelled.");
  });

  it.each([
    ["DELIVERY_ADMIN_CANCEL_MONEY_HELD", "The payment is still held."],
    ["DELIVERY_ADMIN_CANCEL_CHANGED", "changed since the page was loaded"],
    ["DELIVERY_ADMIN_CANCEL_NOT_ALLOWED", "cannot be cancelled here"],
    ["SOMETHING_ELSE", "Action failed. Please try again."],
  ])("shows the refusal %s in words and keeps the form", async (code, words) => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onError?.({ code }));
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Cancel delivery" }));
    await user.type(screen.getByLabelText("Why the delivery is cancelled"), REASON);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, cancel the delivery" }));

    expect(screen.getByRole("alert").textContent).toContain(words);
    expect(screen.getByLabelText("Why the delivery is cancelled")).toBeTruthy();
  });

  it("keeps a too short reason from being reviewed", async () => {
    const user = userEvent.setup();
    renderIt();

    await user.click(screen.getByRole("button", { name: "Cancel delivery" }));
    await user.type(screen.getByLabelText("Why the delivery is cancelled"), "  too short  ");

    expect((screen.getByRole("button", { name: "Review" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("offers nothing while the payment is still held, and says to decide it first", () => {
    renderIt("inTransit", "escrowed");

    expect(screen.queryByRole("button", { name: "Cancel delivery" })).toBeNull();
    expect(screen.getByText(/The payment is still held/)).toBeTruthy();
  });

  it("offers the action when the order has no escrow at all", () => {
    renderIt("pickedUp", null);

    expect(screen.getByRole("button", { name: "Cancel delivery" })).toBeTruthy();
  });

  it.each(["pending", "delivered", "disputed", "cancelled"])("offers nothing on a %s delivery", (status) => {
    renderIt(status);

    expect(screen.queryByRole("button", { name: "Cancel delivery" })).toBeNull();
  });

  it.each<Locale>(["en", "am", "om"])("has every string in %s", (locale) => {
    const keys = [
      "action", "title", "hint", "moneyFirst", "reason", "reasonHint", "review", "confirmQuestion", "confirm", "back",
      "cancel", "done", "already",
    ].map((k) => `cancelDelivery.${k}`);
    const codes = ["INVALID", "NOT_ALLOWED", "MONEY_HELD", "CHANGED"].map((c) => `errors.DELIVERY_ADMIN_CANCEL_${c}`);
    for (const key of [...keys, ...codes, "transactions.action_admin_delivery_cancelled"]) {
      expect(translate(key, locale)).not.toBe(key);
    }
  });
});
