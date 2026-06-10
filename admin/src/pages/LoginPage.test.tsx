import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { LoginPage } from "./LoginPage";

const loginMock = vi.hoisted(() =>
  vi.fn<
    (phone: string, password: string) => Promise<{ success: boolean; error?: string }>
  >(),
);

vi.mock("@/hooks/useAuth", () => ({
  useAuth: () => ({
    isAuthenticated: false,
    isLoading: false,
    user: null,
    login: loginMock,
    logout: vi.fn(),
  }),
}));

// Real English translations so the tests also prove the i18n keys exist.
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

vi.mock("@/components/layout/LanguageSwitcher", () => ({
  LanguageSwitcher: () => null,
}));

describe("LoginPage phone validation", () => {
  beforeEach(() => {
    loginMock.mockReset();
    loginMock.mockResolvedValue({ success: true });
  });

  it("shows localized required message and skips Cognito on empty phone", async () => {
    const user = userEvent.setup();
    render(<LoginPage />);

    await user.click(screen.getByRole("button", { name: "Login" }));

    expect(screen.getByText("Phone number is required")).toBeTruthy();
    expect(loginMock).not.toHaveBeenCalled();
    // Button must not be stuck in the loading state
    expect(screen.getByRole("button", { name: "Login" })).toBeTruthy();
  });

  it("shows localized format error and skips Cognito on invalid phone", async () => {
    const user = userEvent.setup();
    render(<LoginPage />);

    await user.type(screen.getByPlaceholderText("09XXXXXXXX or +251..."), "12345");
    await user.click(screen.getByRole("button", { name: "Login" }));

    expect(
      screen.getByText(
        "Invalid phone number. Use +251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX",
      ),
    ).toBeTruthy();
    expect(loginMock).not.toHaveBeenCalled();
  });

  it("normalizes a local-format phone to E.164 before calling login", async () => {
    const user = userEvent.setup();
    render(<LoginPage />);

    await user.type(
      screen.getByPlaceholderText("09XXXXXXXX or +251..."),
      "0912345678",
    );
    const inputs = document.querySelectorAll("input");
    await user.type(inputs[1], "TestPass1dev");
    await user.click(screen.getByRole("button", { name: "Login" }));

    expect(loginMock).toHaveBeenCalledWith("+251912345678", "TestPass1dev");
  });
});
