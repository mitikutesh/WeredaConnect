import { LogOut, Menu } from "lucide-react";
import { useAuth } from "@/hooks/useAuth";
import { useLocale } from "@/hooks/useLocale";
import { useSidebar } from "@/hooks/useSidebar";
import { LanguageSwitcher } from "./LanguageSwitcher";

export function Header() {
  const { user, logout } = useAuth();
  const { t } = useLocale();
  const { open } = useSidebar();

  return (
    <header className="flex h-16 items-center justify-between border-b border-line bg-ground px-4 md:px-8">
      <div className="flex items-center gap-3">
        <button
          onClick={open}
          aria-label={t("nav.openMenu")}
          className="md:hidden rounded-[10px] p-2.5 text-ink hover:bg-paper"
        >
          <Menu size={20} />
        </button>
        <p className="hidden md:block text-sm font-semibold text-slate">
          {t("app.subtitle")}
        </p>
      </div>
      <div className="flex items-center gap-2 md:gap-4">
        <LanguageSwitcher />
        <span className="hidden sm:inline text-sm text-slate">{user?.phone}</span>
        <button
          onClick={logout}
          aria-label={t("auth.logout")}
          className="flex min-h-11 items-center gap-1.5 rounded-[10px] px-3 text-sm font-semibold text-ink-soft hover:bg-paper hover:text-ink"
        >
          <LogOut size={16} />
          <span className="hidden sm:inline">{t("auth.logout")}</span>
        </button>
      </div>
    </header>
  );
}
