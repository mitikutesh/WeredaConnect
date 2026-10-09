import { test, expect } from "@playwright/test";
import { TEST_USERS, apiLogin, API_URL } from "../helpers/auth";

/**
 * E2E: Listing creation flow.
 *
 * Verifies that when a farmer creates a listing:
 * 1. It appears in the farmer's listings via API
 * 2. The listing count increases
 * 3. A buyer can see the listing
 *
 * These tests hit the live API directly (not the Flutter UI) to validate
 * the data flow that the overview stats depend on.
 *
 * NOTE: Fraud detection allows max 5 listings/hour/farmer and rejects a
 * near-duplicate of a listing created in the last hour. The server reports
 * both as HTTP 200 with a per-operation "error" result (not 429), so the
 * creation test skips on those two codes and fails on anything else, for
 * example CROP_VALIDATION. Read-only tests always run.
 *
 * The created listing is deleted in afterAll with the version the create
 * returned. A failed delete fails the run: a leftover listing trips the
 * duplicate check on every run for the next hour.
 */

interface CropsResponse {
  crops: Array<{ cropId: string; farmerId: string; status: string }>;
  nextToken?: string;
}

/**
 * Must be one of the server's AllowedCropTypes (SyncFunction.cs), which
 * mirror the mobile CropType enum: teff, wheat, barley, maize, chickpea,
 * lentils, sorghum, coffee, sesame. Anything else is rejected with
 * "CROP_VALIDATION: Invalid cropType.".
 */
const E2E_CROP_TYPE = "sorghum";

/** Fraud throttles that make the creation test skip instead of fail. */
const FRAUD_THROTTLE_CODES = [
  "FRAUD_VELOCITY_EXCEEDED",
  "FRAUD_DUPLICATE_LISTING",
];

type CreateListingResult =
  | { listingId: string; version: number; skipReason?: undefined }
  | { listingId: null; skipReason: string };

interface SyncOperationResult {
  status?: string;
  serverVersion?: number;
  message?: string;
}

/** The single per-operation result of a one-operation /sync batch. */
async function readOpResult(res: Response): Promise<SyncOperationResult> {
  const syncResult = await res.json();
  const opResult: SyncOperationResult | undefined = syncResult.results?.[0];
  if (!opResult) {
    throw new Error(`POST /sync returned no operation result: ${JSON.stringify(syncResult)}`);
  }
  return opResult;
}

/** Fetch all crops for a user via paginated API calls. */
async function fetchAllCrops(
  token: string,
  params: Record<string, string> = {},
): Promise<CropsResponse["crops"]> {
  const allCrops: CropsResponse["crops"] = [];
  let nextToken: string | undefined;

  do {
    const qs = new URLSearchParams(params);
    if (nextToken) qs.set("nextToken", nextToken);

    const res = await fetch(`${API_URL}/crops?${qs}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(res.ok, `GET /crops failed: ${res.status}`).toBeTruthy();

    const data: CropsResponse = await res.json();
    allCrops.push(...data.crops);
    nextToken = data.nextToken ?? undefined;
  } while (nextToken);

  return allCrops;
}

/**
 * Try to create a listing via the /sync endpoint (same as mobile app).
 * Returns the listing ID on success, or a skip reason when the request was
 * throttled (HTTP 429, or a fraud velocity/duplicate rejection). Any other
 * failed operation throws so the test fails.
 */
async function tryCreateListing(
  token: string,
  farmerId: string,
  cropType: string,
): Promise<CreateListingResult> {
  const listingId = `e2e_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
  const now = new Date().toISOString();

  const res = await fetch(`${API_URL}/sync`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({
      syncBatchId: `e2e-batch-${Date.now()}`,
      clientId: "e2e-test",
      operations: [
        {
          operationId: `e2e-op-${Date.now()}`,
          entityType: "crop_listing",
          action: "create",
          entityId: listingId,
          timestamp: now,
          idempotencyKey: `e2e_create_${listingId}`,
          data: {
            farmerId,
            cropType,
            quantity: 10,
            pricePerKg: 50,
            latitude: 9.0,
            longitude: 38.0,
            locationName: "E2E Test Woreda",
            woreda: "E2E Test Woreda",
            description: `E2E test listing - ${cropType}`,
            status: "active",
            createdAt: now,
          },
        },
      ],
    }),
  });

  // API-level throttling (rate limiter in front of /sync)
  if (res.status === 429) {
    return { listingId: null, skipReason: "POST /sync returned 429" };
  }

  expect(res.ok, `POST /sync failed: ${res.status}`).toBeTruthy();

  const opResult = await readOpResult(res);
  if (opResult.status !== "success") {
    const message = opResult.message ?? "";
    if (FRAUD_THROTTLE_CODES.some((code) => message.startsWith(code))) {
      return { listingId: null, skipReason: message };
    }
    throw new Error(`Sync operation failed: ${opResult.status} - ${message}`);
  }
  if (typeof opResult.serverVersion !== "number") {
    throw new Error(`Create of ${listingId} returned no serverVersion`);
  }

  return { listingId, version: opResult.serverVersion };
}

/**
 * Delete a listing via the /sync endpoint. The server only deletes when
 * clientVersion matches the stored version, so pass the version the create
 * returned. Throws unless the delete operation succeeded.
 */
async function deleteListing(
  token: string,
  farmerId: string,
  listingId: string,
  version: number,
): Promise<void> {
  const now = new Date().toISOString();
  const res = await fetch(`${API_URL}/sync`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({
      syncBatchId: `e2e-batch-${Date.now()}`,
      clientId: "e2e-test",
      operations: [
        {
          operationId: `e2e-op-${Date.now()}`,
          entityType: "crop_listing",
          action: "delete",
          entityId: listingId,
          timestamp: now,
          idempotencyKey: `e2e_delete_${listingId}`,
          clientVersion: version,
          data: { farmerId },
        },
      ],
    }),
  });
  expect(res.ok, `POST /sync (delete) failed: ${res.status}`).toBeTruthy();

  const opResult = await readOpResult(res);
  if (opResult.status !== "success") {
    throw new Error(
      `Cleanup of ${listingId} failed: ${opResult.status} - ${opResult.message ?? ""}`,
    );
  }
}

