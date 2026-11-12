import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { CorrectReference } from "./CorrectReference";

/* FUP-218: the correction of a payout reference recorded by mistake. The mutation hook is mocked; the English
 * strings are real, so the tests also prove every correctRef.* key exists. */

type MutateOptions = {
  onSuccess?: (result: { alreadyCorrected?: boolean }) => void;
  onError?: (err: unknown) => void;
};

const mocks = vi.hoisted(() => ({
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useCorrectPayoutReference: () => ({ mutate: mocks.mutate, isPending: false }),
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

const REASON = "Typed the portal id of the other refund.";

function renderIt() {
  render(<CorrectReference escrowId="escrow-order-ord-1" legRef="release#farmer" currentRef="TB-B-PAYMENT" />);
}

async function fillForm(user: ReturnType<typeof userEvent.setup>, newRef = "TB-A-PAYMENT", reason = REASON) {
  await user.click(screen.getByRole("button", { name: "Correct reference" }));
  if (newRef) await user.type(screen.getByLabelText("Correct Telebirr transaction ID"), newRef);
  if (reason) await user.type(screen.getByLabelText("Why the recorded ID is wrong"), reason);
}

describe("CorrectReference (FUP-218)", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
  });

  it("asks for the new ID and the reason, shows both IDs before sending, then sends them", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ alreadyCorrected: false }));
    const user = userEvent.setup();
    renderIt();

    await fillForm(user, " TB-A-PAYMENT ", `  ${REASON}  `);
    expect(screen.getByText("TB-B-PAYMENT")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Review" }));

    // The confirm step names the old and the new ID and the reason; nothing is sent yet.
    expect(screen.getByText("Replace TB-B-PAYMENT with TB-A-PAYMENT on this leg?")).toBeTruthy();
    expect(screen.getByText(`Why the recorded ID is wrong: ${REASON}`)).toBeTruthy();
    expect(mocks.mutate).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Yes, correct it" }));

    expect(mocks.mutate).toHaveBeenCalledWith(
      {
        escrowId: "escrow-order-ord-1",
        ref: "release#farmer",
        oldExternalRef: "TB-B-PAYMENT",
        externalRef: "TB-A-PAYMENT",
        reason: REASON,
      },
      expect.anything(),
    );
    expect(screen.getByRole("status").textContent).toBe("Reference corrected.");
    expect(screen.getByRole("button", { name: "Correct reference" })).toBeTruthy();
  });

  it("goes back from the confirm step without sending", async () => {
    const user = userEvent.setup();
    renderIt();

    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Back" }));

    expect((screen.getByLabelText("Correct Telebirr transaction ID") as HTMLInputElement).value).toBe("TB-A-PAYMENT");
    expect(mocks.mutate).not.toHaveBeenCalled();
  });

  it.each([
    ["the same ID in another case", "tb-b-payment", REASON, "Enter an ID that differs from the recorded one."],
    ["a malformed ID", "TB A", REASON, "Enter the Telebirr transaction ID: 4 to 64 letters, digits or . _ : / -"],
    ["a reason under 10 characters", "TB-A-PAYMENT", "too short", null],
  ])("does not let the admin review %s", async (_label, newRef, reason, problem) => {
    const user = userEvent.setup();
    renderIt();

    await fillForm(user, newRef, reason);

    expect((screen.getByRole("button", { name: "Review" }) as HTMLButtonElement).disabled).toBe(true);
    if (problem) expect(screen.getByText(problem)).toBeTruthy();
  });

  it("names the leg that holds the new ID when the server refuses it", async () => {
    mocks.mutate.mockImplementation((_vars, opts) =>
      opts?.onError?.({
        code: "ESCROW_047",
        message: "This reference is already recorded on escrow escrow-order-ord-2, leg buyer.",
      }),
    );
    const user = userEvent.setup();
    renderIt();

    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, correct it" }));

    expect(screen.getByRole("alert").textContent).toBe(
      "This Telebirr transaction ID is already recorded on escrow escrow-order-ord-2, leg buyer. Check the ID in the portal: one payment cannot be recorded twice.",
    );
    // Back on the form with what was typed, so the admin can fix the ID.
    expect((screen.getByLabelText("Correct Telebirr transaction ID") as HTMLInputElement).value).toBe("TB-A-PAYMENT");
  });

  it.each([
    ["ESCROW_036", "This leg changed since the page was loaded. Reload and check its ID before you try again."],
    ["ESCROW_035", "This leg cannot be corrected here: it is not recorded yet, or the gateway paid it."],
    ["ESCROW_048", "Give a reason of 10 to 500 characters for the correction."],
    ["SOMETHING_NEW", "Action failed. Please try again."],
  ])("says what %s means", async (code, text) => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onError?.({ code, message: "server text" }));
    const user = userEvent.setup();
    renderIt();

    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, correct it" }));

    expect(screen.getByRole("alert").textContent).toBe(text);
  });

  it("says so when the leg already carried the new ID", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ alreadyCorrected: true }));
    const user = userEvent.setup();
    renderIt();

    await fillForm(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, correct it" }));

    expect(screen.getByRole("status").textContent).toBe("This leg already carries that ID.");
  });
});
