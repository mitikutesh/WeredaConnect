import {
  createContext,
  useContext,
  useState,
  useEffect,
  useCallback,
  type ReactNode,
} from "react";
import * as auth from "@/lib/auth";

interface AuthContextValue {
  isAuthenticated: boolean;
  isLoading: boolean;
  user: { phone: string; groups: string[] } | null;
  login: (phone: string, password: string) => Promise<{ success: boolean; error?: string }>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

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

  const loginFn = useCallback(async (phone: string, password: string) => {
    const result = await auth.login(phone, password);
    if (result.success) {
      setIsAuthenticated(true);
      setUser(auth.getCurrentUser());
    }
    return result;
  }, []);

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
        logout: logoutFn,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