test.describe("Listing Creation Flow", () => {
  // Run tests serially — they share state
  test.describe.configure({ mode: "serial" });

  let farmerToken: string;
  let buyerToken: string;
  let farmerId: string;

  // Shared listing created once, verified by multiple tests (null if rate-limited)
  let sharedListingId: string | null = null;
  let sharedListingVersion = 0;
  let countBefore: number;

  test.beforeAll(async () => {
    // Login as farmer
    const farmerTokens = await apiLogin(
      TEST_USERS.farmer.phone,
      TEST_USERS.farmer.password,
    );
    farmerToken = farmerTokens.idToken;

    // Get farmer userId from token
    const payload = JSON.parse(
      Buffer.from(farmerToken.split(".")[1], "base64").toString(),
    );
    farmerId = payload.sub;

    // Login as buyer
    const buyerTokens = await apiLogin(
      TEST_USERS.buyer.phone,
      TEST_USERS.buyer.password,
    );
    buyerToken = buyerTokens.idToken;

    // Capture initial listing count
    const before = await fetchAllCrops(farmerToken, { farmerId });
    countBefore = before.length;
  });

  test.afterAll(async () => {
    if (sharedListingId) {
      await deleteListing(
        farmerToken,
        farmerId,
        sharedListingId,
        sharedListingVersion,
      );
    }
  });

  test("farmer creates a listing and count increases", async () => {
    const result = await tryCreateListing(farmerToken, farmerId, E2E_CROP_TYPE);

    if (result.listingId === null) {
      // Fraud throttle, not a product bug: skip the creation assertions.
      // The read-only tests below use the farmer's existing listings.
      test.skip(true, `Skipped: ${result.skipReason}`);
      return;
    }
    sharedListingId = result.listingId;
    sharedListingVersion = result.version;

    // Verify count increased
    const after = await fetchAllCrops(farmerToken, { farmerId });
    expect(after.length).toBeGreaterThan(countBefore);
    expect(after.some((c) => c.cropId === sharedListingId)).toBeTruthy();
  });

  test("farmer can see listings in My Listings (farmerId filter)", async () => {
    const crops = await fetchAllCrops(farmerToken, { farmerId });

    // Farmer must have at least 1 listing (from this or previous runs)
    expect(crops.length, "Farmer should have at least 1 listing").toBeGreaterThan(0);
    // Every returned listing must belong to this farmer
    expect(crops.every((c) => c.farmerId === farmerId)).toBeTruthy();

    // If we created a listing this run, verify it specifically
    if (sharedListingId) {
      const found = crops.find((c) => c.cropId === sharedListingId);
      expect(found, "Created listing should appear in farmer's listings").toBeTruthy();
    }
  });

  test("buyer can see active listings in marketplace", async () => {
    // Fetch active listings as buyer (no farmerId filter = marketplace)
    const crops = await fetchAllCrops(buyerToken, { status: "available" });

    // There should be active listings in the marketplace
    expect(crops.length, "Marketplace should have active listings").toBeGreaterThan(0);

    // If we created a listing this run, verify the buyer can see it
    if (sharedListingId) {
      const found = crops.find((c) => c.cropId === sharedListingId);
      expect(found, "Buyer should see farmer's listing in marketplace").toBeTruthy();
    }
  });

  test("overview stats API returns correct structure for farmer", async () => {
    const res = await fetch(
      `${API_URL}/crops?farmerId=${farmerId}`,
      { headers: { Authorization: `Bearer ${farmerToken}` } },
    );
    expect(res.ok).toBeTruthy();

    const data = await res.json();

    // Verify response structure matches what overview stats expect
    expect(data).toHaveProperty("crops");
    expect(Array.isArray(data.crops)).toBeTruthy();

    // Farmer should have at least 1 listing
    expect(
      data.crops.length,
      "Farmer should have at least 1 listing",
    ).toBeGreaterThan(0);
  });

  test("overview stats API requires authentication", async () => {
    // Without auth token, /crops should return 401
    const res = await fetch(`${API_URL}/crops?farmerId=${farmerId}`);
    expect(res.status).toBe(401);
  });
});
