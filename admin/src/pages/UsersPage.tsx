import { useState } from "react";
import { useNavigate } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { SearchInput } from "@/components/ui/SearchInput";
import { useLocale } from "@/hooks/useLocale";
import { useUsers } from "@/hooks/useQuery";
import { formatDate } from "@/lib/i18n";

const roleVariant: Record<string, "success" | "info" | "warning" | "danger"> = {
  farmer: "success",
  trucker: "info",
  buyer: "warning",
  admin: "danger",
};

export function UsersPage() {
  const { t } = useLocale();
  const [search, setSearch] = useState("");
  const navigate = useNavigate();
  const { data, isLoading } = useUsers(search ? `search=${encodeURIComponent(search)}` : undefined);

  const resp = data as Record<string, unknown> | undefined;
  const users = (resp?.users as Record<string, unknown>[]) || [];

  const columns = [
    { key: "displayName", header: t("users.name") },
    { key: "phoneNumber", header: t("users.phone") },
    {
      key: "role",
      header: t("users.role"),
      render: (row: Record<string, unknown>) => {
        const role = String(row.role || "");
        return (
          <Badge variant={roleVariant[role] || "default"}>
            {t(`users.${role}`) || role}
          </Badge>
        );
      },
    },
    { key: "woreda", header: t("users.woreda") },
    {
      key: "status",
      header: t("users.status"),
      render: (row: Record<string, unknown>) => (
        <Badge variant={row.suspended ? "danger" : "success"}>
          {row.suspended ? t("users.suspended") : t("users.active")}
        </Badge>
      ),
    },
    {
      key: "createdAt",
      header: t("users.joined"),
      render: (row: Record<string, unknown>) =>
        row.createdAt ? formatDate(String(row.createdAt)) : "",
    },
  ];

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("users.title")}</h2>
      <Card>
        <div className="mb-4 max-w-full md:max-w-sm">
          <SearchInput value={search} onChange={setSearch} />
        </div>
        <DataTable
          columns={columns}
          data={users}
          isLoading={isLoading}
          onRowClick={(row) => navigate(`/users/${row.userId}`)}
        />
      </Card>
    </div>
  );
}
