import { useState } from "react";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useFraudSignals, useFraudAction } from "@/hooks/useQuery";
import { formatDateTime, formatETB } from "@/lib/i18n";

const severityVariant: Record<string, "danger" | "warning" | "default"> = {
  high: "danger",
  medium: "warning",
  low: "default",
};

/* FRD-05: what a reviewed signal was decided as. */
const resolutionVariant: Record<string, "success" | "danger" | "info"> = {
  approve: "success",
  reject: "danger",
  whitelist: "info",
};

/** FRD-05: the action response (fraud-detection-architecture.md section 19). */
interface OpenEscrow {
  escrowId: string;
  status: string;
  party: string;
  amount: number;
}

interface FraudActionResult {
  resolution?: string;
  userId?: string;
  suspended?: boolean;
  sessionsEnded?: boolean;
  openEscrows?: OpenEscrow[];
  openEscrowsComplete?: boolean;
  whitelistUntil?: string;
}

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

export function FraudReviewPage() {
  const { t, locale } = useLocale();
  const { data, isLoading } = useFraudSignals();
  const fraudAction = useFraudAction();

  const resp = data as Record<string, unknown> | undefined;
  const signals = (resp?.signals as Record<string, unknown>[]) || [];

  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [result, setResult] = useState<FraudActionResult | null>(null);
  // Reject suspends the account, so it asks once more.
  const [confirmingReject, setConfirmingReject] = useState<string | null>(null);

  function handleAction(id: string, action: string) {
    setErrorMsg(null);
    setResult(null);
    setConfirmingReject(null);
    fraudAction.mutate(
      { id, action: { action } },
      {
        onSuccess: (res: unknown) => setResult((res ?? {}) as FraudActionResult),
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          const key = code ? `errors.${code}` : "errors.actionFailed";
          const msg = t(key);
          setErrorMsg(msg !== key ? msg : t("errors.actionFailed"));
        },
      },
    );
  }

  const columns = [
    { key: "signalType", header: t("fraud.signalType") },
    {
      key: "severity",
      header: t("fraud.severity"),
      render: (row: Record<string, unknown>) => {
        const sev = String(row.severity || "");
        return (
          <Badge variant={severityVariant[sev] || "default"}>
            {t(`fraud.${sev}`) || sev}
          </Badge>
        );
      },
    },
    { key: "userId", header: t("users.name") },
    {
      key: "createdAt",
      header: t("common.date"),
      render: (row: Record<string, unknown>) =>
        row.createdAt ? formatDateTime(String(row.createdAt)) : "",
    },
    {
      key: "status",
      header: t("escrows.status"),
      render: (row: Record<string, unknown>) => {
        const resolution = String(row.resolution || "");
        if (row.status === "reviewed" && resolutionVariant[resolution]) {
          return (
            <Badge variant={resolutionVariant[resolution]}>
              {t(`fraud.resolution_${resolution}`)}
            </Badge>
          );
        }
        return (
          <Badge variant={row.status === "reviewed" ? "success" : "warning"}>
            {t(`fraud.${row.status}`)}
          </Badge>
        );
      },
    },
    {
      key: "actions",
      header: t("common.actions"),
      render: (row: Record<string, unknown>) => {
        if (row.status === "reviewed") return null;
        const id = String(row.signalId || "");
        if (confirmingReject === id) {
          return (
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-xs text-danger">{t("fraud.confirmReject")}</span>
              <button
                onClick={(e) => { e.stopPropagation(); handleAction(id, "reject"); }}
                className="rounded bg-danger px-2 py-1 text-xs text-white hover:opacity-90"
              >
                {t("fraud.confirmRejectButton")}
              </button>
              <button
                onClick={(e) => { e.stopPropagation(); setConfirmingReject(null); }}
                className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50"
              >
                {t("common.cancel")}
              </button>
            </div>
          );
        }
        return (
          <div className="flex gap-2">
            <button
              onClick={(e) => { e.stopPropagation(); handleAction(id, "approve"); }}
              className="rounded bg-success px-2 py-1 text-xs text-white hover:opacity-90"
            >
              {t("fraud.approve")}
            </button>
            <button
              onClick={(e) => { e.stopPropagation(); setConfirmingReject(id); }}
              className="rounded bg-danger px-2 py-1 text-xs text-white hover:opacity-90"
            >
              {t("fraud.reject")}
            </button>
            <button
              onClick={(e) => { e.stopPropagation(); handleAction(id, "whitelist"); }}
              className="rounded bg-gray-500 px-2 py-1 text-xs text-white hover:opacity-90"
            >
              {t("fraud.whitelist")}
            </button>
          </div>
        );
      },
    },
  ];

  function renderResult(r: FraudActionResult) {
    if (r.resolution === "approve") {
      return <p>{t("fraud.result_approve")}</p>;
    }
    if (r.resolution === "whitelist") {
      return (
        <p>
          {fill(t("fraud.result_whitelist"), {
            date: r.whitelistUntil ? formatDateTime(r.whitelistUntil) : "",
          })}
        </p>
      );
    }
    const escrows = r.openEscrows ?? [];
    return (
      <div className="space-y-2">
        <p>{fill(t("fraud.result_reject"), { userId: r.userId ?? "" })}</p>
        {r.sessionsEnded === false && (
          <p className="text-warning">{t("fraud.result_sessionsNotEnded")}</p>
        )}
        <p className="font-semibold">{t("fraud.openEscrows")}</p>
        <p className="text-text-muted">{t("fraud.openEscrowsHint")}</p>
        {escrows.length === 0 ? (
          <p>{t("fraud.noOpenEscrows")}</p>
        ) : (
          <ul className="list-disc pl-5">
            {escrows.map((e) => (
              <li key={e.escrowId}>
                {e.escrowId}: {t(`users.${e.party}`)}, {t(`escrows.${e.status}`)},{" "}
                {formatETB(Number(e.amount) || 0, locale)}
              </li>
            ))}
          </ul>
        )}
        {r.openEscrowsComplete === false && (
          <p className="text-warning">{t("fraud.openEscrowsIncomplete")}</p>
        )}
      </div>
    );
  }

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("fraud.title")}</h2>
      {errorMsg && (
        <div
          role="alert"
          className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger"
        >
          {errorMsg}
        </div>
      )}
      {result && (
        <div
          role="status"
          className="mb-4 rounded border border-info/30 bg-info/10 px-4 py-3 text-sm"
        >
          {renderResult(result)}
        </div>
      )}
      <Card>
        <DataTable columns={columns} data={signals} isLoading={isLoading} />
      </Card>
    </div>
  );
}
