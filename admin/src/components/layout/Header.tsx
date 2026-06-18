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
    <header className="flex h-16 items-center justify-between border-b border-gray-200 bg-white px-4 md:px-6">
      <div className="flex items-center gap-3">
        <button
          onClick={open}
          aria-label={t("nav.openMenu")}
          className="md:hidden rounded-lg p-2 text-text-muted hover:bg-gray-100"
        >
          <Menu size={20} />
        </button>
        <h1 className="hidden md:block text-lg font-semibold text-primary">
          {t("app.title")}
        </h1>
      </div>
      <div className="flex items-center gap-2 md:gap-4">
        <LanguageSwitcher />
        <span className="hidden sm:inline text-sm text-text-muted">{user?.phone}</span>
        <button
          onClick={logout}
          aria-label={t("auth.logout")}
          className="flex items-center gap-1 rounded-lg px-2 py-2 md:px-3 text-sm text-text-muted hover:bg-gray-100"
        >
          <LogOut size={16} />
          <span className="hidden sm:inline">{t("auth.logout")}</span>
        </button>
      </div>
    </header>
  );
}
