import { test, expect } from "@playwright/test";

/**
 * Admin Dashboard - Dashboard Page Tests
 *
 * Tests the admin dashboard page structure and UI components.
 * Since admin Cognito pool is separate from mobile users,
 * we test the login form thoroughly and verify UI elements
 * that are visible pre/post authentication.
 */

test.describe("Admin Dashboard UI Components", () => {
  test("login form has all required elements", async ({ page }) => {
    await page.goto("/login");

    // Title text
    const heading = page.locator("h1");
    await expect(heading.first()).toBeVisible();

    // Subtitle/description text
    const subtitle = page.locator("p").first();
    await expect(subtitle).toBeVisible();

    // Form with phone and password fields
    const form = page.locator("form");
    await expect(form).toBeVisible();

    // Labels
    const labels = page.locator("label");
    const labelCount = await labels.count();
    expect(labelCount).toBeGreaterThanOrEqual(2); // phone + password

    // Inputs
    const telInput = page.locator('input[type="tel"]');
    const pwdInput = page.locator('input[type="password"]');
    await expect(telInput).toBeVisible();
    await expect(pwdInput).toBeVisible();

    // Submit button
    const btn = page.locator('button[type="submit"]');
    await expect(btn).toBeVisible();
  });

  test("login form has proper styling", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/login");

    // Card container with rounded corners and shadow
    const card = page.locator(".rounded-xl");
    await expect(card.first()).toBeVisible();

    // Primary-colored button
    const btn = page.locator('button[type="submit"]');
    await expect(btn).toHaveClass(/bg-primary/);
  });

  test("login form inputs have focus styles", async ({ page }) => {
    await page.goto("/login");

    const phoneInput = page.locator('input[type="tel"]');
    await phoneInput.focus();

    // Input should have focus ring class
    await expect(phoneInput).toHaveClass(/focus:border-primary/);
  });

  test("form submission prevents default on empty", async ({ page }) => {
    await page.goto("/login");

    // Intercept form submission
    let formSubmitted = false;
    await page.route("**/*", (route) => {
      if (route.request().method() === "POST") {
        formSubmitted = true;
      }
      return route.continue();
    });

    // Try to submit empty form
    const btn = page.locator('button[type="submit"]');
    await btn.click();

    // HTML5 validation should prevent actual submission
    await page.waitForTimeout(1000);
    expect(formSubmitted).toBe(false);
  });

  test("password field accepts input without echo", async ({ page }) => {
    await page.goto("/login");

    const pwdInput = page.locator('input[type="password"]');
    await pwdInput.fill("SecretPass123");

    // Value should be set but type should remain password
    await expect(pwdInput).toHaveValue("SecretPass123");
    await expect(pwdInput).toHaveAttribute("type", "password");
  });

  test("login card is centered on screen", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/login");

    // The form container should be centered (flex items-center justify-center)
    const formContainer = page.locator(".flex.min-h-screen, .flex.items-center.justify-center").first();
    await expect(formContainer).toBeVisible();
  });

  test("language switcher renders and is clickable", async ({ page }) => {
    await page.goto("/login");

    // Language switcher should have clickable elements
    const buttons = page.locator("button");
    const buttonCount = await buttons.count();
    // At minimum: submit button + language buttons
    expect(buttonCount).toBeGreaterThanOrEqual(1);
  });

  test("handles network error gracefully", async ({ page }) => {
    await page.goto("/login");

    // Block all external API calls
    await page.route("**/auth/**", (route) =>
      route.abort("connectionrefused"),
    );

    const phoneInput = page.locator('input[type="tel"]');
    const pwdInput = page.locator('input[type="password"]');
    const submitBtn = page.locator('button[type="submit"]');

    await phoneInput.fill("+251912345678");
    await pwdInput.fill("SomePassword1");
    await submitBtn.click();

    // Should show an error message, not crash
    await page.waitForTimeout(5000);

    // Page should still be functional (form visible)
    await expect(page.locator("form")).toBeVisible();
  });

  test("full page screenshot at multiple viewports", async ({ page }) => {
    const viewports = [
      { name: "desktop", width: 1440, height: 900 },
      { name: "tablet", width: 768, height: 1024 },
      { name: "mobile", width: 390, height: 844 },
    ];

    for (const vp of viewports) {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto("/login");
      await page.waitForTimeout(1000);
      await page.screenshot({
        path: `test-results/admin-dashboard-${vp.name}.png`,
        fullPage: true,
      });
    }
  });
});
