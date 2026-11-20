import type { ReactNode } from "react";
import { ArrowRight, MapPin, ShoppingCart, Truck, Wallet } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { formatDateTime } from "@/lib/i18n";
import { formatGps, timelineActionLabel, timelineEntityVariant } from "@/lib/transaction";

type Row = Record<string, unknown>;

function s(v: unknown): string {
  return v == null ? "" : String(v);
}

/**
 * FRD-12: a handoff photo as a thumbnail that opens full size. The URL is a 15-minute presigned
 * GET the server signs for the admin view (the transaction view and the dispute evidence, FRD-14).
 */
export function HandoffPhoto({
  url,
  label,
  openLabel,
  size = "h-24 w-24",
}: {
  url: string;
  label: string;
  openLabel: string;
  size?: string;
}) {
  return (
    <a href={url} target="_blank" rel="noopener noreferrer" aria-label={openLabel} className="inline-block">
      <img src={url} alt={label} loading="lazy" className={`${size} rounded-lg border border-gray-200 object-cover`} />
    </a>
  );
}

const entityIcon: Record<string, ReactNode> = {
  order: <ShoppingCart size={16} />,
  delivery: <Truck size={16} />,
  escrow: <Wallet size={16} />,
};

/**
 * The stitched order, delivery and escrow timeline (GET /admin/transactions/{orderId}), shared by
 * the transaction view and the dispute evidence (FRD-14): who, what, when, and where when a GPS fix
 * was taken.
 */
export function TransactionTimeline({ timeline }: { timeline: Row[] }) {
  const { t } = useLocale();
  if (timeline.length === 0) {
    return <p className="text-sm text-text-muted">{t("common.noData")}</p>;
  }

  return (
    <ol className="space-y-4">
      {timeline.map((ev, i) => {
        const entity = s(ev.entity);
        const from = s(ev.fromState);
        const to = s(ev.toState);
        const loc = formatGps(ev.location);
        return (
          <li key={i} className="flex gap-3">
            <div className="mt-0.5 text-text-muted">{entityIcon[entity] || <ArrowRight size={16} />}</div>
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-sm font-medium">{timelineActionLabel(s(ev.action), t)}</span>
                <Badge variant={timelineEntityVariant[entity] || "default"}>
                  {t(`transactions.entity_${entity}`) === `transactions.entity_${entity}`
                    ? entity
                    : t(`transactions.entity_${entity}`)}
                </Badge>
              </div>
              <p className="text-xs text-text-muted">
                {ev.timestamp ? formatDateTime(s(ev.timestamp)) : ""}
                {ev.actor ? ` · ${s(ev.actor)}` : ""}
              </p>
              {from && to ? (
                <p className="text-xs text-text-muted">
                  {from} <ArrowRight size={10} className="inline" /> {to}
                </p>
              ) : null}
              {loc ? (
                <p className="flex items-center gap-1 text-xs text-text-muted">
                  <MapPin size={10} /> {loc}
                </p>
              ) : null}
              {ev.telebirrRef ? <p className="font-mono text-xs text-text-muted">{s(ev.telebirrRef)}</p> : null}
              {ev.ipAddress ? (
                <p className="text-xs text-text-muted">
                  {t("transactions.ip")} {s(ev.ipAddress)}
                </p>
              ) : null}
              {ev.detail ? <p className="text-xs text-text-muted">{s(ev.detail)}</p> : null}
            </div>
          </li>
        );
      })}
    </ol>
  );
}
