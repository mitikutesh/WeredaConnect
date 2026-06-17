import { test, expect } from "@playwright/test";
import { seedAuth } from "../helpers/auth";

/**
 * Flutter Web App - Home Screen Tests
 *
 * Verifies the home screen renders correctly after login.
 * Uses screenshots + console error checks since CanvasKit
 * renders to canvas (limited DOM access).
 */

/** Wait for Flutter to fully render. */
async function waitForFlutter(page: import("@playwright/test").Page) {
  await page.waitForSelector("flutter-view", { timeout: 30_000 });
  await page.waitForTimeout(3000);
}

test.describe("Webapp Home Screen", () => {
  test("home page renders after authentication", async ({ page }) => {
    // Authenticate via the production-gated test seam. seedAuth throws if the
    // seam is absent (prod build) — no silent skip.
    await seedAuth(page, "farmer");

    // Leaving /login is the only reliable auth signal on CanvasKit.
    expect(page.url(), "auth seam must navigate off /login").not.toContain(
      "/login",
    );
    await expect(page.locator("flutter-view")).toBeVisible();
    await page.screenshot({
      path: "test-results/webapp-home-authenticated.png",
    });
  });

  test("home page has no critical JS errors", async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (err) => {
      errors.push(err.message);
    });

    await page.goto("/");
    await waitForFlutter(page);

    expect(errors).toEqual([]);
  });

  test("page title is WeredaConnect", async ({ page }) => {
    await page.goto("/");
    await waitForFlutter(page);
    const title = await page.title();
    expect(title).toContain("WeredaConnect");
  });

  test("responsive viewport renders correctly", async ({ page }) => {
    const viewports = [
      { name: "mobile", width: 390, height: 844 },
      { name: "tablet", width: 800, height: 1200 },
      { name: "desktop", width: 1440, height: 900 },
    ];

    for (const vp of viewports) {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto("/");
      await waitForFlutter(page);

      const flutterView = page.locator("flutter-view");
      await expect(
        flutterView,
        `Flutter view missing at ${vp.name} viewport`,
      ).toBeVisible();

      await page.screenshot({
        path: `test-results/webapp-home-${vp.name}.png`,
      });
    }
  });
});
