import type { ReactNode } from "react";
import { useLocale } from "@/hooks/useLocale";
import { formatTime } from "@/lib/i18n";
import {
  fill,
  makeProjection,
  mapFixes,
  stopPoint,
  type DeliveryTrail,
  type LatLng,
} from "@/lib/trail";

const WIDTH = 760;
const HEIGHT = 320;
const PADDING = 28;

/**
 * LT-13: the trail drawn as plain SVG (no tiles, no map package): the legs between precise
 * check-ins (an impossible jump dashed in red), every check-in (a coarse one as a dashed ring, a
 * mock location in red), and the pickup and drop-off QR scan points. The check-ins table carries the
 * same facts as text.
 */
export function TrailMap({ trail }: { trail: DeliveryTrail }) {
  const { t } = useLocale();
  const fixes = mapFixes(trail);
  const pickup = stopPoint(trail.pickup);
  const dropoff = stopPoint(trail.dropoff);
  const all: LatLng[] = [...fixes, ...(pickup ? [pickup] : []), ...(dropoff ? [dropoff] : [])];
  if (all.length === 0) return null;

  const project = makeProjection(all, WIDTH, HEIGHT, PADDING);
  const byTime = new Map(fixes.map((f) => [f.capturedAt, project(f)]));

  const legend: { label: string; swatch: ReactNode }[] = [
    { label: t("trail.legendPickup"), swatch: <circle cx="8" cy="8" r="5" className="fill-white stroke-primary" strokeWidth="3" /> },
    { label: t("trail.legendDropoff"), swatch: <circle cx="8" cy="8" r="6" className="fill-info" /> },
    { label: t("trail.legendFix"), swatch: <circle cx="8" cy="8" r="4" className="fill-white stroke-primary" strokeWidth="2.5" /> },
    { label: t("trail.legendCoarse"), swatch: <circle cx="8" cy="8" r="5" className="fill-none stroke-text-muted" strokeWidth="1.5" strokeDasharray="3 2" /> },
    { label: t("trail.legendJump"), swatch: <line x1="1" y1="8" x2="15" y2="8" className="stroke-danger" strokeWidth="3" strokeDasharray="4 2" /> },
    { label: t("trail.legendMocked"), swatch: <circle cx="8" cy="8" r="4" className="fill-white stroke-danger" strokeWidth="2.5" /> },
  ];

  return (
    <figure className="m-0">
      <svg
        width="100%"
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        preserveAspectRatio="xMidYMid meet"
        role="img"
        aria-label={fill(t("trail.mapLabel"), { fixes: trail.summary.fixes })}
        className="block rounded-lg bg-cream"
      >
        {trail.legs.map((leg) => {
          const a = byTime.get(leg.fromCapturedAt);
          const b = byTime.get(leg.toCapturedAt);
          if (!a || !b) return null;
          return (
            <line
              key={`${leg.fromCapturedAt}-${leg.toCapturedAt}`}
              data-testid={leg.jump ? "trail-jump" : "trail-leg"}
              x1={a.x}
              y1={a.y}
              x2={b.x}
              y2={b.y}
              className={leg.jump ? "stroke-danger" : "stroke-primary"}
              strokeWidth={leg.jump ? 3 : 4}
              strokeDasharray={leg.jump ? "8 5" : undefined}
              strokeLinecap="round"
            />
          );
        })}

        {fixes.map((f) => {
          const p = byTime.get(f.capturedAt)!;
          if (f.coarse) {
            return (
              <circle
                key={f.capturedAt}
                cx={p.x}
                cy={p.y}
                r={7}
                className="fill-none stroke-text-muted"
                strokeWidth={1.5}
                strokeDasharray="3 3"
              />
            );
          }
          return (
            <circle
              key={f.capturedAt}
              cx={p.x}
              cy={p.y}
              r={5}
              className={f.mocked ? "fill-white stroke-danger" : "fill-white stroke-primary"}
              strokeWidth={2.5}
            />
          );
        })}

        {/* Admin trip evidence board: each point with its time, a stop off the stop list ringed. */}
        {(trail.awayStops ?? []).map((stop) => {
          const p = project({ latitude: stop.latitude, longitude: stop.longitude });
          return (
            <circle
              key={`away-${stop.from}`}
              data-testid="trail-away-stop"
              cx={p.x}
              cy={p.y}
              r={16}
              className="fill-accent/20 stroke-accent"
              strokeWidth={2}
            />
          );
        })}
        {fixes.map((f) => {
          const p = byTime.get(f.capturedAt)!;
          return (
            <text key={`t-${f.capturedAt}`} x={p.x + 10} y={p.y + 18} className="fill-text-muted text-[11px]">
              {formatTime(f.capturedAt)}
            </text>
          );
        })}

        {pickup && (() => {
          const p = project(pickup);
          return <circle cx={p.x} cy={p.y} r={9} className="fill-white stroke-primary" strokeWidth={4} />;
        })()}
        {dropoff && (() => {
          const p = project(dropoff);
          return (
            <g>
              <circle cx={p.x} cy={p.y} r={10} className="fill-info" />
              <circle cx={p.x} cy={p.y} r={4} className="fill-white" />
            </g>
          );
        })()}
      </svg>
      <figcaption className="mt-3 flex flex-wrap gap-x-4 gap-y-2 text-xs text-text-muted">
        {legend.map((item) => (
          <span key={item.label} className="inline-flex items-center gap-1.5">
            <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
              {item.swatch}
            </svg>
            {item.label}
          </span>
        ))}
      </figcaption>
    </figure>
  );
}
