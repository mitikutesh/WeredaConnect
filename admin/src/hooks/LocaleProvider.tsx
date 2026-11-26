import { useState, useCallback, type ReactNode } from "react";
import { t as translate, type Locale } from "@/lib/i18n";
import { LocaleContext } from "./useLocale";

export function LocaleProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<Locale>(() => {
    const saved = localStorage.getItem("wc-admin-locale");
    return (saved as Locale) || "en";
  });

  const setLocale = useCallback((newLocale: Locale) => {
    setLocaleState(newLocale);
    localStorage.setItem("wc-admin-locale", newLocale);
  }, []);

  const t = useCallback(
    (key: string) => translate(key, locale),
    [locale],
  );

  return (
    <LocaleContext.Provider value={{ locale, setLocale, t }}>
      {children}
    </LocaleContext.Provider>
  );
}
