import type { ReactNode } from "react";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { TrailMap } from "@/components/trail/TrailMap";
import { useLocale } from "@/hooks/useLocale";
import { useDeliveryTrail } from "@/hooks/useQuery";
import { formatDateTime } from "@/lib/i18n";
import {
  fill,
  formatCoords,
  nearestKnownKm,
  summaryLines,
  type DeliveryTrail,
  type TrailLeg,
  type TrailPoint,
  type Tone,
} from "@/lib/trail";

const toneDot: Record<Tone, string> = {
  good: "bg-success",
  warn: "bg-warning",
  bad: "bg-danger",
  info: "bg-info",
};

/** A translation with a fallback when the key is missing (a source the app does not know yet). */
function label(t: (key: string) => string, key: string, fallback: string): string {
  const text = t(key);
  return text === key ? fallback : text;
}

/** A plain message in the tab's card (no trail to draw). */
function Message({ children, role }: { children: ReactNode; role?: "status" | "alert" }) {
  return (
    <Card>
      <p role={role} className="text-sm text-text-muted">
        {children}
      </p>
    </Card>
  );
}

/**
 * LT-13: the Trip trail tab of the dispute page. The trail of the dispute's delivery from pickup to
 * delivery: the map, the check-ins with the leg since the last one, the pauses and gaps, what the
 * trail says, and the reminder that it shows where the truck was, not what it carried.
 */
