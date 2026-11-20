import type { ReactNode } from "react";

type Variant =
  | "default"
  | "success"
  | "warning"
  | "danger"
  | "info"
  | "transit";

/* Token-driven — mirrors the mobile StatusChip vocabulary so the same
 * state reads the same color in the app and the admin console. */
const variants: Record<Variant, string> = {
  default: "bg-text-muted/10 text-text-muted",
  success: "bg-success/10 text-success",
  warning: "bg-warning/10 text-warning",
  danger: "bg-danger/10 text-danger",
  info: "bg-info/10 text-info",
  transit: "bg-transit/10 text-transit",
};

interface BadgeProps {
  variant?: Variant;
  children: ReactNode;
}

export function Badge({ variant = "default", children }: BadgeProps) {
  return (
    <span
      className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${variants[variant]}`}
    >
      {children}
    </span>
  );
}
