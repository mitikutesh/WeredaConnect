import { describe, expect, it } from "vitest";

import { noFixCause, partiesOf, photosOf, sentLate, type DisputeEvidence } from "@/lib/evidence";

/* FRD-14: who is who on a dispute, and which handoff photos it has. */

function evidence(overrides: Partial<DisputeEvidence> = {}): DisputeEvidence {
  return {
    escrow: { buyerId: "b", buyerName: "Sara", farmerId: "f", farmerName: "Abebe" },
    order: { buyerId: "b", farmerId: "f", truckerId: "t" },
    delivery: { buyerId: "b", farmerId: "f", truckerId: "t", truckerName: "Dawit", farmerName: "  " },
    timeline: [],
    handoffs: [],
    ...overrides,
  };
}

describe("partiesOf", () => {
  it("names each party by role from the first source that has a name", () => {
    const parties = partiesOf(evidence());
    expect(parties.get("b")).toEqual({ role: "buyer", name: "Sara" });
    // The delivery's blank name is skipped for the escrow's.
    expect(parties.get("f")).toEqual({ role: "farmer", name: "Abebe" });
    expect(parties.get("t")).toEqual({ role: "trucker", name: "Dawit" });
  });

  it("is empty without evidence, and leaves out a party with no id", () => {
    expect(partiesOf(undefined).size).toBe(0);
    const parties = partiesOf(evidence({ order: undefined, delivery: undefined }));
    expect([...parties.keys()]).toEqual(["b", "f"]);
  });
});

describe("photosOf", () => {
  it("lists the photos the delivery carries, pickup first", () => {
    const photos = photosOf(
      evidence({ delivery: { dropoffPhotoUrl: "https://s/d.jpg", pickupPhotoUrl: "https://s/p.webp" } }),
    );
    expect(photos).toEqual([
      { leg: "pickup", url: "https://s/p.webp" },
      { leg: "dropoff", url: "https://s/d.jpg" },
    ]);
  });

  it("adds the trucker's arrival photo when the delivery has one (ESC-36)", () => {
    const photos = photosOf(
      evidence({ delivery: { pickupPhotoUrl: "https://s/p.webp", arrivalPhotoUrl: "https://s/a.webp" } }),
    );
    expect(photos).toEqual([
      { leg: "pickup", url: "https://s/p.webp" },
      { leg: "arrival", url: "https://s/a.webp" },
    ]);
  });

  it("has none without a delivery or a URL", () => {
    expect(photosOf(evidence({ delivery: undefined }))).toEqual([]);
    expect(photosOf(evidence({ delivery: { pickupPhotoUrl: "" } }))).toEqual([]);
  });
});

/* FUP-440: why a handoff had no fix, and a handoff made offline and sent later. */
describe("noFixCause", () => {
  it("is the stored cause of a handoff without a fix", () => {
    for (const cause of ["services_off", "denied", "denied_forever", "timeout"]) {
      expect(noFixCause({ leg: "pickup", gpsStatus: cause })).toBe(cause);
    }
  });

  it("is nothing for a fix, an approximate fix, an unknown value or an older delivery", () => {
    expect(noFixCause({ leg: "pickup", gpsStatus: "fix" })).toBeUndefined();
    expect(noFixCause({ leg: "pickup", gpsStatus: "approximate" })).toBeUndefined();
    expect(noFixCause({ leg: "pickup", gpsStatus: "gps_off" })).toBeUndefined();
    expect(noFixCause({ leg: "pickup" })).toBeUndefined();
    expect(noFixCause(undefined)).toBeUndefined();
  });
});

describe("sentLate", () => {
  const at = "2026-11-26T10:00:00Z";

  it("is true when the phone made the handoff more than 15 minutes before the server got it", () => {
    expect(sentLate({ leg: "dropoff", at, capturedAt: "2026-11-26T09:44:59Z" })).toBe(true);
  });

  it("ignores an old fix time: a phone may hand back a cached fix while online", () => {
    expect(sentLate({ leg: "pickup", at, capturedAt: "2026-11-26T09:59:00Z", gpsCapturedAt: "2026-11-26T08:00:00Z" })).toBe(false);
  });

  it("is false within 15 minutes, without a phone time, or without the server's time", () => {
    expect(sentLate({ leg: "dropoff", at, capturedAt: "2026-11-26T09:45:00Z" })).toBe(false);
    expect(sentLate({ leg: "dropoff", at })).toBe(false);
    expect(sentLate({ leg: "dropoff", capturedAt: "2026-11-26T08:00:00Z" })).toBe(false);
    expect(sentLate({ leg: "dropoff", at, capturedAt: "not a time" })).toBe(false);
  });
});
