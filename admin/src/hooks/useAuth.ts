import { createContext, useContext } from "react";
import type * as auth from "@/lib/auth";

export interface AuthContextValue {
  isAuthenticated: boolean;
  isLoading: boolean;
  user: { phone: string; groups: string[] } | null;
  login: (phone: string, password: string) => Promise<auth.LoginResult>;
  submitTotp: (code: string) => Promise<auth.LoginResult>;
  completeMfaSetup: (code: string, deviceName: string) => Promise<auth.LoginResult>;
  completeNewPassword: (newPassword: string) => Promise<auth.LoginResult>;
  cancelChallenge: () => void;
  logout: () => void;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
