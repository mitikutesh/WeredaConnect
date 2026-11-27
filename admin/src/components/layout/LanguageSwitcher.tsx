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
    <div className="flex items-center gap-1 rounded-[14px] bg-track p-1">
      {locales.map(({ code, label }) => (
        <button
          key={code}
          onClick={() => setLocale(code)}
          aria-pressed={locale === code}
          className={`min-h-9 rounded-[10px] px-2.5 text-sm transition-colors ${
            locale === code
              ? "bg-ground font-bold text-ink"
              : "font-semibold text-ink-soft hover:text-ink"
          }`}
        >
          {label}
        </button>
      ))}
    </div>
  );
}
