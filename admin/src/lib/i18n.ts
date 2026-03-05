import en from "@/i18n/en.json";
import am from "@/i18n/am.json";
import om from "@/i18n/om.json";

export type Locale = "en" | "am" | "om";

const messages: Record<Locale, Record<string, unknown>> = { en, am, om };

export function t(key: string, locale: Locale = "en"): string {
  const parts = key.split(".");
  let current: unknown = messages[locale];
  for (const part of parts) {
    if (current && typeof current === "object" && part in current) {
      current = (current as Record<string, unknown>)[part];
    } else {
      return key;
    }
  }
  return typeof current === "string" ? current : key;
}

export function formatETB(amount: number, locale: Locale = "en"): string {
  const formatted = amount.toLocaleString("en-US", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  const label = t("common.birr", locale);
  return `${formatted} ${label}`;
}

export function formatDate(date: string | Date): string {
  const d = typeof date === "string" ? new Date(date) : date;
  return d.toLocaleDateString("en-GB", {
    day: "2-digit",
    month: "short",
    year: "numeric",
  });
}

export function formatDateTime(date: string | Date): string {
  const d = typeof date === "string" ? new Date(date) : date;
  return d.toLocaleString("en-GB", {
    day: "2-digit",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}
