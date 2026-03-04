import { NavLink } from "react-router";
import {
  LayoutDashboard,
  Wallet,
  Scale,
  ShieldAlert,
  Users,
  TrendingUp,
  ArrowLeftRight,
  FileText,
  HeartPulse,
  Shield,
} from "lucide-react";
import { useLocale } from "@/hooks/useLocale";

const navItems = [
  { path: "/", icon: LayoutDashboard, labelKey: "nav.dashboard" },
  { path: "/escrows", icon: Wallet, labelKey: "nav.escrows" },
  { path: "/disputes", icon: Scale, labelKey: "nav.disputes" },
  { path: "/fraud-review", icon: ShieldAlert, labelKey: "nav.fraudReview" },
  { path: "/users", icon: Users, labelKey: "nav.users" },
  { path: "/revenue", icon: TrendingUp, labelKey: "nav.revenue" },
  { path: "/transactions", icon: ArrowLeftRight, labelKey: "nav.transactions" },
  { path: "/audit-log", icon: FileText, labelKey: "nav.auditLog" },
  { path: "/system-health", icon: HeartPulse, labelKey: "nav.systemHealth" },
  { path: "/compliance", icon: Shield, labelKey: "nav.compliance" },
];

export function Sidebar() {
  const { t } = useLocale();

  return (
    <aside className="flex h-screen w-64 flex-col border-r border-gray-200 bg-white">
      <div className="flex h-16 items-center px-6 border-b border-gray-200">
        <span className="text-xl font-bold text-primary">WC</span>
        <span className="ml-2 text-sm text-text-muted">{t("app.subtitle")}</span>
      </div>
      <nav className="flex-1 overflow-y-auto p-4">
        <ul className="space-y-1">
          {navItems.map(({ path, icon: Icon, labelKey }) => (
            <li key={path}>
              <NavLink
                to={path}
                end={path === "/"}
                className={({ isActive }) =>
                  `flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors ${
                    isActive
                      ? "bg-primary/10 text-primary"
                      : "text-text-muted hover:bg-gray-50 hover:text-text"
                  }`
                }
              >
                <Icon size={18} />
                {t(labelKey)}
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>
    </aside>
  );
}
