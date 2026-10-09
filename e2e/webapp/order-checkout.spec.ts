import { test, expect, type Page, type Route } from "@playwright/test";
import { API_URL, seedAuth } from "../helpers/auth";

/**
 * E2E (ESC-44): the live order checkout in a real browser, with the live
 * Telebirr gateway faked by request interception. Dev runs the mock gateway
 * and the Telebirr sandbox only accepts allow-listed networks, so this is the
 * closest the 200 path gets to a real run here.
 *
 * Proves, in Chrome: "Continue payment" on a payment_submitted order asks
 * POST /escrow/order-checkout once; the page is offered as a real link that
 * opens a new tab (no pop-up blocker in the way); the app keeps reading the
 * order and says "Payment confirmed" once it is funded.
 *
 * Needs a LOCAL web build made with the dev dart-defines plus
 * --dart-define=TELEBIRR_LIVE=true, served on localhost:8080 (the dev API's
 * CORS allow-list), and E2E_LIVE_CHECKOUT=1:
 *
 *   cd mobile && flutter build web --no-tree-shake-icons \
 *     --target lib/main_web.dart --dart-define=TELEBIRR_LIVE=true <dev defines>
 *   node e2e/helpers/spa-server.cjs mobile/build/web 8080
 *   cd e2e && E2E_LIVE_CHECKOUT=1 WEBAPP_URL=http://localhost:8080 \
 *     npx playwright test webapp/order-checkout.spec.ts
 *
 * Only the order list, the order and the checkout are faked; login is real.
 */

const ORDER_ID = "e2e-live-checkout-order";
const FARMER_NAME = "E2E Checkout Farmer";

function fakeOrder(buyerId: string, status: string) {
  return {
    orderId: ORDER_ID,
    listingId: "e2e-listing",
    buyerId,
    farmerId: "e2e-farmer",
    buyerName: "Meron Alemu",
    farmerName: FARMER_NAME,
    cropType: "teff",
    quantity: 100,
    pricePerUnit: 24.5,
    totalPrice: 2450,
    orderTotal: 2448.75,
    status,
    paymentMethod: "telebirr",
    createdAt: new Date().toISOString(),
    version: 2,
  };
}

const API = API_URL.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/**
 * Answer a faked API call. The app calls the API cross-origin, so the
 * preflight and the answer carry the CORS headers the real API sends.
 */
async function fulfillApi(route: Route, json: unknown): Promise<void> {
  const cors = {
    "access-control-allow-origin": "*",
    "access-control-allow-headers": "*",
    "access-control-allow-methods": "GET,POST,OPTIONS",
  };
  if (route.request().method() === "OPTIONS") {
    await route.fulfill({ status: 204, headers: cors });
    return;
  }
  await route.fulfill({ json, headers: cors });
}

/** Turn on Flutter's semantics tree so widgets can be found and clicked. */
async function enableSemantics(page: Page): Promise<void> {
  await page.evaluate(() => {
    const placeholder = document.querySelector(
      "flt-semantics-placeholder",
    ) as HTMLElement | null;
    placeholder?.click();
  });
  await page.waitForTimeout(500);
}

/** Click the semantics node whose text contains [text]. */
async function clickText(page: Page, text: string): Promise<void> {
  const node = page.locator("flt-semantics", { hasText: text }).last();
  await expect(node).toBeVisible({ timeout: 20_000 });
  const box = await node.boundingBox();
  if (!box) throw new Error(`No box for "${text}"`);
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
}

test.describe("Live order checkout (ESC-44)", () => {
  test.skip(
    process.env.E2E_LIVE_CHECKOUT !== "1",
    "Needs a local TELEBIRR_LIVE=true web build on localhost:8080",
  );

  test("Continue payment opens Telebirr in a new tab, then confirms",
    async ({ page, context, baseURL }) => {
      await page.setViewportSize({ width: 390, height: 1400 });
      let buyerId = "";
      let paid = false;
      let checkoutCalls = 0;
      const toPayUrl = `${baseURL}/fake-telebirr-checkout?prepay_id=E2E`;

      // The buyer's order list and the order itself.
      await page.route(new RegExp(`^${API}/orders(\\?.*)?$`), async (route) => {
        const url = new URL(route.request().url());
        buyerId = url.searchParams.get("buyerId") ?? buyerId;
        await fulfillApi(route, {
          orders: [fakeOrder(buyerId, "payment_submitted")],
        });
      });
      await page.route(
        new RegExp(`^${API}/orders/${ORDER_ID}$`),
        async (route) => {
          await fulfillApi(route, {
            order: fakeOrder(buyerId, paid ? "funded" : "payment_submitted"),
          });
        },
      );
      // The live gateway's checkout.
      await page.route(
        new RegExp(`^${API}/escrow/order-checkout$`),
        async (route) => {
          if (route.request().method() === "POST") {
            checkoutCalls++;
            expect(route.request().postDataJSON()).toEqual({
              orderId: ORDER_ID,
            });
          }
          await fulfillApi(route, {
            orderId: ORDER_ID,
            amount: 2448.75,
            currency: "ETB",
            toPayUrl,
            openUntil: new Date(Date.now() + 2 * 60 * 60 * 1000).toISOString(),
            isExisting: false,
          });
        },
      );
      // Stand-in for the Telebirr H5 page.
      await context.route(/fake-telebirr-checkout/, (route) =>
        route.fulfill({
          contentType: "text/html",
          body: "<h1>Telebirr (fake)</h1>",
        }),
      );

      await seedAuth(page, "buyer");
      await page.goto("/orders");
      await page.waitForSelector("flutter-view", { timeout: 30_000 });
      await page.waitForTimeout(3000);
      await enableSemantics(page);

      await clickText(page, FARMER_NAME);
      await clickText(page, "Continue payment");

      // The page is a real link (the Link widget's anchor, plus its
      // semantics node): a click opens a new tab, no pop-up block.
      const link = page
        .locator(`a[href*="fake-telebirr-checkout"]:visible`)
        .first();
      await expect(link).toBeVisible({ timeout: 20_000 });
      await expect(link).toHaveAttribute("target", "_blank");
      expect(checkoutCalls).toBe(1);
      // Click where the buyer would (semantics are on, so its node is on top).
      const box = await link.boundingBox();
      if (!box) throw new Error("No box for the Telebirr link");
      const [telebirr] = await Promise.all([
        context.waitForEvent("page"),
        page.mouse.click(box.x + box.width / 2, box.y + box.height / 2),
      ]);
      await telebirr.waitForLoadState();
      expect(telebirr.url()).toContain("prepay_id=E2E");

      // The buyer pays; the webhook funds the order; back in the app tab.
      paid = true;
      await telebirr.close();
      await page.bringToFront();
      await expect(
        page.locator("flt-semantics", {
          hasText: "Payment confirmed. Funds are held safely.",
        }).first(),
      ).toBeVisible({ timeout: 45_000 });
      expect(checkoutCalls).toBe(1);
    });
});
