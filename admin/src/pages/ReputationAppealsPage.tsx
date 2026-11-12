import { useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { AppealPhoto } from "@/components/reputation/AppealPhoto";
import { ReputationActionDialog } from "@/components/reputation/ReputationActionDialog";
import { useLocale } from "@/hooks/useLocale";
import { useReputationAppeals, useReputationDecision } from "@/hooks/useAdminReputation";
import { formatDateTime } from "@/lib/i18n";
import {
  APPEAL_CHANGED,
  maxUntilDays,
  type AppealItem,
  type AppealQueueStatus,
  type ReputationDecision,
} from "@/lib/reputation";
import { partyName } from "@/lib/user";

/* ESC-32 (reputation-api.md 1.4.0, 9.2 and 9.3): the appeals queue, oldest first. "Appeals" are
 * penalties a farmer or trucker asked an admin to excuse; "reviews" are overdue deliveries, which
 * restrict the trucker until an admin decides. Accepting an appeal excuses the event (its points
 * stop counting); upholding keeps them. Either way the admin writes a note the account holder
 * reads, and the server audits the decision. */

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

const itemKey = (item: AppealItem) => `${item.userId}|${item.eventId}`;

export function ReputationAppealsPage() {
  const { t, locale } = useLocale();
  const [tab, setTab] = useState<AppealQueueStatus>("pending");
  const { data, isLoading, isError, hasNextPage, fetchNextPage, isFetchingNextPage } = useReputationAppeals(tab);
  const decide = useReputationDecision();
  const [open, setOpen] = useState<{ item: AppealItem; decision: ReputationDecision } | null>(null);
  const [dialogError, setDialogError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [changed, setChanged] = useState<string | null>(null);

  function label(key: string, fallback: string): string {
    const text = t(key);
    return text === key ? fallback : text;
  }

  // In the order the queue gives them; a retried page cannot double an appeal.
  const appeals: AppealItem[] = [];
  const seen = new Set<string>();
  for (const page of data?.pages ?? []) {
    for (const item of page.appeals ?? []) {
      if (seen.has(itemKey(item))) continue;
      seen.add(itemKey(item));
      appeals.push(item);
    }
  }

  function changeTab(next: AppealQueueStatus) {
    setTab(next);
    setNotice(null);
    setChanged(null);
  }

  function confirm(note: string, until?: string) {
    if (!open) return;
    const decision = open.decision;
    setDialogError(null);
    decide.mutate(
      {
        userId: open.item.userId,
        eventId: open.item.eventId,
        decision,
        note,
        until,
        seenAppealTextAt: open.item.appealTextAt,
      },
      {
        onSuccess: (result) => {
          setOpen(null);
          setNotice(result?.alreadyDecided ? t("reputation.alreadyDone") : t(`reputation.done_${decision}`));
        },
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          const text = code ? label(`errors.${code}`, t("errors.actionFailed")) : t("errors.actionFailed");
          // The appeal is not the one this page showed: close the dialog, the queue reloads.
          if (code === APPEAL_CHANGED) {
            setOpen(null);
            setChanged(text);
          } else {
            setDialogError(text);
          }
        },
      },
    );
  }

  const tabButton = (value: AppealQueueStatus) => (
    <button
      type="button"
      aria-pressed={tab === value}
      onClick={() => changeTab(value)}
      className={`rounded px-3 py-1 text-sm ${
        tab === value ? "bg-primary text-white" : "border border-gray-300 hover:bg-gray-50"
      }`}
    >
      {t(`reputation.tab_${value}`)}
    </button>
  );

  let body;
  if (isLoading) {
    body = (
      <Card>
        <div className="flex items-center justify-center py-12 text-text-muted">{t("common.loading")}</div>
      </Card>
    );
  } else if (appeals.length === 0) {
    body = isError ? null : (
      <Card>
        <div className="flex items-center justify-center py-12 text-text-muted">
          {t(tab === "pending" ? "reputation.emptyPending" : "reputation.emptyReview")}
        </div>
      </Card>
    );
  } else {
    body = appeals.map((item) => {
      const name = partyName(item.displayName, item.userId, locale);
      return (
        <section key={itemKey(item)} aria-label={`${name} ${item.eventId}`} className="mb-4">
          <Card>
            <div className="mb-3 flex flex-wrap items-start justify-between gap-2">
              <div className="space-y-1">
                <div className="flex flex-wrap items-center gap-2">
                  <Badge variant="warning">{label(`reputation.code_${item.code}`, item.code)}</Badge>
                  <Badge variant="danger">{fill(t("reputation.pointsCount"), { points: String(item.points ?? 0) })}</Badge>
                  {item.role && <Badge>{label(`users.${item.role}`, item.role)}</Badge>}
                </div>
                <p className="text-sm">
                  <Link to={`/users/${item.userId}`} className="font-medium text-primary hover:underline">
                    {name}
                  </Link>
                  <span className="ml-2 text-xs text-text-muted">
                    {item.tripId
                      ? `${t("reputation.trip")} ${item.tripId}`
                      : item.orderId
                        ? `${t("reputation.order")} ${item.orderId}`
                        : ""}
                  </span>
                </p>
                <p className="text-xs text-text-muted">
                  {item.occurredAt && fill(t("reputation.occurred"), { date: formatDateTime(item.occurredAt) })}
                  {item.appealedAt && ` · ${fill(t("reputation.appealedAt"), { date: formatDateTime(item.appealedAt) })}`}
                </p>
              </div>
            </div>
            {item.text ? (
              <p className="max-w-3xl whitespace-pre-line text-sm">{item.text}</p>
            ) : (
              <p className="text-sm text-text-muted">{t("reputation.noText")}</p>
            )}
            {item.hasPhoto && (
              <div className="mt-3">
                <AppealPhoto url={item.appealPhotoUrl} />
              </div>
            )}
            <div className="mt-3 flex flex-wrap gap-2 border-t border-gray-100 pt-3">
              <button
                type="button"
                onClick={() => {
                  setDialogError(null);
                  setNotice(null);
                  setChanged(null);
                  setOpen({ item, decision: "excuse" });
                }}
                disabled={decide.isPending}
                className="rounded bg-primary px-3 py-1 text-sm text-white hover:opacity-90 disabled:opacity-50"
              >
                {t("reputation.acceptAppeal")}
              </button>
              <button
                type="button"
                onClick={() => {
                  setDialogError(null);
                  setNotice(null);
                  setChanged(null);
                  setOpen({ item, decision: "uphold" });
                }}
                disabled={decide.isPending}
                className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
              >
                {t("reputation.upholdPenalty")}
              </button>
            </div>
          </Card>
        </section>
      );
    });
  }

  // Only the uphold of an overdue delivery review sets an end date (the restriction's).
  const review = open?.decision === "uphold" && open.item.code === "DELIVERY_OVERDUE";

  return (
    <div>
      <h2 className="text-xl font-bold">{t("reputation.appealsTitle")}</h2>
      <p className="mb-4 mt-1 max-w-3xl text-sm text-text-muted">{t("reputation.appealsSubtitle")}</p>
      <div className="mb-4 flex flex-wrap gap-2">
        {tabButton("pending")}
        {tabButton("review")}
      </div>
      {isError && (
        <div role="alert" className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
          {t("reputation.appealsLoadFailed")}
        </div>
      )}
      {notice && (
        <div role="status" className="mb-4 rounded border border-success/30 bg-success/10 px-4 py-2 text-sm text-success">
          {notice}
        </div>
      )}
      {changed && (
        <div role="alert" className="mb-4 rounded border border-warning/30 bg-warning/10 px-4 py-2 text-sm text-warning">
          {changed}
        </div>
      )}
      {body}
      {hasNextPage && (
        <div className="flex justify-center p-4">
          <button
            type="button"
            onClick={() => fetchNextPage()}
            disabled={isFetchingNextPage}
            className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
          >
            {t("reputation.loadMoreAppeals")}
          </button>
        </div>
      )}
      {open && (
        <ReputationActionDialog
          title={`${t(`reputation.title_${open.decision}`)}: ${label(`reputation.code_${open.item.code}`, open.item.code)}`}
          help={t(review ? "reputation.help_upholdReview" : `reputation.help_${open.decision}`)}
          reasonLabel={t("reputation.note")}
          confirmLabel={t(open.decision === "excuse" ? "reputation.acceptAppeal" : "reputation.upholdPenalty")}
          danger={open.decision === "uphold"}
          until={review ? "optional" : "none"}
          untilLabel={t("reputation.untilOptional")}
          maxDays={maxUntilDays("uphold")}
          busy={decide.isPending}
          error={dialogError}
          onConfirm={confirm}
          onCancel={() => setOpen(null)}
        />
      )}
    </div>
  );
}
