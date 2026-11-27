import type { ReactNode } from "react";

interface CardProps {
  children: ReactNode;
  className?: string;
}

export function Card({ children, className = "" }: CardProps) {
  return (
    <div className={`rounded-[22px] border border-line bg-ground p-5 ${className}`}>
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
          <p className="text-sm text-slate">{label}</p>
          <p className="mt-1 text-2xl font-extrabold tracking-tight">{value}</p>
          {trend && (
            <p
              className={`mt-1 text-sm ${trend.positive ? "text-success" : "text-danger"}`}
            >
              {trend.positive ? "+" : ""}{trend.value}
            </p>
          )}
        </div>
        <div className="rounded-[14px] bg-paper p-2 text-ink">{icon}</div>
      </div>
    </Card>
  );
}
