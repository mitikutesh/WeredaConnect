import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { AuthProvider } from "./AuthProvider";
import { LocaleProvider } from "./LocaleProvider";
import { SidebarProvider } from "./SidebarProvider";
import { useAuth } from "./useAuth";
import { useLocale } from "./useLocale";
import { useSidebar } from "./useSidebar";

/* The providers live in their own files and the hooks beside their context objects (fast refresh
 * needs a component file to export only components). Each hook still reads its own provider. */

const auth = vi.hoisted(() => ({
  restoreSession: vi.fn(),
  getCurrentUser: vi.fn(),
  login: vi.fn(),
  submitTotp: vi.fn(),
  completeMfaSetup: vi.fn(),
  completeNewPassword: vi.fn(),
  cancelChallenge: vi.fn(),
  logout: vi.fn(),
}));

vi.mock("@/lib/auth", () => auth);

const silently = (run: () => void) => {
  const spy = vi.spyOn(console, "error").mockImplementation(() => {});
  try {
    run();
  } finally {
    spy.mockRestore();
  }
};

describe("context hooks and their providers", () => {
  beforeEach(() => {
    localStorage.clear();
    auth.restoreSession.mockReset().mockResolvedValue(false);
    auth.getCurrentUser.mockReset().mockReturnValue({ phone: "+251900000001", groups: ["admin"] });
    auth.login.mockReset().mockResolvedValue({ status: "success" });
    auth.logout.mockReset();
  });

  it("refuses each hook outside its provider", () => {
    silently(() => {
      expect(() => renderHook(() => useAuth())).toThrow("useAuth must be used within AuthProvider");
      expect(() => renderHook(() => useLocale())).toThrow("useLocale must be used within LocaleProvider");
      expect(() => renderHook(() => useSidebar())).toThrow("useSidebar must be used within SidebarProvider");
    });
  });

  it("opens, toggles and closes the sidebar", () => {
    const { result } = renderHook(() => useSidebar(), {
      wrapper: ({ children }: { children: ReactNode }) => <SidebarProvider>{children}</SidebarProvider>,
    });
    expect(result.current.isOpen).toBe(false);
    act(() => result.current.open());
    expect(result.current.isOpen).toBe(true);
    act(() => result.current.toggle());
    expect(result.current.isOpen).toBe(false);
    act(() => result.current.toggle());
    act(() => result.current.close());
    expect(result.current.isOpen).toBe(false);
  });

  it("translates in the chosen language and remembers it", () => {
    const { result } = renderHook(() => useLocale(), {
      wrapper: ({ children }: { children: ReactNode }) => <LocaleProvider>{children}</LocaleProvider>,
    });
    expect(result.current.locale).toBe("en");
    expect(result.current.t("nav.disputes")).toBe("Disputes");
    act(() => result.current.setLocale("om"));
    expect(result.current.locale).toBe("om");
    expect(localStorage.getItem("wc-admin-locale")).toBe("om");
  });

  it("signs in on a successful login and out again", async () => {
    const { result } = renderHook(() => useAuth(), {
      wrapper: ({ children }: { children: ReactNode }) => <AuthProvider>{children}</AuthProvider>,
    });
    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.isAuthenticated).toBe(false);

    await act(async () => {
      await result.current.login("+251900000001", "secret");
    });
    expect(auth.login).toHaveBeenCalledWith("+251900000001", "secret");
    expect(result.current.isAuthenticated).toBe(true);
    expect(result.current.user).toEqual({ phone: "+251900000001", groups: ["admin"] });

    act(() => result.current.logout());
    expect(auth.logout).toHaveBeenCalled();
    expect(result.current.isAuthenticated).toBe(false);
    expect(result.current.user).toBeNull();
  });
});
