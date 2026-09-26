import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { LoginResult } from "@/lib/auth";
import { LoginPage } from "./LoginPage";

const mocks = vi.hoisted(() => ({
  login: vi.fn<(phone: string, password: string) => Promise<LoginResult>>(),
  submitTotp: vi.fn<(code: string) => Promise<LoginResult>>(),
  completeMfaSetup:
    vi.fn<(code: string, deviceName: string) => Promise<LoginResult>>(),
  completeNewPassword: vi.fn<(pw: string) => Promise<LoginResult>>(),
  cancelChallenge: vi.fn(),
}));
const loginMock = mocks.login;

vi.mock("@/hooks/useAuth", () => ({
  useAuth: () => ({
    isAuthenticated: false,
    isLoading: false,
    user: null,
    ...mocks,
    logout: vi.fn(),
  }),
}));

function renderLogin() {
  return render(
    <MemoryRouter initialEntries={["/login"]}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<p>Dashboard home</p>} />
      </Routes>
    </MemoryRouter>,
  );
}

async function signIn(user: ReturnType<typeof userEvent.setup>) {
  await user.type(
    screen.getByPlaceholderText("09XXXXXXXX or +251..."),
    "0912345678",
  );
  const inputs = document.querySelectorAll("input");
  await user.type(inputs[1], "TestPass1dev");
  await user.click(screen.getByRole("button", { name: "Login" }));
}

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
    loginMock.mockResolvedValue({ status: "success" });
  });

  it("shows localized required message and skips Cognito on empty phone", async () => {
    const user = userEvent.setup();
    renderLogin();

    await user.click(screen.getByRole("button", { name: "Login" }));

    expect(screen.getByText("Phone number is required")).toBeTruthy();
    expect(loginMock).not.toHaveBeenCalled();
    // Button must not be stuck in the loading state
    expect(screen.getByRole("button", { name: "Login" })).toBeTruthy();
  });

  it("shows localized format error and skips Cognito on invalid phone", async () => {
    const user = userEvent.setup();
    renderLogin();

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
    renderLogin();

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

describe("LoginPage MFA steps", () => {
  beforeEach(() => {
    Object.values(mocks).forEach((m) => m.mockReset());
  });

  it("renders the code step when Cognito requires TOTP", async () => {
    loginMock.mockResolvedValue({ status: "totpRequired", mfaType: "totp" });
    const user = userEvent.setup();
    renderLogin();

    await signIn(user);

    expect(screen.getByText("Two-step verification")).toBeTruthy();
    expect(
      screen.getByText("Enter the 6-digit code from your authenticator app."),
    ).toBeTruthy();
    expect(screen.getByLabelText("Verification code")).toBeTruthy();
  });

  it("lands on the dashboard route after a valid code", async () => {
    loginMock.mockResolvedValue({ status: "totpRequired", mfaType: "totp" });
    mocks.submitTotp.mockResolvedValue({ status: "success" });
    const user = userEvent.setup();
    renderLogin();

    await signIn(user);
    await user.type(screen.getByLabelText("Verification code"), "123456");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(mocks.submitTotp).toHaveBeenCalledWith("123456");
    expect(await screen.findByText("Dashboard home")).toBeTruthy();
  });

  it("shows an error on a wrong code and stays on the code step", async () => {
    loginMock.mockResolvedValue({ status: "totpRequired", mfaType: "totp" });
    mocks.submitTotp.mockResolvedValue({ status: "error", error: "invalidCode" });
    const user = userEvent.setup();
    renderLogin();

    await signIn(user);
    await user.type(screen.getByLabelText("Verification code"), "000000");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(
      await screen.findByText(
        "Incorrect code. Check your authenticator app and try again.",
      ),
    ).toBeTruthy();
    expect(screen.getByLabelText("Verification code")).toBeTruthy();
    expect(screen.queryByText("Dashboard home")).toBeNull();
  });

  it("rejects a short code locally without calling Cognito", async () => {
    loginMock.mockResolvedValue({ status: "totpRequired", mfaType: "totp" });
    const user = userEvent.setup();
    renderLogin();

    await signIn(user);
    await user.type(screen.getByLabelText("Verification code"), "12a3");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(screen.getByText("Enter the 6-digit code")).toBeTruthy();
    expect(mocks.submitTotp).not.toHaveBeenCalled();
  });

  it("returns to the password step when the session expired", async () => {
    loginMock.mockResolvedValue({ status: "totpRequired", mfaType: "totp" });
    mocks.submitTotp.mockResolvedValue({ status: "error", error: "sessionExpired" });
    const user = userEvent.setup();
    renderLogin();

    await signIn(user);
    await user.type(screen.getByLabelText("Verification code"), "123456");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(
      await screen.findByText("Your sign-in session expired. Please sign in again."),
    ).toBeTruthy();
    expect(screen.getByRole("button", { name: "Login" })).toBeTruthy();
    expect(mocks.cancelChallenge).toHaveBeenCalled();
  });

  it("renders the secret and otpauth link on MFA setup, then completes", async () => {
    const otpauthUrl =
      "otpauth://totp/WeredaConnect%20Admin:%2B251912345678?secret=JBSWY3DPEHPK3PXP&issuer=WeredaConnect%20Admin";
    loginMock.mockResolvedValue({
      status: "mfaSetupRequired",
      secretCode: "JBSWY3DPEHPK3PXP",
      otpauthUrl,
    });
    mocks.completeMfaSetup.mockResolvedValue({ status: "success" });
    const user = userEvent.setup();
    renderLogin();

    await signIn(user);

    expect(screen.getByText("Set up two-step verification")).toBeTruthy();
    expect(screen.getByTestId("mfa-secret").textContent).toBe("JBSWY3DPEHPK3PXP");
    expect(
      screen.getByRole("link", { name: "Open in authenticator app" }).getAttribute("href"),
    ).toBe(otpauthUrl);

    await user.type(screen.getByLabelText("Verification code"), "654321");
    await user.type(screen.getByLabelText("Device name (optional)"), "Pixel");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(mocks.completeMfaSetup).toHaveBeenCalledWith("654321", "Pixel");
    expect(await screen.findByText("Dashboard home")).toBeTruthy();
  });

  it("renders the new-password step and blocks mismatched passwords", async () => {
    loginMock.mockResolvedValue({ status: "newPasswordRequired" });
    mocks.completeNewPassword.mockResolvedValue({
      status: "mfaSetupRequired",
      secretCode: "SECRET",
      otpauthUrl: "otpauth://totp/x?secret=SECRET",
    });
    const user = userEvent.setup();
    renderLogin();

    await signIn(user);
    expect(screen.getByText("Set a new password")).toBeTruthy();

    await user.type(screen.getByLabelText("New password"), "N3w-Password!");
    await user.type(screen.getByLabelText("Confirm new password"), "different");
    await user.click(screen.getByRole("button", { name: "Save password" }));
    expect(screen.getByText("Passwords do not match")).toBeTruthy();
    expect(mocks.completeNewPassword).not.toHaveBeenCalled();

    await user.clear(screen.getByLabelText("Confirm new password"));
    await user.type(screen.getByLabelText("Confirm new password"), "N3w-Password!");
    await user.click(screen.getByRole("button", { name: "Save password" }));

    expect(mocks.completeNewPassword).toHaveBeenCalledWith("N3w-Password!");
    expect(await screen.findByText("Set up two-step verification")).toBeTruthy();
  });
});
