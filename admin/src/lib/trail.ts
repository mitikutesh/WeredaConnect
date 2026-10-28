/**
 * LT-13: the admin delivery trail (GET /admin/deliveries/{id}/trail, live-trip-api.md section 3),
 * the plain map projection the Trip trail tab draws with, and the "what the trail says" lines. No
 * tiles and no map package: an equirectangular projection fitted to the points, like the mobile map
 * (live-trip.md section 8).
 */

export interface TrailStop {
  latitude: number | null;
  longitude: number | null;
  at: string | null;
}

export interface TrailPoint {
  capturedAt: string;
  receivedAt: string;
  /** fix, paused, resumed or declined */
  kind: string;
  source: string | null;
  latitude: number | null;
  longitude: number | null;
  accuracyM: number | null;
  mocked: boolean | null;
  pausedUntil: string | null;
  coarse: boolean;
  late: boolean;
}

export interface TrailLeg {
  fromCapturedAt: string;
  toCapturedAt: string;
  distanceKm: number;
  minutes: number;
  kmh: number | null;
  jump: boolean;
}

export interface TrailSpan {
  from: string;
  until: string;
}

export interface TrailGap extends TrailSpan {
  minutes: number;
}

export interface TrailSummary {
  sharedWholeWay: boolean;
  declined: boolean;
  fixes: number;
  coarseFixes: number;
  mockedFixes: number;
  lateFixes: number;
  jumps: number;
  gaps: number;
  distanceKm: number;
  durationMin: number;
  maxKmh: number | null;
}

export interface DeliveryTrail {
  deliveryId: string;
  /** null for a self-pickup: no truck, no trail */
  tripId: string | null;
  status: string;
  open: boolean;
  /** pickedUpAt; null before pickup */
  from: string | null;
  /** when the delivery left the road; null while open */
  until: string | null;
  asOf: string;
  pickup: TrailStop;
  dropoff: TrailStop;
  points: TrailPoint[];
  legs: TrailLeg[];
  pauses: TrailSpan[];
  gaps: TrailGap[];
  summary: TrailSummary;
}

export interface LatLng {
  latitude: number;
  longitude: number;
}

/** Fills {name} placeholders. */
export function fill(template: string, values: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => (k in values ? String(values[k]) : ""));
}

/** "8.7523, 38.9785": the stored precision (about 11 m). */
export function formatCoords(latitude: number, longitude: number): string {
  return `${latitude.toFixed(4)}, ${longitude.toFixed(4)}`;
}

/** The fixes with a position, in capture order. */
export function mapFixes(trail: DeliveryTrail): (TrailPoint & LatLng)[] {
  return trail.points.filter(
    (p): p is TrailPoint & LatLng => p.kind === "fix" && p.latitude != null && p.longitude != null,
  );
}

/** A QR scan point with both coordinates, else null. */
export function stopPoint(stop: TrailStop): LatLng | null {
  return stop.latitude != null && stop.longitude != null
    ? { latitude: stop.latitude, longitude: stop.longitude }
    : null;
}

/** The smallest span the projection fits (about 1 km), so one point or a tight cluster stays readable. */
const MIN_SPAN_DEGREES = 0.01;

/**
 * An equirectangular projection fitted into a width x height box with padding, centred, keeping the
 * aspect: longitude is scaled by the cosine of the middle latitude, latitude grows upwards.
 */
export function makeProjection(
  points: LatLng[],
  width: number,
  height: number,
  padding: number,
): (p: LatLng) => { x: number; y: number } {
  if (points.length === 0) {
    return () => ({ x: width / 2, y: height / 2 });
  }

  const lats = points.map((p) => p.latitude);
  const lngs = points.map((p) => p.longitude);
  const minLat = Math.min(...lats);
  const maxLat = Math.max(...lats);
  const minLng = Math.min(...lngs);
  const maxLng = Math.max(...lngs);
  const kx = Math.cos((((minLat + maxLat) / 2) * Math.PI) / 180);

  const spanX = Math.max((maxLng - minLng) * kx, MIN_SPAN_DEGREES);
  const spanY = Math.max(maxLat - minLat, MIN_SPAN_DEGREES);
  const innerW = width - 2 * padding;
  const innerH = height - 2 * padding;
  const scale = Math.min(innerW / spanX, innerH / spanY);
  // Centre the drawing: the box is fitted on one axis and has room on the other.
  const offsetX = padding + (innerW - (maxLng - minLng) * kx * scale) / 2;
  const offsetY = padding + (innerH - (maxLat - minLat) * scale) / 2;

  return (p) => ({
    x: offsetX + (p.longitude - minLng) * kx * scale,
    y: offsetY + (maxLat - p.latitude) * scale,
  });
}

export type Tone = "good" | "warn" | "bad" | "info";

export interface SummaryLine {
  tone: Tone;
  /** an i18n key under trail. */
  key: string;
  values: Record<string, string | number>;
}

/**
 * "What the trail says", in the order the panel shows it: sharing (whole way, gaps, declined, none),
 * speed (jumps, or the fastest leg), mock locations, then the notes (coarse, late, pauses, distance).
 */
export function summaryLines(trail: DeliveryTrail): SummaryLine[] {
  const s = trail.summary;
  const lines: SummaryLine[] = [];

  if (s.fixes === 0) {
    lines.push({ tone: "warn", key: "trail.saysNoCheckIns", values: {} });
  } else if (s.declined) {
    lines.push({ tone: "warn", key: "trail.saysDeclined", values: { fixes: s.fixes } });
  } else if (s.sharedWholeWay) {
    lines.push({ tone: "good", key: "trail.saysSharedWholeWay", values: { fixes: s.fixes } });
  } else {
    lines.push({ tone: "warn", key: "trail.saysGaps", values: { gaps: s.gaps } });
  }

  if (s.fixes > 0) {
    if (s.jumps > 0) {
      lines.push({ tone: "bad", key: "trail.saysJumps", values: { jumps: s.jumps } });
    } else if (trail.legs.length > 0 && s.maxKmh != null) {
      lines.push({ tone: "good", key: "trail.saysNoJumps", values: { kmh: s.maxKmh } });
    } else {
      lines.push({ tone: "info", key: "trail.saysNoLegs", values: {} });
    }

    lines.push(
      s.mockedFixes > 0
        ? { tone: "bad", key: "trail.saysMocked", values: { count: s.mockedFixes } }
        : { tone: "good", key: "trail.saysNoMocked", values: {} },
    );
  }

  if (s.coarseFixes > 0) {
    lines.push({ tone: "info", key: "trail.saysCoarse", values: { count: s.coarseFixes } });
  }
  if (s.lateFixes > 0) {
    lines.push({ tone: "info", key: "trail.saysLate", values: { count: s.lateFixes } });
  }
  if (trail.pauses.length > 0) {
    lines.push({ tone: "info", key: "trail.saysPauses", values: { count: trail.pauses.length } });
  }
  if (s.fixes > 0) {
    lines.push({
      tone: "info",
      key: "trail.saysDistance",
      values: { km: s.distanceKm, minutes: s.durationMin },
    });
  }

  return lines;
}
