import type { ReactNode } from "react";

type Variant =
  | "default"
  | "success"
  | "warning"
  | "danger"
  | "info"
  | "transit";

/* Token-driven: the same state reads the same colour in the app and the
 * admin console (page M: what waits is orange tint, what is wrong is the
 * error tint, the rest is a neutral chip). */
const variants: Record<Variant, string> = {
  default: "bg-chip text-slate",
  success: "bg-success-tint text-success-ink",
  warning: "bg-orange-tint text-orange-ink",
  danger: "bg-danger-tint text-danger-ink",
  info: "bg-chip text-ink-soft",
  transit: "bg-orange-tint text-orange-ink",
};

interface BadgeProps {
  variant?: Variant;
  children: ReactNode;
}

export function Badge({ variant = "default", children }: BadgeProps) {
  return (
    <span
      className={`inline-flex min-h-[22px] items-center rounded-md px-2 text-[11px] font-bold ${variants[variant]}`}
    >
      {children}
    </span>
  );
}
