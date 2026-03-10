import { useState } from "react";
import { useParams } from "react-router";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useUserDetail, useUserAction } from "@/hooks/useQuery";
import { formatDate } from "@/lib/i18n";

export function UserDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t } = useLocale();
  const { data, isLoading } = useUserDetail(id || "");
  const userAction = useUserAction();

  const resp = data as Record<string, unknown> | undefined;
  const user = (resp?.user as Record<string, unknown>) || {};

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  function handleAction(action: string) {
    if (!id) return;
    setErrorMsg(null);
    userAction.mutate(
      { id, action: { action } },
      {
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          const key = code ? `errors.${code}` : "errors.actionFailed";
          const msg = t(key);
          setErrorMsg(msg !== key ? msg : t("errors.actionFailed"));
        },
      },
    );
  }

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("users.detail")}</h2>
      {errorMsg && (
        <div className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
          {errorMsg}
        </div>
      )}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <Card>
          <dl className="space-y-3">
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("users.name")}</dt>
              <dd className="font-medium">{String(user.displayName || "")}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("users.phone")}</dt>
              <dd>{String(user.phoneNumber || "")}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("users.role")}</dt>
              <dd><Badge>{String(user.role || "")}</Badge></dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("users.woreda")}</dt>
              <dd>{String(user.woreda || "")}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("users.joined")}</dt>
              <dd>{user.createdAt ? formatDate(String(user.createdAt)) : ""}</dd>
            </div>
          </dl>

          <div className="mt-6 flex flex-wrap gap-2">
            <button
              onClick={() => handleAction(user.suspended ? "unsuspend" : "suspend")}
              className={`rounded px-3 py-1.5 text-sm text-white ${user.suspended ? "bg-success" : "bg-danger"}`}
            >
              {user.suspended ? t("users.activate") : t("users.suspend")}
            </button>
            <button
              onClick={() => handleAction("verify")}
              className="rounded bg-primary px-3 py-1.5 text-sm text-white"
            >
              {t("users.verify")}
            </button>
          </div>
        </Card>

        <Card>
          <h3 className="mb-4 font-semibold">{t("users.transactionHistory")}</h3>
          <p className="text-sm text-text-muted">{t("common.noData")}</p>
        </Card>
      </div>
    </div>
  );
}
