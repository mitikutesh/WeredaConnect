import { Search } from "lucide-react";
import { useLocale } from "@/hooks/useLocale";

interface SearchInputProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
}

export function SearchInput({ value, onChange, placeholder }: SearchInputProps) {
  const { t } = useLocale();

  return (
    <div className="relative">
      <Search
        size={16}
        className="absolute left-3 top-1/2 -translate-y-1/2 text-slate"
      />
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder || t("common.search")}
        className="w-full rounded-[14px] border border-field bg-ground py-2 pl-9 pr-4 text-sm outline-none focus:border-ink focus:ring-1 focus:ring-ink"
      />
    </div>
  );
}
