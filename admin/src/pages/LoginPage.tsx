import { useState, type FormEvent, type ReactNode } from "react";
import { useNavigate } from "react-router";
import { useAuth } from "@/hooks/useAuth";
import { useLocale } from "@/hooks/useLocale";
import { normalizeEthiopianPhone } from "@/lib/phone";
import type { LoginResult, MfaType } from "@/lib/auth";
import { LanguageSwitcher } from "@/components/layout/LanguageSwitcher";

type Step =
  | { kind: "credentials" }
  | { kind: "totp"; mfaType: MfaType }
  | { kind: "mfaSetup"; secretCode: string; otpauthUrl: string }
  | { kind: "newPassword" };

const INPUT_CLASS =
  "mt-1 w-full rounded-lg border border-gray-200 px-4 py-2 text-sm outline-none focus:border-primary focus:ring-1 focus:ring-primary";
const PRIMARY_BUTTON_CLASS =
  "w-full rounded-lg bg-primary py-2 text-sm font-medium text-white hover:bg-primary-dark disabled:opacity-50";
const LINK_BUTTON_CLASS =
  "w-full text-center text-sm text-text-muted underline hover:text-primary";

export function LoginPage() {
  const {
    login,
    submitTotp,
    completeMfaSetup,
    completeNewPassword,
    cancelChallenge,
  } = useAuth();
  const { t } = useLocale();
  const navigate = useNavigate();
  const [step, setStep] = useState<Step>({ kind: "credentials" });
  const [phone, setPhone] = useState("");
  const [password, setPassword] = useState("");
  const [phoneError, setPhoneError] = useState("");
  const [code, setCode] = useState("");
  const [deviceName, setDeviceName] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [copied, setCopied] = useState(false);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);

  function translateError(errorKey: string): string {
    const key = `auth.${errorKey}`;
    const translated = t(key);
    // If t() returns the raw key, no i18n entry exists, so use the generic fallback
    return translated !== key ? translated : t("auth.genericError");
  }

  function resetToCredentials() {
    cancelChallenge();
    setStep({ kind: "credentials" });
    setPassword("");
    setCode("");
    setNewPassword("");
    setConfirmPassword("");
    setCopied(false);
  }

  /** Move to whatever step the latest Cognito response asks for. */
  function applyResult(result: LoginResult) {
    setCode("");
    switch (result.status) {
      case "success":
        navigate("/", { replace: true });
        return;
      case "totpRequired":
        setStep({ kind: "totp", mfaType: result.mfaType });
        return;
      case "mfaSetupRequired":
        setStep({
          kind: "mfaSetup",
          secretCode: result.secretCode,
          otpauthUrl: result.otpauthUrl,
        });
        return;
      case "newPasswordRequired":
        setStep({ kind: "newPassword" });
        return;
      case "error":
        if (result.error === "sessionExpired") resetToCredentials();
        setError(translateError(result.error));
        return;
    }
  }

  async function run(action: () => Promise<LoginResult>) {
    setLoading(true);
    try {
      applyResult(await action());
    } finally {
      setLoading(false);
    }
  }

  async function handleCredentials(e: FormEvent) {
    e.preventDefault();
    setError("");

    // Validate + normalize the phone BEFORE entering the loading state:
    // invalid input must never reach Cognito.
    if (!phone.trim()) {
      setPhoneError(t("auth.phoneRequired"));
      return;
    }
    const normalizedPhone = normalizeEthiopianPhone(phone);
    if (!normalizedPhone) {
      setPhoneError(t("auth.invalidPhoneFormat"));
      return;
    }
    setPhoneError("");
    await run(() => login(normalizedPhone, password));
  }

  async function handleCode(e: FormEvent) {
    e.preventDefault();
    setError("");
    if (!/^\d{6}$/.test(code.trim())) {
      setError(t("auth.codeRequired"));
      return;
    }
    const submitted = code;
    await run(() =>
      step.kind === "mfaSetup"
        ? completeMfaSetup(submitted, deviceName)
        : submitTotp(submitted),
    );
  }

  async function handleNewPassword(e: FormEvent) {
    e.preventDefault();
    setError("");
    if (newPassword !== confirmPassword) {
      setError(t("auth.passwordsDontMatch"));
      return;
    }
    await run(() => completeNewPassword(newPassword));
  }

  async function copySecret(secret: string) {
    try {
      await navigator.clipboard.writeText(secret);
      setCopied(true);
    } catch {
      // Clipboard blocked (insecure context or denied): the key stays visible to copy by hand
    }
  }

  const errorLine = error ? (
    <p role="alert" className="text-sm text-danger">
      {error}
    </p>
  ) : null;

  const backButton = (
    <button
      type="button"
      onClick={() => {
        setError("");
        resetToCredentials();
      }}
      className={LINK_BUTTON_CLASS}
    >
      {t("auth.backToLogin")}
    </button>
  );

  const codeInput = (
    <div>
      <label htmlFor="mfa-code" className="block text-sm font-medium text-text-muted">
        {t("auth.code")}
      </label>
      <input
        id="mfa-code"
        type="text"
        inputMode="numeric"
        autoComplete="one-time-code"
        pattern="[0-9]{6}"
        maxLength={6}
        value={code}
        onChange={(e) => setCode(e.target.value.replace(/\D/g, ""))}
        placeholder="123456"
        required
        autoFocus
        className={`${INPUT_CLASS} tracking-widest`}
      />
    </div>
  );

  let title: string;
  let subtitle: string;
  let body: ReactNode;

  switch (step.kind) {
    case "credentials":
      title = t("auth.loginTitle");
      subtitle = t("auth.loginSubtitle");
      body = (
        // noValidate: localized validation in handleCredentials replaces
        // browser-native constraint tooltips; required stays for
        // aria-required semantics.
        <form onSubmit={handleCredentials} noValidate className="mt-6 space-y-4">
          <div>
            <label className="block text-sm font-medium text-text-muted">
              {t("auth.phone")}
            </label>
            <input
              type="tel"
              value={phone}
              onChange={(e) => setPhone(e.target.value)}
              placeholder="09XXXXXXXX or +251..."
              required
              aria-invalid={!!phoneError}
              aria-describedby={phoneError ? "phone-error" : undefined}
              className={INPUT_CLASS}
            />
            {phoneError && (
              <p id="phone-error" className="mt-1 text-sm text-danger">
                {phoneError}
              </p>
            )}
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
              className={INPUT_CLASS}
            />
          </div>

          {errorLine}

          <button type="submit" disabled={loading} className={PRIMARY_BUTTON_CLASS}>
            {loading ? t("auth.signingIn") : t("auth.login")}
          </button>
        </form>
      );
      break;

    case "totp":
      title = t("auth.mfaTitle");
      subtitle =
        step.mfaType === "sms" ? t("auth.smsPrompt") : t("auth.totpPrompt");
      body = (
        <form onSubmit={handleCode} noValidate className="mt-6 space-y-4">
          {codeInput}
          {errorLine}
          <button type="submit" disabled={loading} className={PRIMARY_BUTTON_CLASS}>
            {loading ? t("auth.verifying") : t("auth.verify")}
          </button>
          {backButton}
        </form>
      );
      break;

    case "mfaSetup":
      title = t("auth.mfaSetupTitle");
      subtitle = t("auth.mfaSetupIntro");
      body = (
        <form onSubmit={handleCode} noValidate className="mt-6 space-y-4">
          <div>
            <p className="block text-sm font-medium text-text-muted">
              {t("auth.secretKey")}
            </p>
            <div className="mt-1 flex items-start gap-2">
              <code
                data-testid="mfa-secret"
                className="flex-1 break-all rounded-lg bg-gray-50 px-3 py-2 font-mono text-sm"
              >
                {step.secretCode}
              </code>
              <button
                type="button"
                onClick={() => copySecret(step.secretCode)}
                className="rounded-lg border border-gray-200 px-3 py-2 text-sm hover:border-primary"
              >
                {copied ? t("auth.copied") : t("auth.copy")}
              </button>
            </div>
            <a
              href={step.otpauthUrl}
              className="mt-2 inline-block text-sm text-primary underline"
            >
              {t("auth.openInApp")}
            </a>
          </div>
          {codeInput}
          <div>
            <label htmlFor="mfa-device" className="block text-sm font-medium text-text-muted">
              {t("auth.deviceName")}
            </label>
            <input
              id="mfa-device"
              type="text"
              value={deviceName}
              onChange={(e) => setDeviceName(e.target.value)}
              maxLength={64}
              className={INPUT_CLASS}
            />
          </div>
          {errorLine}
          <button type="submit" disabled={loading} className={PRIMARY_BUTTON_CLASS}>
            {loading ? t("auth.verifying") : t("auth.verify")}
          </button>
          {backButton}
        </form>
      );
      break;

    case "newPassword":
      title = t("auth.newPasswordTitle");
      subtitle = t("auth.newPasswordIntro");
      body = (
        <form onSubmit={handleNewPassword} noValidate className="mt-6 space-y-4">
          <div>
            <label htmlFor="new-password" className="block text-sm font-medium text-text-muted">
              {t("auth.newPassword")}
            </label>
            <input
              id="new-password"
              type="password"
              autoComplete="new-password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              required
              autoFocus
              className={INPUT_CLASS}
            />
          </div>
          <div>
            <label htmlFor="confirm-password" className="block text-sm font-medium text-text-muted">
              {t("auth.confirmPassword")}
            </label>
            <input
              id="confirm-password"
              type="password"
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
              required
              className={INPUT_CLASS}
            />
          </div>
          {errorLine}
          <button type="submit" disabled={loading} className={PRIMARY_BUTTON_CLASS}>
            {loading ? t("auth.verifying") : t("auth.savePassword")}
          </button>
          {backButton}
        </form>
      );
      break;
  }

  return (
    <div className="flex min-h-screen flex-col md:flex-row">
      {/* Left panel: branding (desktop only) */}
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

      {/* Right panel: sign-in steps */}
      <div className="flex min-h-screen md:min-h-0 w-full md:w-1/2 flex-col items-center justify-center bg-cream px-6 py-12">
        <div className="w-full max-w-sm">
          <div className="mb-6 flex justify-center">
            <LanguageSwitcher />
          </div>
          <div className="rounded-xl bg-white p-8 shadow-sm">
            <h1 className="text-center text-2xl font-bold text-primary">
              {title}
            </h1>
            <p className="mt-2 text-center text-sm text-text-muted">
              {subtitle}
            </p>
            {body}
          </div>
        </div>
      </div>
    </div>
  );
}
