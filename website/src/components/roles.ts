import type { IconName } from './Icon.astro';

export type RoleKey = 'farmer' | 'trucker' | 'buyer';

/** The three marketplace roles, in the order every page lists them. */
export const roles: { key: RoleKey; anchor: string; icon: IconName }[] = [
  { key: 'farmer', anchor: 'farmers', icon: 'sprout' },
  { key: 'trucker', anchor: 'truckers', icon: 'truck' },
  { key: 'buyer', anchor: 'buyers', icon: 'bag' },
];

/**
 * Each role's colour (app theme RoleColors: plum, blue, green), used only
 * where that role is the subject. Full class names so Tailwind finds them.
 */
export const roleTones: Record<RoleKey, { solid: string; tint: string; text: string }> = {
  farmer: { solid: 'bg-farmer text-white', tint: 'bg-farmer-tint text-farmer', text: 'text-farmer' },
  trucker: { solid: 'bg-trucker text-white', tint: 'bg-trucker-tint text-trucker', text: 'text-trucker' },
  buyer: { solid: 'bg-buyer text-white', tint: 'bg-buyer-tint text-buyer', text: 'text-buyer' },
};

export const stepNumbers = [1, 2, 3] as const;
