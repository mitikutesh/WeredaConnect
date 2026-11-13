import { test, expect } from "@playwright/test";
import { TEST_USERS, apiLogin, API_URL, seedAuth } from "../helpers/auth";
import { uploadCropPhotos } from "../helpers/crop-photos";

/**
 * Flutter Web App - Role-Based Visibility / Isolation Tests
 *
 * CRITICAL BUSINESS RULES (from CLAUDE.md):
 * - Farmer: sees "My Crops" nav, can create crop listings, sees OWN crops ONLY
 * - Trucker: sees "My Trips" nav, can create trip listings, sees OWN trips ONLY
 * - Buyer:  sees "Find Crops" nav, can browse ALL crops + ALL trips
 *
 * Farmer MUST NOT see other farmers' crops.
 * Trucker MUST NOT see other truckers' trips.
 * Buyer MUST see ALL crops and ALL trips from everyone.
 *
 * These tests verify role isolation at TWO levels:
 * 1. API level: server enforces data filtering per role/user
 * 2. UI level: navigation differs by role (verified via screenshots + semantics)
 *
 * API level (FUP-10), against API_URL (the dev API by default):
 * - The farmer makes one crop listing and the trucker one trip through POST /sync.
 *   /sync answers HTTP 200 for a refused operation too, so each create must come
 *   back with per-operation status `success` and a serverVersion
 *   (sync-protocol.md), and is then read back by id.
 * - Every read is held to the role rules (crop-listing-api.md,
 *   trip-listing-api.md): the farmer's crop reads hold only the farmer's own
 *   listings, the trucker's trip reads only the trucker's own trips, another
 *   owner's list or row answers 403, and the buyer's browse lists the crop and
 *   the trip made in this run.
 * - The last test deletes the crop (a crop_listing `delete`) and cancels the trip
 *   (an `update` to `cancelled`, the one cancel path of trip-listing-api.md
 *   2.4.0), each with the version the server holds, asserts that both were
 *   applied, and reads both back as gone (404) for the owner and for the buyer.
 *   afterAll repeats that cleanup when a failure stopped the run before it, and
 *   fails the run when it cannot.
 * - A login that the API refuses fails the run. Only an API that cannot be
 *   reached at all (DNS, connection refused, timeout) skips these tests, and the
 *   skip names the URL. A crop create that the fraud rules throttle, or a 429,
 *   skips only the checks that need the new rows.
 *
 * Writes per run: one crop listing with 2 photos and one trip, both removed by
 * the cleanup. The listing takes a crop type the farmer has not listed in the
 * last hour and the market's median price for it, so the shared test farmer is
 * not refused or scored by the fraud duplicate and price rules.
 *
 * UI tests require a running web app built for a non-prod environment.
 */

/** Wait for Flutter to fully render. */
async function waitForFlutter(page: import("@playwright/test").Page) {
  await page.waitForSelector("flutter-view", { timeout: 30_000 });
  await page.waitForTimeout(3000);
}

type Role = keyof typeof TEST_USERS;

/** A logged-in test user: the ID token (it carries custom:role) and the Cognito sub. */
interface Session {
  role: Role;
  token: string;
  userId: string;
}

/** The read model fields this spec uses (GET /crops, GET /crops/{id}). */
interface CropRow {
  cropId: string;
  farmerId: string;
  cropType?: string;
  pricePerKg?: number;
  status?: string;
  version?: number;
  createdAt?: string;
}

/** The fields this spec uses (GET /trips, GET /trips/{id}). */
interface TripRow {
  tripId: string;
  truckerId: string;
  status?: string;
  version?: number;
}

/** One page of GET /crops or GET /trips. */
interface ListPage {
  crops?: CropRow[];
  trips?: TripRow[];
  nextToken?: string | null;
  hasMore?: boolean;
}

interface SyncOp {
  entityType: "crop_listing" | "trip";
  action: "create" | "update" | "delete";
  entityId: string;
  /** The version the server holds (op level, sync-protocol.md). */
  clientVersion?: number;
  data: Record<string, unknown>;
}

interface SyncOpResult {
  operationId?: string;
  status?: string;
  serverVersion?: number;
  message?: string;
}

const DAY_MS = 24 * 60 * 60 * 1000;

