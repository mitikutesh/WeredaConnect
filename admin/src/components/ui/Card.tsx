import type { ReactNode } from "react";

interface CardProps {
  children: ReactNode;
  className?: string;
}

export function Card({ children, className = "" }: CardProps) {
  return (
    <div className={`rounded-xl bg-white p-6 shadow-sm ${className}`}>
      {children}
    </div>
  );
}

interface KpiCardProps {
  label: string;
  value: string | number;
  icon: ReactNode;
  trend?: { value: string; positive: boolean };
}

export function KpiCard({ label, value, icon, trend }: KpiCardProps) {
  return (
    <Card>
      <div className="flex items-start justify-between">
        <div>
          <p className="text-sm text-text-muted">{label}</p>
          <p className="mt-1 text-2xl font-bold">{value}</p>
          {trend && (
            <p
              className={`mt-1 text-sm ${trend.positive ? "text-success" : "text-danger"}`}
            >
              {trend.positive ? "+" : ""}{trend.value}
            </p>
          )}
        </div>
        <div className="rounded-lg bg-primary/10 p-2 text-primary">{icon}</div>
      </div>
    </Card>
  );
}