export function TripTrailTab({ deliveryId }: { deliveryId: string }) {
  const { t } = useLocale();
  const { data, isLoading, isError, error, refetch } = useDeliveryTrail(deliveryId);

  if (!deliveryId) return <Message>{t("trail.noDelivery")}</Message>;
  if (isLoading) return <Message role="status">{t("trail.loading")}</Message>;

  if (isError || !data) {
    const code = (error as { code?: string } | null)?.code;
    return (
      <Card>
        <div role="alert" className="text-sm text-danger">
          {code ? label(t, `errors.${code}`, t("trail.loadFailed")) : t("trail.loadFailed")}
        </div>
        <button
          type="button"
          onClick={() => refetch()}
          className="mt-3 rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50"
        >
          {t("common.retry")}
        </button>
      </Card>
    );
  }

  const trail = data as DeliveryTrail;
  if (!trail.tripId) return <Message>{t("trail.selfPickup")}</Message>;
  if (!trail.from) return <Message>{t("trail.notPickedUp")}</Message>;

  const windowText = trail.open
    ? fill(t("trail.windowOpen"), { from: formatDateTime(trail.from) })
    : fill(t("trail.window"), { from: formatDateTime(trail.from), until: formatDateTime(trail.until ?? trail.asOf) });

  return (
    <div className="flex flex-col gap-6 lg:flex-row lg:items-start">
      <div className="flex min-w-0 flex-col gap-6 lg:flex-[3]">
        <Card>
          <p className="mb-4 text-sm text-text-muted">{windowText}</p>
          <TrailMap trail={trail} />
        </Card>

        <Card>
          <h3 className="mb-3 font-semibold">{t("trail.checkIns")}</h3>
          {trail.points.length === 0 ? (
            <p className="text-sm text-text-muted">{t("trail.noCheckIns")}</p>
          ) : (
            <CheckInsTable trail={trail} />
          )}
        </Card>
      </div>

      <aside className="flex min-w-0 flex-col gap-6 lg:flex-[2]">
        <Card>
          <h3 className="mb-3 font-semibold">{t("trail.says")}</h3>
          <ul className="space-y-2 text-sm">
            {summaryLines(trail).map((line) => (
              <li key={line.key} className="flex gap-2">
                <span aria-hidden="true" className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${toneDot[line.tone]}`} />
                <span>{fill(t(line.key), line.values)}</span>
              </li>
            ))}
          </ul>
          <p className="mt-4 border-t border-gray-200 pt-3 text-xs leading-relaxed text-text-muted">
            {t("trail.reminder")}
          </p>
        </Card>

        <Card>
          <h3 className="mb-2 font-semibold">{t("trail.pauses")}</h3>
          {trail.pauses.length === 0 ? (
            <p className="text-sm text-text-muted">{t("trail.noPauses")}</p>
          ) : (
            <ul className="space-y-1 text-sm">
              {trail.pauses.map((p) => (
                <li key={p.from}>
                  {fill(t("trail.pauseRange"), { from: formatDateTime(p.from), until: formatDateTime(p.until) })}
                </li>
              ))}
            </ul>
          )}
          {(trail.awayStops?.length ?? 0) > 0 && (
            <>
              <h3 className="mb-2 mt-4 font-semibold">{t("trail.awayStops")}</h3>
              <ul className="space-y-1 text-sm">
                {trail.awayStops!.map((stop) => (
                  <li key={stop.from}>
                    {fill(t("trail.awayStopRange"), {
                      from: formatDateTime(stop.from),
                      until: formatDateTime(stop.until),
                      place: formatCoords(stop.latitude, stop.longitude),
                      km: nearestKnownKm(stop) ?? "",
                    })}
                  </li>
                ))}
              </ul>
            </>
          )}
          <h3 className="mb-2 mt-4 font-semibold">{t("trail.gaps")}</h3>
          {trail.gaps.length === 0 ? (
            <p className="text-sm text-text-muted">{t("trail.noGaps")}</p>
          ) : (
            <ul className="space-y-1 text-sm">
              {trail.gaps.map((g) => (
                <li key={g.from}>
                  {fill(t("trail.gapRange"), {
                    from: formatDateTime(g.from),
                    until: formatDateTime(g.until),
                    minutes: g.minutes,
                  })}
                </li>
              ))}
            </ul>
          )}
        </Card>
      </aside>
    </div>
  );
}

/** Every fix and event in capture order, each fix with the leg that ends at it. */
function CheckInsTable({ trail }: { trail: DeliveryTrail }) {
  const { t } = useLocale();
  const legEndingAt = new Map(trail.legs.map((leg) => [leg.toCapturedAt, leg]));
  // The first precise fix starts the legs; a coarse fix is no end of a leg.
  const firstPrecise = trail.points.find((p) => p.kind === "fix" && !p.coarse)?.capturedAt;

  const legText = (point: TrailPoint, leg: TrailLeg | undefined): string => {
    if (point.coarse) return t("trail.legCoarse");
    if (leg) {
      return leg.kmh == null
        ? fill(t("trail.legSameTime"), { km: leg.distanceKm })
        : fill(t("trail.legValue"), { km: leg.distanceKm, minutes: leg.minutes, kmh: leg.kmh });
    }
    return point.capturedAt === firstPrecise ? t("trail.legStart") : "";
  };

  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[620px] text-left text-sm">
        <thead>
          <tr className="border-b border-gray-200 text-xs text-text-muted">
            <th scope="col" className="px-2 py-2 font-medium">{t("trail.colTime")}</th>
            <th scope="col" className="px-2 py-2 font-medium">{t("trail.colPlace")}</th>
            <th scope="col" className="px-2 py-2 font-medium">{t("trail.colLeg")}</th>
            <th scope="col" className="px-2 py-2 font-medium">{t("trail.colNotes")}</th>
          </tr>
        </thead>
        <tbody>
          {trail.points.map((point) => {
            const key = `${point.capturedAt}#${point.kind}`;
            if (point.kind !== "fix") {
              const text =
                point.kind === "paused"
                  ? fill(t("trail.event_paused"), { time: point.pausedUntil ? formatDateTime(point.pausedUntil) : "" })
                  : label(t, `trail.event_${point.kind}`, point.kind);
              return (
                <tr key={key} className="border-b border-gray-100 bg-warning/5">
                  <td className="px-2 py-2 font-medium">{formatDateTime(point.capturedAt)}</td>
                  <td className="px-2 py-2" />
                  <td className="px-2 py-2 text-text-muted" colSpan={2}>{text}</td>
                </tr>
              );
            }

            const leg = legEndingAt.get(point.capturedAt);
            const legCell = legText(point, leg);
            return (
              <tr key={key} className={`border-b border-gray-100 ${leg?.jump ? "bg-danger/5" : ""}`}>
                <td className="px-2 py-2 font-medium">{formatDateTime(point.capturedAt)}</td>
                <td className="px-2 py-2 font-mono text-xs">
                  {point.latitude != null && point.longitude != null ? formatCoords(point.latitude, point.longitude) : ""}
                </td>
                <td className="px-2 py-2">{legCell}</td>
                <td className="px-2 py-2">
                  <div className="flex flex-wrap gap-1">
                    {leg?.jump && <Badge variant="danger">{t("trail.flagJump")}</Badge>}
                    {point.mocked && <Badge variant="danger">{t("trail.flagMocked")}</Badge>}
                    {point.coarse && (
                      <Badge variant="warning">
                        {fill(t("trail.flagCoarse"), { meters: Math.round(point.accuracyM ?? 0).toLocaleString("en-US") })}
                      </Badge>
                    )}
                    {point.late && (
                      <Badge variant="info">{fill(t("trail.flagLate"), { time: formatDateTime(point.receivedAt) })}</Badge>
                    )}
                    {point.source && <Badge>{label(t, `trail.source_${point.source}`, point.source)}</Badge>}
                  </div>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
