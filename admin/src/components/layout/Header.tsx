import { LogOut } from "lucide-react";
import { useAuth } from "@/hooks/useAuth";
import { useLocale } from "@/hooks/useLocale";
import { LanguageSwitcher } from "./LanguageSwitcher";

export function Header() {
  const { user, logout } = useAuth();
  const { t } = useLocale();

  return (
    <header className="flex h-16 items-center justify-between border-b border-gray-200 bg-white px-6">
      <h1 className="text-lg font-semibold text-primary">
        {t("app.title")}
      </h1>
      <div className="flex items-center gap-4">
        <LanguageSwitcher />
        <span className="text-sm text-text-muted">{user?.phone}</span>
        <button
          onClick={logout}
          className="flex items-center gap-1 rounded-lg px-3 py-2 text-sm text-text-muted hover:bg-gray-100"
        >
          <LogOut size={16} />
          {t("auth.logout")}
        </button>
      </div>
    </header>
  );
}
