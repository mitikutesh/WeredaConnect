import en from './en.json';
import am from './am.json';
import om from './om.json';

export type Locale = 'en' | 'am' | 'om';

const translations: Record<Locale, typeof en> = { en, am, om };

export const defaultLocale: Locale = 'en';

export const locales: { code: Locale; label: string; name: string; dir: 'ltr' | 'rtl' }[] = [
  { code: 'en', label: 'EN', name: 'English', dir: 'ltr' },
  { code: 'am', label: 'አማ', name: 'አማርኛ', dir: 'ltr' },
  { code: 'om', label: 'OM', name: 'Afaan Oromoo', dir: 'ltr' },
];

/** Avatar initials: first character of the first two words (one fidäl character is one syllable). */
export function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((word) => Array.from(word)[0])
    .join('');
}

/**
 * Get a nested translation value by dot-separated key.
 * Example: t('en', 'hero.title')
 */
export function t(locale: Locale, key: string): string {
  const keys = key.split('.');
  let value: unknown = translations[locale];
  for (const k of keys) {
    if (value && typeof value === 'object' && k in value) {
      value = (value as Record<string, unknown>)[k];
    } else {
      // Fallback to English
      value = translations.en;
      for (const fk of keys) {
        if (value && typeof value === 'object' && fk in value) {
          value = (value as Record<string, unknown>)[fk];
        } else {
          return key;
        }
      }
      return typeof value === 'string' ? value : key;
    }
  }
  return typeof value === 'string' ? value : key;
}

/** Get the locale from a URL pathname. */
export function getLocaleFromPath(pathname: string): Locale {
  const seg = pathname.split('/').filter(Boolean)[0];
  if (seg === 'am' || seg === 'om') return seg;
  return 'en';
}

/** Build a localized path. English uses root, others use /am/ or /om/. */
export function localePath(locale: Locale, path: string): string {
  const clean = path.replace(/^\/(en|am|om)/, '').replace(/^\/+/, '/') || '/';
  if (locale === 'en') return clean;
  return `/${locale}${clean === '/' ? '/' : clean}`;
}
