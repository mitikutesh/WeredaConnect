import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { UserDetailPage } from "./UserDetailPage";

/* FRD-05: the Fraud Review page has its own wording for ADMIN_006 and ADMIN_007; the user page keeps
 * the shared errors.* text for its own answers. The hooks are mocked; the English strings are real. */

type MutateOptions = { onError?: (err: unknown) => void };

const mocks = vi.hoisted(() => ({
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useUserDetail: () => ({
    data: { user: { userId: "user-1", displayName: "Abebe", role: "farmer", suspended: false } },
    isLoading: false,
  }),
  useUserAction: () => ({ mutate: mocks.mutate, isPending: false }),
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

describe("UserDetailPage", () => {
  beforeEach(() => mocks.mutate.mockReset());

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
});
