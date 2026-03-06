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
    <div className="flex min-h-screen flex-col md:flex-row">
      {/* Left panel — branding (desktop only) */}
      <div className="hidden md:flex md:w-1/2 flex-col items-center justify-center bg-[#1a6b5c] px-12">
        <svg className="w-16 h-16 text-white" viewBox="0 0 24 24" fill="currentColor" xmlns="http://www.w3.org/2000/svg">
          <path d="M19.5,12c0.93,0,1.78,0.28,2.5,0.76V8c0-1.1-0.9-2-2-2h-6.29l-1.06-1.06l1.41-1.41l-0.71-0.71L9.82,6.35l0.71,0.71l1.41-1.41L13,6.71V9c0,1.1-0.9,2-2,2h-0.54c0.95,1.06,1.54,2.46,1.54,4c0,0.34-0.04,0.67-0.09,1h3.14C15.3,13.75,17.19,12,19.5,12z"/>
          <path d="M19.5,13c-1.93,0-3.5,1.57-3.5,3.5s1.57,3.5,3.5,3.5s3.5-1.57,3.5-3.5S21.43,13,19.5,13z M19.5,18c-0.83,0-1.5-0.67-1.5-1.5s0.67-1.5,1.5-1.5s1.5,0.67,1.5,1.5S20.33,18,19.5,18z"/>
          <path d="M4,9h5c0-1.1-0.9-2-2-2H4C3.45,7,3,7.45,3,8C3,8.55,3.45,9,4,9z"/>
          <path d="M9.83,13.82l-0.18-0.47L10.58,13c-0.46-1.06-1.28-1.91-2.31-2.43l-0.4,0.89l-0.46-0.21l0.4-0.9C7.26,10.13,6.64,10,6,10c-0.53,0-1.04,0.11-1.52,0.26l0.34,0.91l-0.47,0.18L4,10.42c-1.06,0.46-1.91,1.28-2.43,2.31l0.89,0.4l-0.21,0.46l-0.9-0.4C1.13,13.74,1,14.36,1,15c0,0.53,0.11,1.04,0.26,1.52l0.91-0.34l0.18,0.47L1.42,17c0.46,1.06,1.28,1.91,2.31,2.43l0.4-0.89l0.46,0.21l-0.4,0.9C4.74,19.87,5.36,20,6,20c0.53,0,1.04-0.11,1.52-0.26l-0.34-0.91l0.47-0.18L8,19.58c1.06-0.46,1.91-1.28,2.43-2.31l-0.89-0.4l0.21-0.46l0.9,0.4C10.87,16.26,11,15.64,11,15c0-0.53-0.11-1.04-0.26-1.52L9.83,13.82z M7.15,17.77c-1.53,0.63-3.29-0.09-3.92-1.62c-0.63-1.53,0.09-3.29,1.62-3.92c1.53-0.63,3.29,0.09,3.92,1.62C9.41,15.38,8.68,17.14,7.15,17.77z"/>
        </svg>
        <h1 className="mt-6 text-4xl font-bold text-white">WeredaConnect</h1>
        <p className="mt-3 max-w-xs text-center text-lg text-white/80">
          {t("app.tagline")}
        </p>
      </div>

      {/* Right panel — login form */}
      <div className="flex min-h-screen md:min-h-0 w-full md:w-1/2 flex-col items-center justify-center bg-cream px-6 py-12">
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
    </div>
  );
}
