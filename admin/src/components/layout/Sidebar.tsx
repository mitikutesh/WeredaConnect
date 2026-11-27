import { NavLink, useLocation } from "react-router";
import {
  LayoutDashboard,
  Wallet,
  Scale,
  HandCoins,
  Hourglass,
  Undo2,
  ShieldAlert,
  Flag,
  Gavel,
  Users,
  TrendingUp,
  ArrowLeftRight,
  BarChart3,
  FileText,
  HeartPulse,
  Shield,
  X,
} from "lucide-react";
import { useLocale } from "@/hooks/useLocale";
import { useOpenDisputeCount } from "@/hooks/useQuery";
import { useSidebar } from "@/hooks/useSidebar";
import { fill } from "@/lib/trail";

const navItems = [
  { path: "/", icon: LayoutDashboard, labelKey: "nav.dashboard" },
  { path: "/escrows", icon: Wallet, labelKey: "nav.escrows" },
  { path: "/disputes", icon: Scale, labelKey: "nav.disputes" },
  { path: "/payouts", icon: HandCoins, labelKey: "nav.manualPayouts" },
  { path: "/stuck-orders", icon: Hourglass, labelKey: "nav.stuckOrders" },
  { path: "/unapplied-payments", icon: Undo2, labelKey: "nav.unappliedPayments" },
  { path: "/fraud-review", icon: ShieldAlert, labelKey: "nav.fraudReview" },
  { path: "/listing-reports", icon: Flag, labelKey: "nav.listingReports" },
  { path: "/reputation-appeals", icon: Gavel, labelKey: "nav.reputationAppeals" },
  { path: "/users", icon: Users, labelKey: "nav.users" },
  { path: "/transactions", icon: ArrowLeftRight, labelKey: "nav.transactions" },
  { path: "/revenue", icon: TrendingUp, labelKey: "nav.revenue" },
  { path: "/analytics", icon: BarChart3, labelKey: "nav.analytics" },
  { path: "/audit-log", icon: FileText, labelKey: "nav.auditLog" },
  { path: "/system-health", icon: HeartPulse, labelKey: "nav.systemHealth" },
  { path: "/compliance", icon: Shield, labelKey: "nav.compliance" },
];

export function Sidebar() {
  const { t } = useLocale();
  const { isOpen, close } = useSidebar();
  const { pathname } = useLocation();
  // Page M board: "Disputes · 3". Nothing while it loads, on an error, or with no open dispute.
  const openDisputes = useOpenDisputeCount(pathname === "/disputes" || pathname.startsWith("/disputes/"));

  const sidebarContent = (
    <>
      <div className="flex h-16 items-center justify-between px-5">
        <div className="flex min-w-0 items-center gap-2.5">
          <svg aria-hidden="true" className="h-[34px] w-[34px] shrink-0 rounded-[10px] p-[7px] text-white" viewBox="0 0 24 24" fill="currentColor" xmlns="http://www.w3.org/2000/svg">
            <path d="M19.5,12c0.93,0,1.78,0.28,2.5,0.76V8c0-1.1-0.9-2-2-2h-6.29l-1.06-1.06l1.41-1.41l-0.71-0.71L9.82,6.35l0.71,0.71l1.41-1.41L13,6.71V9c0,1.1-0.9,2-2,2h-0.54c0.95,1.06,1.54,2.46,1.54,4c0,0.34-0.04,0.67-0.09,1h3.14C15.3,13.75,17.19,12,19.5,12z"/>
            <path d="M19.5,13c-1.93,0-3.5,1.57-3.5,3.5s1.57,3.5,3.5,3.5s3.5-1.57,3.5-3.5S21.43,13,19.5,13z M19.5,18c-0.83,0-1.5-0.67-1.5-1.5s0.67-1.5,1.5-1.5s1.5,0.67,1.5,1.5S20.33,18,19.5,18z"/>
            <path d="M4,9h5c0-1.1-0.9-2-2-2H4C3.45,7,3,7.45,3,8C3,8.55,3.45,9,4,9z"/>
            <path d="M9.83,13.82l-0.18-0.47L10.58,13c-0.46-1.06-1.28-1.91-2.31-2.43l-0.4,0.89l-0.46-0.21l0.4-0.9C7.26,10.13,6.64,10,6,10c-0.53,0-1.04,0.11-1.52,0.26l0.34,0.91l-0.47,0.18L4,10.42c-1.06,0.46-1.91,1.28-2.43,2.31l0.89,0.4l-0.21,0.46l-0.9-0.4C1.13,13.74,1,14.36,1,15c0,0.53,0.11,1.04,0.26,1.52l0.91-0.34l0.18,0.47L1.42,17c0.46,1.06,1.28,1.91,2.31,2.43l0.4-0.89l0.46,0.21l-0.4,0.9C4.74,19.87,5.36,20,6,20c0.53,0,1.04-0.11,1.52-0.26l-0.34-0.91l0.47-0.18L8,19.58c1.06-0.46,1.91-1.28,2.43-2.31l-0.89-0.4l0.21-0.46l0.9,0.4C10.87,16.26,11,15.64,11,15c0-0.53-0.11-1.04-0.26-1.52L9.83,13.82z M7.15,17.77c-1.53,0.63-3.29-0.09-3.92-1.62c-0.63-1.53,0.09-3.29,1.62-3.92c1.53-0.63,3.29,0.09,3.92,1.62C9.41,15.38,8.68,17.14,7.15,17.77z"/>
          </svg>
          <span className="truncate text-[15px] font-extrabold text-white">{t("app.title")}</span>
        </div>
        <button
          onClick={close}
          aria-label={t("nav.closeMenu")}
          className="md:hidden rounded-[10px] p-2 text-nav-text hover:bg-nav-selected hover:text-white"
        >
          <X size={20} />
        </button>
      </div>
      <nav aria-label={t("nav.label")} className="flex-1 overflow-y-auto px-3 pb-4">
        <ul className="space-y-1">
          {navItems.map(({ path, icon: Icon, labelKey }) => {
            const count = path === "/disputes" && openDisputes ? openDisputes : null;
            return (
              <li key={path}>
                <NavLink
                  to={path}
                  end={path === "/"}
                  onClick={close}
                  aria-label={count ? fill(t("nav.disputesOpen"), { count }) : undefined}
                  className={({ isActive }) =>
                    `flex min-h-11 items-center gap-3 rounded-[10px] px-3.5 text-sm transition-colors ${
                      isActive
                        ? "bg-nav-selected font-bold text-white"
                        : "font-semibold text-nav-text hover:bg-nav-selected hover:text-white"
                    }`
                  }
                >
                  <Icon size={18} />
                  {t(labelKey)}
                  {count ? ` · ${count}` : null}
                </NavLink>
              </li>
            );
          })}
        </ul>
      </nav>
    </>
  );

  return (
    <>
      {/* Desktop sidebar - always visible */}
      <aside className="on-dark hidden md:flex h-screen w-64 flex-col bg-nav">
        {sidebarContent}
      </aside>

      {/* Mobile overlay + drawer */}
      {isOpen && (
        <div className="fixed inset-0 z-40 md:hidden">
          <div className="fixed inset-0 bg-black/50" onClick={close} />
          <aside className="on-dark fixed inset-y-0 left-0 z-50 flex w-64 flex-col bg-nav shadow-xl">
            {sidebarContent}
          </aside>
        </div>
      )}
    </>
  );
}