/** Upper bound on the pages one list read follows (50 rows a page). */
const MAX_PAGES = 50;

/** The crop types the server accepts (SyncFunction AllowedCropTypes). */
const CROP_TYPES = [
  "teff",
  "wheat",
  "barley",
  "maize",
  "chickpea",
  "lentils",
  "sorghum",
  "coffee",
  "sesame",
];

/**
 * FraudDetectionService refuses a farmer's 6th create in a UTC hour, and a
 * create with a live listing of the same type at a price within 5% from the
 * last hour (5 minutes of slack for clock skew). Both refusals score the user.
 */
const MAX_LISTINGS_PER_HOUR = 5;
const DUPLICATE_WINDOW_MS = 65 * 60 * 1000;
const FRAUD_THROTTLE_CODES = [
  "FRAUD_VELOCITY_EXCEEDED",
  "FRAUD_DUPLICATE_LISTING",
];

/**
 * The network error behind a fetch that got no answer (DNS, refused, reset,
 * timeout): Node's fetch throws a TypeError carrying the cause. Null for any
 * other error (a TypeError without a cause is a bug, not the network).
 */
function unreachableCause(err: unknown): string | null {
  if (!(err instanceof TypeError)) return null;
  const cause = (err as TypeError & { cause?: { code?: string; message?: string } })
    .cause;
  if (!cause) return null;
  return cause.code ?? cause.message ?? "network error";
}

/**
 * Log `role` in through POST /auth/login. A refused or broken login fails with
 * the API's answer. Only an API that cannot be reached at all skips, and the
 * skip names the URL and the network error.
 */
async function login(role: Role): Promise<Session> {
  const user = TEST_USERS[role];
  let idToken: string | undefined;
  try {
    idToken = (await apiLogin(user.phone, user.password))?.idToken;
  } catch (err) {
    const cause = unreachableCause(err);
    test.skip(
      cause !== null,
      `API unreachable at ${API_URL} (${cause}): the ${role} login got no answer`,
    );
    throw new Error(
      `The ${role} test user (${user.phone}) could not log in at ${API_URL}: ${(err as Error).message}`,
    );
  }
  if (!idToken) {
    throw new Error(`The ${role} login at ${API_URL} returned no idToken`);
  }
  const claims = JSON.parse(
    Buffer.from(idToken.split(".")[1], "base64url").toString(),
  ) as { sub?: string; "custom:role"?: string };
  // The server's role rules key on custom:role, compared ignoring case (dev
  // issues "Farmer"): a test user with another role proves nothing.
  expect(
    claims["custom:role"]?.toLowerCase(),
    `${role} test user's custom:role claim`,
  ).toBe(role);
  expect(claims.sub, `${role} test user's sub claim`).toBeTruthy();
  return { role, token: idToken, userId: claims.sub as string };
}

async function apiGet(session: Session | null, path: string): Promise<Response> {
  return fetch(`${API_URL}${path}`, {
    headers: session ? { Authorization: `Bearer ${session.token}` } : undefined,
  });
}

/** The HTTP status of GET `path` as `session` (null: no token). */
async function statusOf(session: Session | null, path: string): Promise<number> {
  const res = await apiGet(session, path);
  await res.text();
  return res.status;
}

/** GET `path` as `session`; fails with the answer's body unless it is 200. */
async function getJson<T>(session: Session, path: string): Promise<T> {
  const res = await apiGet(session, path);
  if (res.status !== 200) {
    throw new Error(
      `GET ${path} as ${session.role} answered ${res.status}: ${await res.text()}`,
    );
  }
  return (await res.json()) as T;
}

/**
 * Read a list endpoint page by page as `session`, checking on every page that
 * `hasMore` agrees with `nextToken` (crop-listing-api 1.9.0, trip-listing-api
 * 2.5.0). Stops early once `until` holds for the rows read so far.
 */
