import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { UserDetailPage } from "./UserDetailPage";

/* FRD-05: the Fraud Review page has its own wording for ADMIN_006 and ADMIN_007; the user page keeps
 * the shared errors.* text for its own answers. The hooks are mocked; the English strings are real. */

type MutateOptions = { onError?: (err: unknown) => void };

const mocks = vi.hoisted(() => ({
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
  detail: { user: { userId: "user-1", displayName: "Abebe", role: "farmer", suspended: false } } as Record<
    string,
    unknown
  >,
}));

vi.mock("@/hooks/useQuery", () => ({
  useUserDetail: () => ({ data: mocks.detail, isLoading: false }),
  useUserAction: () => ({ mutate: mocks.mutate, isPending: false }),
}));

/* ESC-32: the reputation panel has its own tests (ReputationPanel.test.tsx); here it only has to be on the page. */
vi.mock("@/components/reputation/ReputationPanel", () => ({
  ReputationPanel: ({ userId }: { userId: string }) => <div data-testid="reputation-panel">{userId}</div>,
}));

function renderPage() {
  render(
    <MemoryRouter initialEntries={["/users/user-1"]}>
      <Routes>
        <Route path="/users/:id" element={<UserDetailPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

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

describe("UserDetailPage", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
    mocks.detail = { user: { userId: "user-1", displayName: "Abebe", role: "farmer", suspended: false } };
  });

  /* FRD-07: the suspend reason is admin-only; the server sends it next to the user. */
  it("shows why a suspended account is suspended, labelling the Fraud Review reject", () => {
    mocks.detail = {
      user: { userId: "user-1", displayName: "Abebe", role: "farmer", suspended: true },
      suspendReason: "fraud_confirmed",
    };
    renderPage();

    expect(screen.getByText("Suspend reason")).toBeTruthy();
    expect(screen.getByText("Fraud confirmed in Fraud Review")).toBeTruthy();
  });

  it("shows a free-text reason as the admin typed it", () => {
    mocks.detail = {
      user: { userId: "user-1", displayName: "Abebe", role: "farmer", suspended: true },
      suspendReason: "reported by phone",
    };
    renderPage();

    expect(screen.getByText("reported by phone")).toBeTruthy();
  });

  it("shows the reputation panel of the user on the page", () => {
    renderPage();

    expect(screen.getByTestId("reputation-panel").textContent).toBe("user-1");
  });

  it("shows no reason for an active account", () => {
    mocks.detail = { user: { userId: "user-1", displayName: "Abebe", role: "farmer", suspended: false } };
    renderPage();

    expect(screen.queryByText("Suspend reason")).toBeNull();
  });

  it("shows the shared text for a user that was not found, not the Fraud Review wording", async () => {
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={["/users/user-1"]}>
        <Routes>
          <Route path="/users/:id" element={<UserDetailPage />} />
        </Routes>
      </MemoryRouter>,
    );

    await user.click(screen.getByRole("button", { name: "Verify" }));
    const [, opts] = mocks.mutate.mock.calls[0];
    act(() => opts?.onError?.({ code: "ADMIN_007", status: 404 }));

    expect(screen.getByText("Action failed. Please try again.")).toBeTruthy();
    expect(screen.queryByText(/signal/)).toBeNull();
  });

  /* FUP-317: the name history from the profile edit audit rows, newest first as the server sends it. */
  describe("profile history", () => {
    const liveUser = {
      userId: "user-1",
      displayName: "Abebe Kebede Tola",
      phoneNumber: "+251911000001",
      role: "buyer",
      suspended: false,
    };

    function historyItems() {
      const list = screen.getByRole("list", { name: "Profile history" });
      return within(list).getAllByRole("listitem");
    }

    it("lists each edit with the old and the new value of every changed field", () => {
      mocks.detail = {
        user: liveUser,
        profileHistory: [
          {
            editedAt: "2026-11-13T09:00:00.000Z",
            actor: "user-1",
            changes: [
              { field: "displayName", from: "Abebe K.", to: "Abebe Kebede Tola" },
              { field: "companyName", from: "Old Traders", to: "Meki Grain Traders" },
            ],
          },
          {
            editedAt: "2026-11-01T09:00:00.000Z",
            actor: "user-1",
            changes: [
              { field: "displayName", from: "Abebe", to: "Abebe K." },
              { field: "woreda", from: "Adama", to: "Meki" },
            ],
          },
        ],
        profileHistoryMore: false,
      };
      renderPage();

      const items = historyItems();
      expect(items).toHaveLength(2);
      expect(items[0].textContent).toContain("Name");
      expect(items[0].textContent).toContain("Abebe K. → Abebe Kebede Tola");
      expect(items[0].textContent).toContain("Company name");
      expect(items[0].textContent).toContain("Old Traders → Meki Grain Traders");
      expect(items[0].textContent).toContain("13 Nov 2026");
      expect(items[1].textContent).toContain("Abebe → Abebe K.");
      expect(items[1].textContent).toContain("Woreda");
      expect(items[1].textContent).toContain("Adama → Meki");
      expect(screen.queryByText("Only the latest 50 edits are shown.")).toBeNull();
    });

    it("shows (none) for a field the account never had", () => {
      mocks.detail = {
        user: liveUser,
        profileHistory: [{ editedAt: "2026-11-13T09:00:00.000Z", actor: "user-1", changes: [{ field: "woreda", to: "Meki" }] }],
        profileHistoryMore: false,
      };
      renderPage();

      expect(historyItems()[0].textContent).toContain("(none) → Meki");
    });

    it("says when only the latest 50 edits are listed", () => {
      mocks.detail = {
        user: liveUser,
        profileHistory: [{ editedAt: "2026-11-13T09:00:00.000Z", actor: "user-1", changes: [] }],
        profileHistoryMore: true,
      };
      renderPage();

      expect(screen.getByText("Only the latest 50 edits are shown.")).toBeTruthy();
      expect(screen.getByText("Details not available.")).toBeTruthy();
    });

    it("says there are no edits", () => {
      mocks.detail = { user: liveUser, profileHistory: [], profileHistoryMore: false };
      renderPage();

      expect(screen.getByText("No profile edits.")).toBeTruthy();
    });

    it("says the history could not be loaded when the server left it out", () => {
      mocks.detail = { user: liveUser };
      renderPage();

      expect(screen.getByText("Profile history could not be loaded.")).toBeTruthy();
    });

    it("shows no old names on a deleted account", () => {
      mocks.detail = {
        user: { userId: "user-1", role: "buyer", suspended: false },
        profileHistory: [
          { editedAt: "2026-11-13T09:00:00.000Z", actor: "user-1", changes: [{ field: "displayName", from: "Abebe", to: "Abebe K." }] },
        ],
        profileHistoryMore: false,
      };
      renderPage();

      expect(screen.getByText("Not shown for a deleted account.")).toBeTruthy();
      expect(screen.queryByText(/Abebe/)).toBeNull();
    });

    it("says the history is withheld while a deletion runs, not that it failed to load", () => {
      /* deletionState in_progress: the phone is still on the row, so only the server knows; it says so. */
      mocks.detail = { user: liveUser, profileHistoryHidden: true };
      renderPage();

      expect(screen.getByText("Not shown for a deleted account.")).toBeTruthy();
      expect(screen.queryByText("Profile history could not be loaded.")).toBeNull();
    });
  });
});
