import { useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useReputationAction, useReputationDecision, useUserReputation } from "@/hooks/useAdminReputation";
import { formatDate, formatDateTime } from "@/lib/i18n";
import {
  APPEAL_CHANGED,
  appealVariant,
  canExcuse,
  canUphold,
  eventStatusVariant,
  isSystemRow,
  maxUntilDays,
  needsUntil,
  tierVariant,
  type ReputationAction,
  type ReputationDecision,
  type ReputationEventRow,
  type RestrictionRow,
} from "@/lib/reputation";
import { ReputationActionDialog } from "./ReputationActionDialog";
import { AppealPhoto } from "./AppealPhoto";

/* ESC-32 (reputation-api.md 1.4.0 section 9): a user's reputation on the user page. Points, tier,
 * the Low reputation tag, restrictions with their end, and every event with its appeal. An admin
 * can excuse or uphold an event and tag, untag, restrict or lift, each with a reason the server
 * audits. Admin only: farmers and truckers see their own reputation in the app, never this view
 * (the buyer's id, the base points and other users' appeals stay here). */

type Open =
  | { kind: "decision"; decision: ReputationDecision; event: ReputationEventRow }
  | { kind: "action"; action: ReputationAction };

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

export function ReputationPanel({ userId }: { userId: string }) {
  const { t } = useLocale();
  const { data, isLoading, isError, error, hasNextPage, fetchNextPage, isFetchingNextPage } = useUserReputation(userId);
  const decide = useReputationDecision();
  const act = useReputationAction();
  const [open, setOpen] = useState<Open | null>(null);
  const [dialogError, setDialogError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [changed, setChanged] = useState<string | null>(null);

  function label(key: string, fallback: string): string {
    const text = t(key);
    return text === key ? fallback : text;
  }

  function errorText(err: unknown): string {
    const code = (err as { code?: string }).code;
    return code ? label(`errors.${code}`, t("errors.actionFailed")) : t("errors.actionFailed");
  }

  function openDialog(next: Open) {
    setDialogError(null);
    setNotice(null);
    setChanged(null);
    setOpen(next);
  }

  function confirm(reason: string, until?: string) {
    if (!open) return;
    setDialogError(null);
    if (open.kind === "decision") {
      const decision = open.decision;
      decide.mutate(
        { userId, eventId: open.event.eventId, decision, note: reason, until, seenAppealTextAt: open.event.appeal?.textAt },
        {
          onSuccess: (result) => {
            setOpen(null);
            setNotice(result?.alreadyDecided ? t("reputation.alreadyDone") : t(`reputation.done_${decision}`));
          },
          onError: (err: unknown) => {
            // The appeal is not the one this page showed: close the dialog, the panel reloads.
            if ((err as { code?: string }).code === APPEAL_CHANGED) {
              setOpen(null);
              setChanged(errorText(err));
            } else {
              setDialogError(errorText(err));
            }
          },
        },
      );
    } else {
      const action = open.action;
      act.mutate(
        { userId, action, reason, until },
        {
          onSuccess: (result) => {
            setOpen(null);
            setNotice(result?.alreadyApplied ? t("reputation.alreadyDone") : t(`reputation.done_${action}`));
          },
          onError: (err: unknown) => setDialogError(errorText(err)),
        },
      );
    }
  }

  const heading = <h3 className="mb-4 font-semibold">{t("reputation.title")}</h3>;

  if (isLoading) {
    return (
      <Card>
        {heading}
        <p className="text-sm text-text-muted">{t("common.loading")}</p>
      </Card>
    );
  }

  const first = data?.pages?.[0];
  if (isError || !first) {
    const status = (error as { status?: number } | null)?.status;
    return (
      <Card>
        {heading}
        <p role="alert" className="text-sm text-danger">
          {status === 404 ? t("reputation.notFound") : t("reputation.loadFailed")}
        </p>
      </Card>
    );
  }

  const reputation = first.reputation;
  const restrictions = first.restrictions ?? [];
  // A retried page cannot double an event.
  const events: ReputationEventRow[] = [];
  const seen = new Set<string>();
  for (const page of data.pages) {
    for (const event of page.events ?? []) {
      if (seen.has(event.eventId)) continue;
      seen.add(event.eventId);
      events.push(event);
    }
  }

  if (reputation.role === "buyer") {
    return (
      <Card>
        {heading}
        <p className="text-sm text-text-muted">{t("reputation.buyerNone")}</p>
      </Card>
    );
  }

  const writable = reputation.penaltiesEnabled !== false;
  const busy = decide.isPending || act.isPending;
  const thresholds = reputation.thresholds;

  const ratingText =
    reputation.effectiveRating === undefined
      ? t("reputation.noRatings")
      : `${reputation.effectiveRating.toFixed(1)} (${fill(t("reputation.ratingDetail"), {
          average: (reputation.ratingAverage ?? reputation.effectiveRating).toFixed(1),
          count: String(reputation.ratingCount ?? 0),
          deduction: (reputation.ratingDeduction ?? 0).toFixed(1),
        })})`;

  const reliabilityCounts = fill(t("reputation.reliabilityDetail"), {
    completed: String(reputation.completedOrders90d ?? 0),
    faults: String(reputation.faultCancels90d ?? 0),
    noShows: String(reputation.noShows90d ?? 0),
  });

  function restrictionText(r: { since?: string; until?: string }) {
    return fill(t(r.until ? "reputation.restrictionUntil" : "reputation.restrictionReview"), {
      since: r.since ? formatDate(r.since) : "",
      until: r.until ? formatDate(r.until) : "",
    });
  }

  function actionButton(action: ReputationAction, danger = false) {
    return (
      <button
        type="button"
        onClick={() => openDialog({ kind: "action", action })}
        disabled={!writable || busy}
        className={`rounded px-3 py-1.5 text-sm disabled:opacity-50 ${
          danger ? "bg-danger text-white hover:opacity-90" : "border border-gray-300 hover:bg-gray-50"
        }`}
      >
        {t(`reputation.action_${action}`)}
      </button>
    );
  }

  function restrictionRow(row: RestrictionRow) {
    return (
      <li key={row.eventId} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2">
        <Badge variant={row.status === "active" ? "danger" : "default"}>
          {label(`reputation.status_${row.status}`, row.status)}
        </Badge>
        <span className="font-medium">{label(`reputation.cause_${row.cause}`, row.cause)}</span>
        <span className="text-text-muted">{restrictionText(row)}</span>
        {row.reason && <span>{row.reason}</span>}
        {row.by && <span className="text-text-muted">{fill(t("reputation.setBy"), { by: row.by })}</span>}
        {row.liftedAt && (
          <span className="text-text-muted">
            {fill(t("reputation.liftedBy"), { date: formatDateTime(row.liftedAt), by: row.liftedBy ?? "" })}
          </span>
        )}
      </li>
    );
  }

  function systemDetail(event: ReputationEventRow): string {
    if (event.code === "TIER_CHANGED") {
      return fill(t("reputation.tierChange"), {
        from: label(`reputation.tier_${event.fromTier}`, event.fromTier ?? ""),
        to: label(`reputation.tier_${event.toTier}`, event.toTier ?? ""),
      });
    }
    const parts: string[] = [];
    if (event.cause) parts.push(label(`reputation.cause_${event.cause}`, event.cause));
    if (event.until) parts.push(fill(t("reputation.untilDate"), { until: formatDate(event.until) }));
    if (event.reason) parts.push(event.reason);
    if (event.by) parts.push(fill(t("reputation.setBy"), { by: event.by }));
    return parts.join(" · ");
  }

  function eventRow(event: ReputationEventRow) {
    const system = isSystemRow(event);
    const appeal = event.appeal;
    const points = event.points ?? 0;
    return (
      <tr key={event.eventId} className="border-t border-gray-100 align-top">
        <td className="whitespace-nowrap py-2 pr-3 text-text-muted">
          {event.occurredAt ? formatDateTime(event.occurredAt) : ""}
        </td>
        <td className="py-2 pr-3">
          <span className="font-medium">{label(`reputation.code_${event.code}`, event.code)}</span>
          {system ? (
            <p className="text-xs text-text-muted">{systemDetail(event)}</p>
          ) : (
            <p className="text-xs text-text-muted">
              {event.tripId
                ? `${t("reputation.trip")} ${event.tripId}`
                : event.orderId
                  ? `${t("reputation.order")} ${event.orderId}`
                  : ""}
              {event.buyerId && (
                <>
                  {" · "}
                  {t("reputation.buyer")}{" "}
                  <Link to={`/users/${event.buyerId}`} className="text-primary hover:underline">
                    {event.buyerId}
                  </Link>
                </>
              )}
            </p>
          )}
        </td>
        <td className="whitespace-nowrap py-2 pr-3">
          {!system && (
            <>
              {fill(t("reputation.pointsDetail"), {
                remaining: String(event.remainingPoints ?? 0),
                points: String(points),
              })}
              {points === 0 && event.zeroReason && (
                <p className="text-xs text-text-muted">{label(`reputation.zero_${event.zeroReason}`, event.zeroReason)}</p>
              )}
              {event.basePoints !== undefined && event.basePoints !== points && (
                <p className="text-xs text-text-muted">
                  {fill(t("reputation.basePoints"), { points: String(event.basePoints) })}
                </p>
              )}
            </>
          )}
        </td>
        <td className="py-2 pr-3">
          {/* A tier change is a moment, not a state: it has no status to show. */}
          {event.code !== "TIER_CHANGED" && (
            <Badge variant={eventStatusVariant(event.status)}>{label(`reputation.status_${event.status}`, event.status)}</Badge>
          )}
        </td>
        <td className="max-w-md py-2 pr-3">
          {appeal && appeal.status !== "none" && (
            <div className="space-y-1">
              <Badge variant={appealVariant(appeal.status)}>{label(`reputation.appeal_${appeal.status}`, appeal.status)}</Badge>
              {appeal.text && <p className="whitespace-pre-line">{appeal.text}</p>}
              {appeal.hasPhoto && <AppealPhoto url={appeal.appealPhotoUrl} />}
              {appeal.note && (
                <p className="text-xs text-text-muted">
                  {fill(t("reputation.decidedLine"), {
                    date: appeal.decidedAt ? formatDateTime(appeal.decidedAt) : "",
                    by: appeal.decidedBy ?? "",
                    note: appeal.note,
                  })}
                </p>
              )}
            </div>
          )}
        </td>
        <td className="whitespace-nowrap py-2">
          <div className="flex gap-2">
            {canExcuse(event) && (
              <button
                type="button"
                onClick={() => openDialog({ kind: "decision", decision: "excuse", event })}
                disabled={!writable || busy}
                aria-label={`${t("reputation.excuse")} ${event.eventId}`}
                className="rounded bg-primary px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
              >
                {t("reputation.excuse")}
              </button>
            )}
            {canUphold(event) && (
              <button
                type="button"
                onClick={() => openDialog({ kind: "decision", decision: "uphold", event })}
                disabled={!writable || busy}
                aria-label={`${t("reputation.uphold")} ${event.eventId}`}
                className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50"
              >
                {t("reputation.uphold")}
              </button>
            )}
          </div>
        </td>
      </tr>
    );
  }

  let dialog = null;
  if (open?.kind === "action") {
    const action = open.action;
    dialog = (
      <ReputationActionDialog
        title={t(`reputation.action_${action}`)}
        help={t(`reputation.help_${action}`)}
        reasonLabel={t("reputation.reason")}
        confirmLabel={t(`reputation.action_${action}`)}
        danger={action === "restrict" || action === "tag"}
        until={needsUntil(action) ? "required" : "none"}
        maxDays={maxUntilDays(action)}
        busy={busy}
        error={dialogError}
        onConfirm={confirm}
        onCancel={() => setOpen(null)}
      />
    );
  } else if (open?.kind === "decision") {
    // Only the uphold of an overdue delivery review sets an end date (the restriction's).
    const review = open.decision === "uphold" && open.event.code === "DELIVERY_OVERDUE";
    dialog = (
      <ReputationActionDialog
        title={`${t(`reputation.title_${open.decision}`)}: ${label(`reputation.code_${open.event.code}`, open.event.code)}`}
        help={t(review ? "reputation.help_upholdReview" : `reputation.help_${open.decision}`)}
        reasonLabel={t("reputation.note")}
        confirmLabel={t(`reputation.${open.decision}`)}
        danger={open.decision === "uphold"}
        until={review ? "optional" : "none"}
        untilLabel={t("reputation.untilOptional")}
        maxDays={maxUntilDays("uphold")}
        busy={busy}
        error={dialogError}
        onConfirm={confirm}
        onCancel={() => setOpen(null)}
      />
    );
  }

  return (
    <Card>
      <section aria-label={t("reputation.title")}>
        <div className="mb-4 flex flex-wrap items-baseline justify-between gap-2">
          <h3 className="font-semibold">{t("reputation.title")}</h3>
          <span className="text-xs text-text-muted">
            {fill(t("reputation.asOf"), { date: formatDateTime(reputation.asOf) })}
          </span>
        </div>

        {!writable && (
          <p role="status" className="mb-4 rounded border border-warning/30 bg-warning/10 px-4 py-2 text-sm text-warning">
            {t("reputation.penaltiesOff")}
          </p>
        )}
        {notice && (
          <p role="status" className="mb-4 rounded border border-success/30 bg-success/10 px-4 py-2 text-sm text-success">
            {notice}
          </p>
        )}
        {changed && (
          <p role="alert" className="mb-4 rounded border border-warning/30 bg-warning/10 px-4 py-2 text-sm text-warning">
            {changed}
          </p>
        )}

        <dl className="grid grid-cols-1 gap-x-8 gap-y-3 text-sm md:grid-cols-2">
          <div className="flex justify-between gap-4">
            <dt className="text-text-muted">{t("reputation.tier")}</dt>
            <dd>
              <Badge variant={tierVariant(reputation.tier)}>
                {label(`reputation.tier_${reputation.tier}`, reputation.tier ?? "")}
              </Badge>
            </dd>
          </div>
          <div className="flex justify-between gap-4">
            <dt className="text-text-muted">{t("reputation.activePoints")}</dt>
            <dd className="text-right">
              <span className="font-medium">{reputation.activePoints ?? 0}</span>
              {thresholds && (
                <span className="ml-2 text-xs text-text-muted">
                  {fill(t("reputation.thresholds"), {
                    warning: String(thresholds.warning),
                    low: String(thresholds.low),
                    restricted: String(thresholds.restricted),
                  })}
                </span>
              )}
            </dd>
          </div>
          <div className="flex justify-between gap-4">
            <dt className="text-text-muted">{t("reputation.effectiveRating")}</dt>
            <dd className="text-right">{ratingText}</dd>
          </div>
          <div className="flex justify-between gap-4">
            <dt className="text-text-muted">{t("reputation.reliability")}</dt>
            <dd className="text-right">
              {reputation.reliabilityPct === undefined ? t("reputation.reliabilityNone") : `${reputation.reliabilityPct}%`}
              <span className="ml-2 text-xs text-text-muted">{reliabilityCounts}</span>
            </dd>
          </div>
          {reputation.nextPointExpiryAt && (
            <div className="flex justify-between gap-4">
              <dt className="text-text-muted">{t("reputation.nextExpiry")}</dt>
              <dd>{formatDate(reputation.nextPointExpiryAt)}</dd>
            </div>
          )}
          <div className="flex justify-between gap-4">
            <dt className="text-text-muted">{t("reputation.restriction")}</dt>
            <dd className="text-right">
              {reputation.restriction ? (
                <>
                  <span className="font-medium text-danger">
                    {label(`reputation.cause_${reputation.restriction.cause}`, reputation.restriction.cause)}
                  </span>
                  <span className="ml-2 text-xs text-text-muted">{restrictionText(reputation.restriction)}</span>
                </>
              ) : (
                t("reputation.restrictionNone")
              )}
            </dd>
          </div>
          {reputation.override && (
            <div className="flex justify-between gap-4 md:col-span-2">
              <dt className="text-text-muted">
                {t(reputation.override.tag === "clear" ? "reputation.tagClear" : "reputation.tagLow")}
              </dt>
              <dd className="text-right">
                {fill(t("reputation.overrideDetail"), {
                  until: reputation.override.until ? formatDate(reputation.override.until) : "",
                  by: reputation.override.by ?? "",
                  reason: reputation.override.reason ?? "",
                })}
              </dd>
            </div>
          )}
        </dl>

        <div className="mt-4 flex flex-wrap gap-2">
          {actionButton("tag")}
          {actionButton("untag")}
          {reputation.restriction ? actionButton("lift") : actionButton("restrict", true)}
        </div>

        {restrictions.length > 0 && (
          <>
            <h4 className="mb-1 mt-6 text-sm font-semibold">{t("reputation.restrictionsHistory")}</h4>
            <ul className="divide-y divide-gray-100 text-sm">{restrictions.map(restrictionRow)}</ul>
          </>
        )}

        <h4 className="mb-1 mt-6 text-sm font-semibold">{t("reputation.events")}</h4>
        {events.length === 0 ? (
          <p className="text-sm text-text-muted">{t("reputation.noEvents")}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead>
                <tr className="text-xs text-text-muted">
                  <th className="py-2 pr-3 font-medium">{t("common.date")}</th>
                  <th className="py-2 pr-3 font-medium">{t("reputation.colEvent")}</th>
                  <th className="py-2 pr-3 font-medium">{t("reputation.colPoints")}</th>
                  <th className="py-2 pr-3 font-medium">{t("users.status")}</th>
                  <th className="py-2 pr-3 font-medium">{t("reputation.colAppeal")}</th>
                  <th className="py-2 font-medium">{t("common.actions")}</th>
                </tr>
              </thead>
              <tbody>{events.map(eventRow)}</tbody>
            </table>
          </div>
        )}
        {hasNextPage && (
          <div className="flex justify-center pt-4">
            <button
              type="button"
              onClick={() => fetchNextPage()}
              disabled={isFetchingNextPage}
              className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
            >
              {t("reputation.loadMore")}
            </button>
          </div>
        )}
      </section>
      {dialog}
    </Card>
  );
}
