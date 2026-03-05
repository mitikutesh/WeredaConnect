import { useState, type FormEvent } from "react";
import { useAuth } from "@/hooks/useAuth";
import { useLocale } from "@/hooks/useLocale";
import { LanguageSwitcher } from "@/components/layout/LanguageSwitcher";

export function LoginPage() {
  const { login } = useAuth();
  const { t } = useLocale();
  const [phone, setPhone] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError("");
    setLoading(true);

    const result = await login(phone, password);
    if (!result.success) {
      const errorKey = result.error === "notAdmin"
        ? "auth.notAdmin"
        : result.error === "invalidCredentials"
          ? "auth.invalidCredentials"
          : "";
      setError(errorKey ? t(errorKey) : result.error || t("common.error"));
    }
    setLoading(false);
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-cream">
      <div className="w-full max-w-sm">
        <div className="mb-6 flex justify-center">
          <LanguageSwitcher />
        </div>
        <div className="rounded-xl bg-white p-8 shadow-sm">
          <h1 className="text-center text-2xl font-bold text-primary">
            {t("auth.loginTitle")}
          </h1>
          <p className="mt-2 text-center text-sm text-text-muted">
            {t("auth.loginSubtitle")}
          </p>

          <form onSubmit={handleSubmit} className="mt-6 space-y-4">
            <div>
              <label className="block text-sm font-medium text-text-muted">
                {t("auth.phone")}
              </label>
              <input
                type="tel"
                value={phone}
                onChange={(e) => setPhone(e.target.value)}
                placeholder="+251..."
                required
                className="mt-1 w-full rounded-lg border border-gray-200 px-4 py-2 text-sm outline-none focus:border-primary focus:ring-1 focus:ring-primary"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-text-muted">
                {t("auth.password")}
              </label>
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
                className="mt-1 w-full rounded-lg border border-gray-200 px-4 py-2 text-sm outline-none focus:border-primary focus:ring-1 focus:ring-primary"
              />
            </div>

            {error && (
              <p className="text-sm text-danger">{error}</p>
            )}

            <button
              type="submit"
              disabled={loading}
              className="w-full rounded-lg bg-primary py-2 text-sm font-medium text-white hover:bg-primary-dark disabled:opacity-50"
            >
              {loading ? t("auth.signingIn") : t("auth.login")}
            </button>
          </form>
        </div>
      </div>
    </div>
  );
}
