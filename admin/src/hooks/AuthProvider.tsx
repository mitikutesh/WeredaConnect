import { useState, useEffect, useCallback, type ReactNode } from "react";
import * as auth from "@/lib/auth";
import { AuthContext } from "./useAuth";

export function AuthProvider({ children }: { children: ReactNode }) {
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [user, setUser] = useState<{ phone: string; groups: string[] } | null>(null);

  useEffect(() => {
    auth.restoreSession().then((restored) => {
      if (restored) {
        setIsAuthenticated(true);
        setUser(auth.getCurrentUser());
      }
      setIsLoading(false);
    });
  }, []);

  // Every sign-in step can end in a session; flip auth state only on success.
  const applyResult = useCallback((result: auth.LoginResult) => {
    if (result.status === "success") {
      setIsAuthenticated(true);
      setUser(auth.getCurrentUser());
    }
    return result;
  }, []);

  const loginFn = useCallback(
    async (phone: string, password: string) =>
      applyResult(await auth.login(phone, password)),
    [applyResult],
  );

  const submitTotpFn = useCallback(
    async (code: string) => applyResult(await auth.submitTotp(code)),
    [applyResult],
  );

  const completeMfaSetupFn = useCallback(
    async (code: string, deviceName: string) =>
      applyResult(await auth.completeMfaSetup(code, deviceName)),
    [applyResult],
  );

  const completeNewPasswordFn = useCallback(
    async (newPassword: string) =>
      applyResult(await auth.completeNewPassword(newPassword)),
    [applyResult],
  );

  const logoutFn = useCallback(() => {
    auth.logout();
    setIsAuthenticated(false);
    setUser(null);
  }, []);

  return (
    <AuthContext.Provider
      value={{
        isAuthenticated,
        isLoading,
        user,
        login: loginFn,
        submitTotp: submitTotpFn,
        completeMfaSetup: completeMfaSetupFn,
        completeNewPassword: completeNewPasswordFn,
        cancelChallenge: auth.cancelChallenge,
        logout: logoutFn,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}
