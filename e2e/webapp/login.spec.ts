import { test, expect, type Page } from "@playwright/test";
import { TEST_USERS } from "../helpers/auth";

/**
 * Flutter Web App - Login Tests
 *
 * Flutter CanvasKit renders to <canvas>, so we test via:
 * - URL routing (go_router redirects)
 * - Page load success (no JS errors)
 * - Flutter DOM structure (flutter-view, flt-glass-pane)
 * - Keyboard-driven interaction
 * - Semantics tree for form elements
 * - Screenshots for visual verification
 *
 * Note: Flutter web uses CanvasKit which renders everything to a canvas,
 * so standard DOM selectors (input, button) won't find Flutter widgets.
 * We rely on Flutter's semantics tree (flt-semantics-host) for interaction.
 */

/** Wait for Flutter to fully render. */
async function waitForFlutter(page: Page) {
  await page.waitForSelector("flutter-view", { timeout: 30_000 });
  // Extra time for CanvasKit to paint
  await page.waitForTimeout(3000);
}

/**
 * Enable Flutter's semantics tree. CanvasKit only mirrors widgets into the
 * DOM once accessibility is switched on, which happens by activating the
 * off-screen <flt-semantics-placeholder> button. A Tab press does not do it
 * (verified against dev 2026-10-06), which is why the earlier helper never
 * found a form field and the tests fell through to no-op fallbacks.
 */
async function enableSemantics(page: Page) {
  const placeholder = page.locator("flt-semantics-placeholder");
  if ((await placeholder.count()) > 0) {
    await placeholder.dispatchEvent("click");
  }
  await page
    .locator("flt-semantics")
    .first()
    .waitFor({ state: "attached", timeout: 10_000 });
}

/**
 * Semantic mirrors of the login fields, matched by input type (locale
 * independent). They also carry data-semantics-role="text-field", but that
 * value has changed across Flutter releases (the old "textfield" selector
 * matched nothing), so the spec keys on type, which also tells phone from
 * password.
 */
const PHONE_INPUT = 'flt-semantics input[type="tel"]';
const PASSWORD_INPUT = 'flt-semantics input[type="password"]';

/** The Login button's semantics node (English l10n label). */
function loginButton(page: Page) {
  return page.locator('flt-semantics[role="button"]', { hasText: /^Login$/ });
}

/**
 * Type into a semantic text field with real key events. fill() writes the
 * DOM value directly and intermittently never reaches the Flutter text
 * controller. Flutter also attaches its editing state a beat after the DOM
 * input receives focus, and a keystroke sent before that is dropped, so the
 * helper waits for focus, paces the keys, and asserts the value landed.
 */
async function typeInto(page: Page, selector: string, text: string) {
  const field = page.locator(selector);
  await field.click();
  await expect(field).toBeFocused();
  await page.waitForTimeout(250);
  await field.pressSequentially(text, { delay: 30 });
  await expect(field).toHaveValue(text);
}

/**
 * Text rendered by a widget, looked up inside the semantics tree only.
 * Flutter announces live-region text through a separate aria-live element
 * (flt-announcement-host) outside flt-semantics-host, so an unscoped
 * getByText could match twice while the announcement is up and trip strict
 * mode.
 */
function semanticText(page: Page, text: string | RegExp) {
  return page.locator("flt-semantics-host").getByText(text, { exact: true });
}

/** Fill both fields and press Login (Enter in the password field does not submit). */
async function submitLogin(page: Page, phone: string, password: string) {
  await typeInto(page, PHONE_INPUT, phone);
  await typeInto(page, PASSWORD_INPUT, password);
  await loginButton(page).click();
}

test.describe("Webapp Login - Rendering", () => {
  test("redirects unauthenticated user to /login", async ({ page }) => {
    await page.goto("/");
    await waitForFlutter(page);
    // go_router should redirect to /login
    expect(page.url()).toContain("/login");
  });

  test("login page renders Flutter view", async ({ page }) => {
    await page.goto("/login");
    await waitForFlutter(page);

    // Flutter creates <flutter-view> with <flt-glass-pane> inside
    const flutterView = page.locator("flutter-view");
    await expect(flutterView).toBeVisible();

    // flt-glass-pane contains the canvas
    const glassPane = page.locator("flt-glass-pane");
    await expect(glassPane).toBeAttached();

    // Screenshot for visual verification
    await page.screenshot({ path: "test-results/webapp-login.png" });
  });

  test("login page loads without console errors", async ({ page }) => {
    const errors: string[] = [];
    page.on("console", (msg) => {
      if (msg.type() === "error") {
        errors.push(msg.text());
      }
    });

    await page.goto("/login");
    await waitForFlutter(page);

    // Filter out known non-critical warnings
    const realErrors = errors.filter(
      (e) =>
        !e.includes("favicon") &&
        !e.includes("wasm") &&
        !e.includes("service-worker") &&
        !e.includes("404") &&
        !e.includes("manifest.json"),
    );
    expect(realErrors).toEqual([]);
  });

  test("page title contains WeredaConnect", async ({ page }) => {
    await page.goto("/login");
    await waitForFlutter(page);
    const title = await page.title();
    expect(title).toContain("WeredaConnect");
  });
});

