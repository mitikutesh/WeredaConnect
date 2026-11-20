import { useLocale } from "@/hooks/useLocale";
import type { Locale } from "@/lib/i18n";

const locales: { code: Locale; label: string }[] = [
  { code: "en", label: "EN" },
  { code: "am", label: "\u12A0\u121B" },
  { code: "om", label: "OM" },
];

export function LanguageSwitcher() {
  const { locale, setLocale } = useLocale();

  return (
    <div className="flex items-center gap-1 rounded-lg bg-white p-1 shadow-sm">
      {locales.map(({ code, label }) => (
        <button
          key={code}
          onClick={() => setLocale(code)}
          className={`rounded-md px-2 py-1 text-sm font-medium transition-colors ${
            locale === code
              ? "bg-primary text-white"
              : "text-text-muted hover:bg-gray-100"
          }`}
        >
          {label}
        </button>
      ))}
    </div>
  );
}
