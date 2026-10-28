import { describe, expect, it } from "vitest";

import { makeProjection, summaryLines, type DeliveryTrail } from "@/lib/trail";

/* LT-13: the plain map projection of the Trip trail tab and the "what the trail says" lines. */

describe("makeProjection", () => {
  const points = [
    { latitude: 8.0, longitude: 38.7 },
    { latitude: 9.0, longitude: 38.9 },
  ];

  it("fits every point inside the box less its padding, north up", () => {
    const project = makeProjection(points, 760, 320, 28);
    const south = project(points[0]);
    const north = project(points[1]);

    for (const p of [south, north]) {
      expect(p.x).toBeGreaterThanOrEqual(28);
      expect(p.x).toBeLessThanOrEqual(760 - 28);
      expect(p.y).toBeGreaterThanOrEqual(28);
      expect(p.y).toBeLessThanOrEqual(320 - 28);
    }
    expect(north.y).toBeLessThan(south.y);
    expect(north.x).toBeGreaterThan(south.x);
    // The tall span fills the height exactly.
    expect(south.y - north.y).toBeCloseTo(320 - 2 * 28, 6);
  });

  it("keeps the aspect: a degree of longitude is shorter than one of latitude by the cosine", () => {
    const project = makeProjection(
      [
        { latitude: 9.0, longitude: 38.0 },
        { latitude: 10.0, longitude: 39.0 },
      ],
      1000,
      1000,
      0,
    );
    const a = project({ latitude: 9.0, longitude: 38.0 });
    const b = project({ latitude: 10.0, longitude: 39.0 });
    const ratio = Math.abs(b.x - a.x) / Math.abs(b.y - a.y);
    expect(ratio).toBeCloseTo(Math.cos((9.5 * Math.PI) / 180), 6);
  });

  it("centres a single point", () => {
    const project = makeProjection([{ latitude: 8.5, longitude: 38.8 }], 760, 320, 28);
    const p = project({ latitude: 8.5, longitude: 38.8 });
    expect(p.x).toBeCloseTo(380, 6);
    expect(p.y).toBeCloseTo(160, 6);
  });
});

function trail(summary: Partial<DeliveryTrail["summary"]>, legs = 1, pauses = 0): DeliveryTrail {
  const leg = { fromCapturedAt: "a", toCapturedAt: "b", distanceKm: 10, minutes: 30, kmh: 20, jump: false };
  return {
    deliveryId: "del-1",
    tripId: "trip-1",
    status: "delivered",
    open: false,
    from: "2026-10-27T05:05:00.000Z",
    until: "2026-10-27T08:00:00.000Z",
    asOf: "2026-10-27T09:00:00.000Z",
    pickup: { latitude: null, longitude: null, at: null },
    dropoff: { latitude: null, longitude: null, at: null },
    points: [],
    legs: Array.from({ length: legs }, () => leg),
    pauses: Array.from({ length: pauses }, () => ({ from: "a", until: "b" })),
    gaps: [],
    summary: {
      sharedWholeWay: true,
      declined: false,
      fixes: 3,
      coarseFixes: 0,
      mockedFixes: 0,
      lateFixes: 0,
      jumps: 0,
      gaps: 0,
      distanceKm: 20,
      durationMin: 175,
      maxKmh: 25.4,
      ...summary,
    },
  };
}

const keys = (t: DeliveryTrail) => summaryLines(t).map((l) => `${l.tone}:${l.key}`);

describe("summaryLines", () => {
  it("a clean trail: shared the whole way, no jumps, no mock locations, the distance", () => {
    expect(keys(trail({}))).toEqual([
      "good:trail.saysSharedWholeWay",
      "good:trail.saysNoJumps",
      "good:trail.saysNoMocked",
      "info:trail.saysDistance",
    ]);
    expect(summaryLines(trail({}))[1].values).toEqual({ kmh: 25.4 });
  });

  it("gaps, jumps, mock locations and the notes", () => {
    const lines = keys(
      trail({ sharedWholeWay: false, gaps: 2, jumps: 1, mockedFixes: 2, coarseFixes: 1, lateFixes: 3 }, 2, 1),
    );
    expect(lines).toEqual([
      "warn:trail.saysGaps",
      "bad:trail.saysJumps",
      "bad:trail.saysMocked",
      "info:trail.saysCoarse",
      "info:trail.saysLate",
      "info:trail.saysPauses",
      "info:trail.saysDistance",
    ]);
  });

  it("a decline wins over gaps; one precise fix cannot measure speed", () => {
    expect(keys(trail({ declined: true, sharedWholeWay: false, gaps: 1, maxKmh: null }, 0))).toEqual([
      "warn:trail.saysDeclined",
      "info:trail.saysNoLegs",
      "good:trail.saysNoMocked",
      "info:trail.saysDistance",
    ]);
  });

  it("no check-ins at all says only that", () => {
    expect(keys(trail({ fixes: 0, sharedWholeWay: false, maxKmh: null }, 0))).toEqual(["warn:trail.saysNoCheckIns"]);
  });
});
