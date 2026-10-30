import type { IconName } from './Icon.astro';

/** The three marketplace roles, in the order every page lists them. */
export const roles: { key: 'farmer' | 'trucker' | 'buyer'; anchor: string; icon: IconName }[] = [
  { key: 'farmer', anchor: 'farmers', icon: 'sprout' },
  { key: 'trucker', anchor: 'truckers', icon: 'truck' },
  { key: 'buyer', anchor: 'buyers', icon: 'bag' },
];

export const stepNumbers = [1, 2, 3] as const;