test.describe("Webapp Login - Semantics & Form Interaction", () => {
  test("semantics placeholder exposes the login form to the DOM", async ({
    page,
  }) => {
    await page.goto("/login");
    await waitForFlutter(page);
    await enableSemantics(page);

    await expect(page.locator("flt-semantics-host")).toBeAttached();
    await expect(loginButton(page)).toHaveCount(1);
  });

  test("semantics tree contains text fields for phone and password", async ({
    page,
  }) => {
    await page.goto("/login");
    await waitForFlutter(page);
    await enableSemantics(page);

    // Flutter mirrors text fields as <input> elements inside flt-semantics.
    // Exactly one phone (tel) and one password field must be present; a
    // count of 0 means semantics never came up and is a failure.
    await expect(page.locator(PHONE_INPUT)).toHaveCount(1);
    await expect(page.locator(PASSWORD_INPUT)).toHaveCount(1);
  });

  test("login form accepts phone number input via keyboard", async ({
    page,
  }) => {
    await page.goto("/login");
    await waitForFlutter(page);
    await enableSemantics(page);

    // typeInto asserts that the typed value reached the field.
    await typeInto(page, PHONE_INPUT, TEST_USERS.farmer.phone);
  });

  test("empty form submit does not navigate away (validation)", async ({
    page,
  }) => {
    await page.goto("/login");
    await waitForFlutter(page);
    await enableSemantics(page);

    // Press Login without filling any field
    await loginButton(page).click();

    // Form validation blocks the submit: the required-field error renders
    // and the route stays on /login.
    await expect(semanticText(page, "Phone number is required")).toBeVisible();
    expect(page.url()).toContain("/login");
    await page.screenshot({
      path: "test-results/webapp-login-empty-submit.png",
    });
  });
});

test.describe("Webapp Login - Routes & Links", () => {
  test("/register route loads (register link target)", async ({ page }) => {
    await page.goto("/register");
    await waitForFlutter(page);

    const flutterView = page.locator("flutter-view");
    await expect(flutterView).toBeVisible();
    // go_router handles the /register route
    expect(page.url()).toContain("/register");
  });

  test("/forgot-password route loads", async ({ page }) => {
    await page.goto("/forgot-password");
    await waitForFlutter(page);

    const flutterView = page.locator("flutter-view");
    await expect(flutterView).toBeVisible();
    // go_router handles the /forgot-password route
    expect(page.url()).toContain("/forgot-password");
  });

  test("/reset-password route loads", async ({ page }) => {
    await page.goto("/reset-password");
    await waitForFlutter(page);

    const flutterView = page.locator("flutter-view");
    await expect(flutterView).toBeVisible();
    expect(page.url()).toContain("/reset-password");
  });
});

test.describe("Webapp Login - Authentication Flow", () => {
  test("login with valid credentials redirects to home", async ({ page }) => {
    await page.goto("/login");
    await waitForFlutter(page);

    await enableSemantics(page);
    await submitLogin(
      page,
      TEST_USERS.farmer.phone,
      TEST_USERS.farmer.password,
    );

    // Leaving /login is the only reliable success signal on CanvasKit. A
    // missing redirect must fail this test, never pass on a swallowed timeout.
    // The router sends a signed-in user on an auth route to home ("/").
    await page.waitForURL((url) => !url.pathname.includes("/login"), {
      timeout: 20_000,
    });
    expect(new URL(page.url()).pathname).toBe("/");
  });

  test("shows error on invalid credentials (API interaction)", async ({
    page,
  }) => {
    await page.goto("/login");
    await waitForFlutter(page);
    await enableSemantics(page);
    await submitLogin(page, "+251900000000", "WrongPassword1");

    // The API rejects the attempt and the screen renders the localized error,
    // which semantics mirrors into the DOM. Repeated runs against the same
    // bogus phone trip the server's failed-attempt lockout, which is also an
    // error banner for invalid credentials. The route must stay on /login.
    await expect(
      semanticText(
        page,
        /^(Invalid phone number or password|Account locked due to too many failed attempts\. Try again in \d+ min\.)$/,
      ),
    ).toBeVisible({ timeout: 15_000 });
    expect(page.url()).toContain("/login");

    // Screenshot to verify error state is visible in the canvas
    await page.screenshot({
      path: "test-results/webapp-login-invalid-credentials.png",
    });
  });
});

test.describe("Webapp Login - Responsive Layout", () => {
  test("mobile viewport (390px) renders correctly", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/login");
    await waitForFlutter(page);

    const flutterView = page.locator("flutter-view");
    await expect(flutterView).toBeVisible();

    await page.screenshot({
      path: "test-results/webapp-login-mobile-390.png",
    });
  });

  test("tablet viewport (768px) renders correctly", async ({ page }) => {
    await page.setViewportSize({ width: 768, height: 1024 });
    await page.goto("/login");
    await waitForFlutter(page);

    const flutterView = page.locator("flutter-view");
    await expect(flutterView).toBeVisible();

    await page.screenshot({
      path: "test-results/webapp-login-tablet-768.png",
    });
  });

  test("desktop viewport (1440px) renders correctly", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/login");
    await waitForFlutter(page);

    const flutterView = page.locator("flutter-view");
    await expect(flutterView).toBeVisible();

    await page.screenshot({
      path: "test-results/webapp-login-desktop-1440.png",
    });
  });

  test("no JS errors across all viewport sizes", async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (err) => {
      errors.push(err.message);
    });

    const viewports = [
      { width: 390, height: 844 },
      { width: 768, height: 1024 },
      { width: 1440, height: 900 },
    ];

    for (const vp of viewports) {
      await page.setViewportSize(vp);
      await page.goto("/login");
      await waitForFlutter(page);
    }

    // Filter Flutter-internal errors that are non-critical
    const realErrors = errors.filter(
      (e) =>
        !e.includes("LateInitializationError") &&
        !e.includes("setState") &&
        !e.includes("mounted"),
    );
    expect(realErrors, "Critical JS errors found across viewports").toEqual([]);
  });
});