async function readList<T>(
  session: Session,
  path: "/crops" | "/trips",
  params: Record<string, string>,
  until?: (rows: T[]) => boolean,
): Promise<T[]> {
  const key = path === "/crops" ? "crops" : "trips";
  const rows: T[] = [];
  let nextToken: string | undefined;
  let pages = 0;
  do {
    const qs = new URLSearchParams({ ...params, limit: "50" });
    if (nextToken) qs.set("nextToken", nextToken);
    const page = await getJson<ListPage>(session, `${path}?${qs}`);
    const pageRows = page[key];
    if (!Array.isArray(pageRows)) {
      throw new Error(`GET ${path}?${qs} as ${session.role} has no "${key}" array`);
    }
    expect(
      page.hasMore,
      `GET ${path} as ${session.role}: hasMore must agree with nextToken`,
    ).toBe(Boolean(page.nextToken));
    rows.push(...(pageRows as unknown as T[]));
    if (until?.(rows)) break;
    nextToken = page.nextToken ?? undefined;
    pages++;
  } while (nextToken && pages < MAX_PAGES);
  return rows;
}

/** Poll GET `path` as `session` until it answers `status` (reads lag a write briefly). */
async function expectStatus(
  session: Session,
  path: string,
  status: number,
): Promise<void> {
  await expect
    .poll(() => statusOf(session, path), {
      message: `GET ${path} as ${session.role}`,
      timeout: 20_000,
    })
    .toBe(status);
}

/**
 * The version of the row at `path` (GET /crops/{id} or /trips/{id}) read as
 * its owner, or null when there is none: the create never stored it, or it is
 * already deleted or cancelled.
 */
async function storedVersion(owner: Session, path: string): Promise<number | null> {
  // A few tries: a create that did commit can lag a read made right after it.
  for (let attempt = 0; attempt < 3; attempt++) {
    const res = await apiGet(owner, path);
    if (res.status === 200) {
      const body = (await res.json()) as { crop?: CropRow; trip?: TripRow };
      const version = (body.crop ?? body.trip)?.version;
      if (typeof version !== "number") {
        throw new Error(`GET ${path} as ${owner.role} returned no version`);
      }
      return version;
    }
    const text = await res.text();
    if (res.status !== 404) {
      throw new Error(`GET ${path} as ${owner.role} answered ${res.status}: ${text}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 1500));
  }
  return null;
}

/**
 * POST /sync with one operation as `session` and return that operation's
 * result, or "rate-limited" on a 429 (nothing was processed). The HTTP status
 * says nothing about the operation: callers read its `status`.
 */
async function syncOne(
  session: Session,
  op: SyncOp,
): Promise<SyncOpResult | "rate-limited"> {
  const operationId = crypto.randomUUID();
  const now = new Date();
  const res = await fetch(`${API_URL}/sync`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${session.token}`,
    },
    body: JSON.stringify({
      syncBatchId: crypto.randomUUID(),
      clientId: "e2e-role-isolation",
      operations: [
        {
          ...op,
          operationId,
          // sync-protocol.md: {userId}:{entityType}:{entityId}:{action}:{timestamp}
          idempotencyKey: `${session.userId}:${op.entityType}:${op.entityId}:${op.action}:${now.getTime()}`,
          timestamp: now.toISOString(),
        },
      ],
    }),
  });
  const body = await res.text();
  if (res.status === 429) return "rate-limited";
  const what = `POST /sync ${op.entityType} ${op.action} ${op.entityId} as ${session.role}`;
  if (!res.ok) throw new Error(`${what} answered ${res.status}: ${body}`);
  const results = (JSON.parse(body) as { results?: SyncOpResult[] }).results;
  const result = results?.find((r) => r.operationId === operationId);
  if (results?.length !== 1 || !result) {
    throw new Error(`${what}: no single result for the operation: ${body}`);
  }
  return result;
}

/** Assert that the operation was applied; returns the version the server now holds. */
function expectApplied(result: SyncOpResult, what: string): number {
  expect(
    result.status,
    `${what}: per-operation status (message: ${result.message ?? "none"})`,
  ).toBe("success");
  expect(typeof result.serverVersion, `${what}: serverVersion`).toBe("number");
  return result.serverVersion as number;
}

/**
 * Median price of the buyer's newest available listings of `cropType`, so the
 * new listing sits inside the server's price band (FraudDetectionService flags
 * a price outside it as PRICE_ANOMALY and scores the farmer). 50 ETB/kg when
 * the market has none.
 */
async function marketPricePerKg(buyer: Session, cropType: string): Promise<number> {
  const qs = new URLSearchParams({ cropType, status: "available", limit: "50" });
  const page = await getJson<ListPage>(buyer, `/crops?${qs}`);
  const prices = (page.crops ?? [])
    .map((c) => c.pricePerKg)
    .filter((p): p is number => typeof p === "number" && p > 0)
    .sort((a, b) => a - b);
  if (prices.length === 0) return 50;
  const mid = Math.floor(prices.length / 2);
  const median =
    prices.length % 2 === 1 ? prices[mid] : (prices[mid - 1] + prices[mid]) / 2;
  return Math.min(1000, Math.max(0.01, Math.round(median * 100) / 100));
}

// ─────────────────────────────────────────────────────────────────────────────
// API-Level Role Isolation Tests
// ─────────────────────────────────────────────────────────────────────────────

test.describe("Role Isolation - API Level", () => {
  // One worker, in order: the tests share the rows this run makes. A failure
  // skips the tests after it; afterAll still cleans up.
  test.describe.configure({ mode: "serial", timeout: 120_000 });

  let farmer: Session;
  let trucker: Session;
  let buyer: Session;

  /**
   * The rows this run makes. The id is set before the create goes out, so the
   * cleanup also finds a row whose create answer was lost; `version` stays null
   * until the create was seen applied.
   */
  const crop = { id: null as string | null, version: null as number | null, removed: false };
  const trip = { id: null as string | null, version: null as number | null, removed: false };

  test.beforeAll(async () => {
    farmer = await login("farmer");
    trucker = await login("trucker");
    buyer = await login("buyer");
  });

  /**
   * Delete the run's crop listing with the version the server holds, then read
   * it back as gone. `readBack` ignores the version the create returned and
   * reads the stored one (afterAll: an earlier attempt may have moved it).
   */
  async function removeCrop(readBack = false): Promise<void> {
    if (!crop.id || crop.removed) return;
    const path = `/crops/${crop.id}`;
    const version =
      (readBack ? null : crop.version) ?? (await storedVersion(farmer, path));
    if (version === null) {
      crop.removed = true; // never stored
      return;
    }
    const result = await syncOne(farmer, {
      entityType: "crop_listing",
      action: "delete",
      entityId: crop.id,
      clientVersion: version,
      data: { farmerId: farmer.userId },
    });
    if (result === "rate-limited") {
      throw new Error(`crop listing ${crop.id} was not deleted: POST /sync answered 429`);
    }
    expect(
      expectApplied(result, `crop listing ${crop.id} delete with version ${version}`),
      "the deleted listing's version",
    ).toBe(version + 1);
    await expectStatus(farmer, path, 404);
    await expectStatus(buyer, path, 404);
    crop.removed = true;
  }

  /** Cancel the run's trip like {@link removeCrop} deletes the listing. */
  async function removeTrip(readBack = false): Promise<void> {
    if (!trip.id || trip.removed) return;
    const path = `/trips/${trip.id}`;
    const version =
      (readBack ? null : trip.version) ?? (await storedVersion(trucker, path));
    if (version === null) {
      trip.removed = true; // never stored
      return;
    }
    // trip-listing-api 2.4.0: mobile and web cancel with this update; a
    // cancelled trip is left out of every read, like a deleted one.
    const result = await syncOne(trucker, {
      entityType: "trip",
      action: "update",
      entityId: trip.id,
      clientVersion: version,
      data: { status: "cancelled", truckerId: trucker.userId },
    });
    if (result === "rate-limited") {
      throw new Error(`trip ${trip.id} was not cancelled: POST /sync answered 429`);
    }
    expect(
      expectApplied(result, `trip ${trip.id} cancel with version ${version}`),
      "the cancelled trip's version",
    ).toBe(version + 1);
    await expectStatus(trucker, path, 404);
    await expectStatus(buyer, path, 404);
    trip.removed = true;
  }

  test.afterAll(async () => {
    // Does the work only when a failure stopped the run before the cleanup
    // test. A row left behind fails the run: it leaks into the buyer's browse.
    const failures: string[] = [];
    for (const [what, remove] of [
      ["crop listing", removeCrop],
      ["trip", removeTrip],
    ] as const) {
      try {
        await remove(true);
      } catch (err) {
        failures.push(`${what}: ${(err as Error).message}`);
      }
    }
    if (failures.length > 0) {
      throw new Error(`Cleanup left rows on ${API_URL}:\n${failures.join("\n")}`);
    }
  });

  test("farmer creates a crop listing: the operation is applied and reads back", async () => {
    // The farmer's own list, every status, for the two fraud guards.
    const own = await readList<CropRow>(farmer, "/crops", {});
    const now = Date.now();
    const hourStart = now - (now % (60 * 60 * 1000));
    const thisHour = own.filter((c) => Date.parse(c.createdAt ?? "") >= hourStart).length;
    test.skip(
      thisHour >= MAX_LISTINGS_PER_HOUR,
      `The farmer made ${thisHour} listings this hour: FRAUD_VELOCITY_EXCEEDED would refuse the create and score the shared test user`,
    );
    const recent = own.filter(
      (c) => Date.parse(c.createdAt ?? "") >= now - DUPLICATE_WINDOW_MS,
    );
    const cropType = CROP_TYPES.find((t) => !recent.some((c) => c.cropType === t));
    if (!cropType) {
      test.skip(true, "The farmer listed every crop type in the last hour: FRAUD_DUPLICATE_LISTING could refuse the create");
      return;
    }
    const pricePerKg = await marketPricePerKg(buyer, cropType);

    // A UUID (the photo endpoint takes nothing else), recorded before any request.
    const id = crypto.randomUUID();
    crop.id = id;
    const photoIds = await uploadCropPhotos(farmer.token, id);
    const result = await syncOne(farmer, {
      entityType: "crop_listing",
      action: "create",
      entityId: id,
      data: {
        farmerId: farmer.userId,
        cropType,
        quantity: 5,
        pricePerKg,
        latitude: 9.0,
        longitude: 38.0,
        locationName: "E2E Role Test",
        woreda: "E2E Role Test",
        description: "Role isolation test listing",
        status: "available",
        photoIds,
      },
    });
    if (result === "rate-limited") {
      test.skip(true, "POST /sync answered 429: no crop listing was made");
      return;
    }
    const message = result.message ?? "";
    test.skip(
      result.status === "error" &&
        FRAUD_THROTTLE_CODES.some((code) => message.startsWith(code)),
      `The fraud rules refused the create: ${message}`,
    );
    crop.version = expectApplied(result, `crop listing ${id} create`);
    expect(crop.version, "a new listing's version").toBe(1);
    test.info().annotations.push({
      type: "created",
      description: `crop listing ${id} (${cropType}, ${pricePerKg} ETB/kg)`,
    });

    await expectStatus(farmer, `/crops/${id}`, 200);
    const { crop: stored } = await getJson<{ crop: CropRow }>(farmer, `/crops/${id}`);
    expect(stored.cropId).toBe(id);
    expect(stored.farmerId).toBe(farmer.userId);
    expect(stored.cropType).toBe(cropType);
    expect(stored.version).toBe(crop.version);
  });

  test("trucker creates a trip: the operation is applied and reads back", async () => {
    // A UUID like the apps make, recorded before any request.
    const id = crypto.randomUUID();
    trip.id = id;
    const result = await syncOne(trucker, {
      entityType: "trip",
      action: "create",
      entityId: id,
      data: {
        truckerId: trucker.userId,
        fromWoreda: "Addis Ababa",
        toWoreda: "Dire Dawa",
        destinationLatitude: 9.6,
        destinationLongitude: 41.85,
        // Two days ahead: inside the 30 day window, and an open trip that has
        // not left is in the buyer's browse (trip-listing-api 2.2.0).
        departureDate: new Date(Date.now() + 2 * DAY_MS).toISOString(),
        capacity: 50,
        pricePerQuintal: 100,
        vehicleDescription: "E2E role isolation test trip",
        status: "open",
      },
    });
    if (result === "rate-limited") {
      test.skip(true, "POST /sync answered 429: no trip was made");
      return;
    }
    trip.version = expectApplied(result, `trip ${id} create`);
    expect(trip.version, "a new trip's version").toBe(1);
    test.info().annotations.push({ type: "created", description: `trip ${id}` });

    await expectStatus(trucker, `/trips/${id}`, 200);
    const { trip: stored } = await getJson<{ trip: TripRow }>(trucker, `/trips/${id}`);
    expect(stored.tripId).toBe(id);
    expect(stored.truckerId).toBe(trucker.userId);
    expect(stored.status).toBe("open");
    expect(stored.version).toBe(trip.version);
  });

  test("buyer's browse lists the crop listing made in this run", async () => {
    const id = crop.id;
    if (crop.version === null || !id) {
      test.skip(true, "No crop listing was made in this run (see the create test)");
      return;
    }
    await expect
      .poll(
        async () =>
          (
            await readList<CropRow>(buyer, "/crops", { status: "available" }, (rows) =>
              rows.some((c) => c.cropId === id),
            )
          ).some((c) => c.cropId === id),
        {
          message: `the buyer's browse (GET /crops?status=available) must list crop listing ${id}`,
          timeout: 60_000,
        },
      )
      .toBe(true);
    const { crop: seen } = await getJson<{ crop: CropRow }>(buyer, `/crops/${id}`);
    expect(seen.farmerId).toBe(farmer.userId);
  });

  test("buyer's browse lists the trip made in this run", async () => {
    const id = trip.id;
    if (trip.version === null || !id) {
      test.skip(true, "No trip was made in this run (see the create test)");
      return;
    }
    await expect
      .poll(
        async () =>
          (
            await readList<TripRow>(buyer, "/trips", { status: "open" }, (rows) =>
              rows.some((t) => t.tripId === id),
            )
          ).some((t) => t.tripId === id),
        {
          message: `the buyer's browse (GET /trips?status=open) must list trip ${id}`,
          timeout: 60_000,
        },
      )
      .toBe(true);
    const { trip: seen } = await getJson<{ trip: TripRow }>(buyer, `/trips/${id}`);
    expect(seen.truckerId).toBe(trucker.userId);
  });

  test("farmer's crop reads hold only the farmer's own listings", async () => {
    const reads: Array<[string, Record<string, string>]> = [
      ["own list (farmerId)", { farmerId: farmer.userId }],
      ["list without farmerId", {}],
      ["marketplace browse (status=available)", { status: "available" }],
    ];
    for (const [label, params] of reads) {
      const rows = await readList<CropRow>(farmer, "/crops", params);
      expect(
        rows
          .filter((c) => c.farmerId !== farmer.userId)
          .map((c) => `${c.cropId} of farmer ${c.farmerId}`),
        `the farmer's ${label} must hold no other farmer's listing`,
      ).toEqual([]);
    }

    const id = crop.id;
    if (crop.version !== null && id) {
      await expect
        .poll(
          async () =>
            (
              await readList<CropRow>(farmer, "/crops", { farmerId: farmer.userId }, (rows) =>
                rows.some((c) => c.cropId === id),
              )
            ).some((c) => c.cropId === id),
          { message: `the farmer's own list must hold crop listing ${id}`, timeout: 30_000 },
        )
        .toBe(true);
    }
  });

  test("farmer cannot list or read another farmer's listings", async () => {
    const market = await getJson<ListPage>(buyer, "/crops?status=available&limit=50");
    const other = (market.crops ?? []).find((c) => c.farmerId !== farmer.userId);
    // Any farmerId but the caller's own is refused, so a made-up one still
    // tests the list rule on a target where no other farmer has a listing.
    const otherFarmerId = other?.farmerId ?? crypto.randomUUID();
    expect(
      await statusOf(farmer, `/crops?farmerId=${encodeURIComponent(otherFarmerId)}`),
      `the farmer listing farmer ${otherFarmerId}'s crops`,
    ).toBe(403);
    if (!other) {
      test.info().annotations.push({
        type: "note",
        description: `No other farmer's listing on ${API_URL}: a leak of other farmers' listings into the farmer's lists, and the by-id read, could not be checked`,
      });
      return;
    }
    expect(
      await statusOf(farmer, `/crops/${encodeURIComponent(other.cropId)}`),
      `the farmer reading listing ${other.cropId} of farmer ${other.farmerId}`,
    ).toBe(403);
  });

  test("trucker's trip reads hold only the trucker's own trips", async () => {
    const reads: Array<[string, Record<string, string>]> = [
      ["own list (truckerId)", { truckerId: trucker.userId }],
      ["list without truckerId", {}],
      ["open trips browse (status=open)", { status: "open" }],
    ];
    for (const [label, params] of reads) {
      const rows = await readList<TripRow>(trucker, "/trips", params);
      expect(
        rows
          .filter((t) => t.truckerId !== trucker.userId)
          .map((t) => `${t.tripId} of trucker ${t.truckerId}`),
        `the trucker's ${label} must hold no other trucker's trip`,
      ).toEqual([]);
    }

    const id = trip.id;
    if (trip.version !== null && id) {
      await expect
        .poll(
          async () =>
            (
              await readList<TripRow>(trucker, "/trips", { truckerId: trucker.userId }, (rows) =>
                rows.some((t) => t.tripId === id),
              )
            ).some((t) => t.tripId === id),
          { message: `the trucker's own list must hold trip ${id}`, timeout: 30_000 },
        )
        .toBe(true);
    }
  });

  test("trucker's own trips page consistently and stay own-only (FUP-126)", async () => {
    // limit=1 makes every trip a page boundary (trip-listing-api 2.5.0):
    // hasMore agrees with nextToken and every page stays on this trucker's
    // trips. A page may be short or empty with a token when a run of dropped
    // trips uses up the read budget (Paging), so emptiness is not asserted.
    let nextToken: string | undefined;
    let pages = 0;
    do {
      const qs = new URLSearchParams({ truckerId: trucker.userId, limit: "1" });
      if (nextToken) qs.set("nextToken", nextToken);

      const data = await getJson<ListPage>(trucker, `/trips?${qs}`);
      expect(data.hasMore, "hasMore must agree with nextToken").toBe(
        Boolean(data.nextToken),
      );
      for (const t of data.trips ?? []) {
        expect(t.truckerId, "Trucker should only see own trips").toBe(
          trucker.userId,
        );
      }
      nextToken = data.nextToken ?? undefined;
      pages++;
    } while (nextToken && pages < 50);
  });

  test("trucker cannot list or read another trucker's trips", async () => {
    // Open trips that left are not browsed, so a target may hold other
    // truckers' trips only in later statuses: look through each status.
    let other: TripRow | undefined;
    for (const status of ["open", "matched", "inTransit", "delivered"]) {
      const browse = await getJson<ListPage>(buyer, `/trips?status=${status}&limit=50`);
      other = (browse.trips ?? []).find((t) => t.truckerId !== trucker.userId);
      if (other) break;
    }
    // Any truckerId but the caller's own is refused (trip-listing-api 2.3.1).
    const otherTruckerId = other?.truckerId ?? crypto.randomUUID();
    expect(
      await statusOf(trucker, `/trips?truckerId=${encodeURIComponent(otherTruckerId)}`),
      `the trucker listing trucker ${otherTruckerId}'s trips`,
    ).toBe(403);
    if (!other) {
      test.info().annotations.push({
        type: "note",
        description: `No other trucker's trip on ${API_URL}: a leak of other truckers' trips into the trucker's lists, and the by-id read, could not be checked`,
      });
      return;
    }
    expect(
      await statusOf(trucker, `/trips/${encodeURIComponent(other.tripId)}`),
      `the trucker reading trip ${other.tripId} of trucker ${other.truckerId}`,
    ).toBe(403);
  });

  test("farmer cannot read trips and trucker cannot read crops", async () => {
    expect(await statusOf(farmer, "/trips"), "the farmer's GET /trips").toBe(403);
    expect(await statusOf(trucker, "/crops"), "the trucker's GET /crops").toBe(403);
    if (trip.version !== null && trip.id) {
      expect(
        await statusOf(farmer, `/trips/${trip.id}`),
        "the farmer reading the run's trip",
      ).toBe(403);
    }
    if (crop.version !== null && crop.id) {
      expect(
        await statusOf(trucker, `/crops/${crop.id}`),
        "the trucker reading the run's crop listing",
      ).toBe(403);
    }
  });

  test("crop and trip reads require authentication", async () => {
    expect(await statusOf(null, `/crops?farmerId=${farmer.userId}`)).toBe(401);
    expect(await statusOf(null, "/trips")).toBe(401);
  });

  test("cleanup deletes the run's crop listing and cancels its trip, and both are gone", async () => {
    if (!crop.id && !trip.id) {
      test.skip(true, "Nothing was made in this run");
      return;
    }
    await removeCrop();
    await removeTrip();
    for (const [what, row] of [["crop listing", crop], ["trip", trip]] as const) {
      if (row.id && row.version !== null) {
        test.info().annotations.push({
          type: "removed",
          description: `${what} ${row.id}: 404 for its owner and the buyer`,
        });
      }
    }
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// UI-Level Role Isolation Tests (require running web app + API)
// ─────────────────────────────────────────────────────────────────────────────

test.describe("Role Isolation - UI Navigation per Role", () => {
  /**
   * These tests inject auth tokens into localStorage to simulate
   * authenticated sessions for each role, then verify navigation.
   *
   * Flutter CanvasKit renders to canvas, so we verify via:
   * - URL-based routing (go_router routes)
   * - Screenshots for visual diff
   * - Semantics tree content
   *
   * Role-specific navigation tabs (from AdaptiveShell):
   * - Farmer: Home, My Crops (/my-crops), Deliveries, Payments
   * - Trucker: Home, My Trips (/trips), Deliveries, Payments
   * - Buyer:  Home, Find Crops (/find-crops), Deliveries, Payments
   */

  test("farmer - /my-crops route loads (role-specific tab)", async ({
    page,
  }) => {
    await seedAuth(page, "farmer");

    await page.goto("/my-crops");
    await waitForFlutter(page);

    // Authenticated farmer must reach its role route, not bounce to /login.
    expect(page.url()).not.toContain("/login");
    expect(page.url()).toContain("/my-crops");
    await page.screenshot({
      path: "test-results/webapp-role-farmer-my-crops.png",
    });
  });

  test("trucker - /trips route loads (role-specific tab)", async ({
    page,
  }) => {
    await seedAuth(page, "trucker");

    await page.goto("/trips");
    await waitForFlutter(page);

    expect(page.url()).not.toContain("/login");
    expect(page.url()).toContain("/trips");
    await page.screenshot({
      path: "test-results/webapp-role-trucker-trips.png",
    });
  });

  test("buyer - /find-crops route loads (role-specific tab)", async ({
    page,
  }) => {
    await seedAuth(page, "buyer");

    await page.goto("/find-crops");
    await waitForFlutter(page);

    expect(page.url()).not.toContain("/login");
    expect(page.url()).toContain("/find-crops");
    await page.screenshot({
      path: "test-results/webapp-role-buyer-find-crops.png",
    });
  });

  test("all three roles - home page renders with role-specific UI", async ({
    page,
  }) => {
    for (const role of ["farmer", "trucker", "buyer"] as const) {
      await seedAuth(page, role);

      // Each role must end authenticated (off /login) — no silent skip.
      expect(
        page.url(),
        `${role} must be authenticated (off /login)`,
      ).not.toContain("/login");

      await page.screenshot({
        path: `test-results/webapp-role-${role}-home.png`,
      });
    }
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// Responsive Role Isolation (viewport-specific)
// ─────────────────────────────────────────────────────────────────────────────

test.describe("Role Isolation - Responsive", () => {
  test("mobile viewport (390px) shows bottom nav (no sidebar)", async ({
    page,
  }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await seedAuth(page, "farmer");
    await waitForFlutter(page);

    expect(page.url()).not.toContain("/login");
    // At mobile viewport, AdaptiveShell renders BottomNavigationBar (not sidebar)
    await page.screenshot({
      path: "test-results/webapp-role-farmer-mobile.png",
    });
  });

  test("desktop viewport (1440px) shows sidebar nav", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await seedAuth(page, "farmer");
    await waitForFlutter(page);

    expect(page.url()).not.toContain("/login");
    // At desktop viewport, AdaptiveShell renders full sidebar with labels
    await page.screenshot({
      path: "test-results/webapp-role-farmer-desktop-sidebar.png",
    });
  });

  test("tablet viewport (800px) shows rail nav", async ({ page }) => {
    await page.setViewportSize({ width: 800, height: 1200 });
    await seedAuth(page, "farmer");
    await waitForFlutter(page);

    expect(page.url()).not.toContain("/login");
    // At tablet viewport, AdaptiveShell renders collapsed NavigationRail
    await page.screenshot({
      path: "test-results/webapp-role-farmer-tablet-rail.png",
    });
  });
});
