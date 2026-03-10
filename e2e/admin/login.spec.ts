import { test, expect } from "@playwright/test";

/**
 * Admin Dashboard - Login Tests
 *
 * The admin uses a separate Cognito pool (no self-signup, MFA required).
 * Mobile test users will get "not admin" error — we test that flow too.
 * Full DOM access since this is a React app.
 */

test.describe("Admin Login Page", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/login");
  });

  test("login page renders correctly", async ({ page }) => {
    // Login form card should be visible
    await expect(page.locator("form")).toBeVisible();

    // Phone input
    const phoneInput = page.locator('input[type="tel"]');
    await expect(phoneInput).toBeVisible();
    await expect(phoneInput).toHaveAttribute("placeholder", /\+251/);

    // Password input
    const passwordInput = page.locator('input[type="password"]');
    await expect(passwordInput).toBeVisible();

    // Submit button
    const submitBtn = page.locator('button[type="submit"]');
    await expect(submitBtn).toBeVisible();
    await expect(submitBtn).toBeEnabled();
  });

  test("branding panel visible on desktop", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/login");

    // Left branding panel with "WeredaConnect" text
    const brandingText = page.locator("text=WeredaConnect");
    await expect(brandingText.first()).toBeVisible();
  });

  test("language switcher is present", async ({ page }) => {
    // LanguageSwitcher component should render
    const languageSwitcher = page.locator("text=/EN|AM|OM/i");
    const count = await languageSwitcher.count();
    expect(count).toBeGreaterThan(0);
  });

  test("phone input accepts Ethiopian phone format", async ({ page }) => {
    const phoneInput = page.locator('input[type="tel"]');
    await phoneInput.fill("+251912345678");
    await expect(phoneInput).toHaveValue("+251912345678");
  });

  test("password input masks characters", async ({ page }) => {
    const passwordInput = page.locator('input[type="password"]');
    await passwordInput.fill("TestPassword123");
    await expect(passwordInput).toHaveAttribute("type", "password");
  });

  test("submit button shows loading state", async ({ page }) => {
    const phoneInput = page.locator('input[type="tel"]');
    const passwordInput = page.locator('input[type="password"]');
    const submitBtn = page.locator('button[type="submit"]');

    await phoneInput.fill("+251900100001");
    await passwordInput.fill("TestPass1dev");

    // Click submit - button should show loading text
    await submitBtn.click();

    // Check for loading indicator (button text changes or gets disabled)
    await expect(submitBtn).toBeDisabled({ timeout: 5000 });
  });

  test("empty form shows required validation", async ({ page }) => {
    const submitBtn = page.locator('button[type="submit"]');
    const phoneInput = page.locator('input[type="tel"]');
    const passwordInput = page.locator('input[type="password"]');

    // Clear and submit
    await phoneInput.fill("");
    await passwordInput.fill("");
    await submitBtn.click();

    // HTML5 required validation should prevent submission
    // The form uses required attribute on inputs
    const isPhoneInvalid = await phoneInput.evaluate(
      (el: HTMLInputElement) => !el.validity.valid,
    );
    expect(isPhoneInvalid).toBe(true);
  });

  test("shows error on invalid credentials", async ({ page }) => {
    const phoneInput = page.locator('input[type="tel"]');
    const passwordInput = page.locator('input[type="password"]');
    const submitBtn = page.locator('button[type="submit"]');

    await phoneInput.fill("+251900000000");
    await passwordInput.fill("WrongPassword1");
    await submitBtn.click();

    // Wait for error message to appear
    const errorMsg = page.locator(".text-danger, [class*='error'], [class*='danger']");
    await expect(errorMsg).toBeVisible({ timeout: 15_000 });
  });

  test("redirects to /login when accessing protected route unauthenticated", async ({
    page,
  }) => {
    await page.goto("/");
    await page.waitForURL("**/login", { timeout: 10_000 });
    expect(page.url()).toContain("/login");
  });

  test("responsive - mobile hides branding panel", async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/login");

    // On mobile, the left branding panel (md:flex) should be hidden
    const brandingPanel = page.locator(".hidden.md\\:flex").first();
    // The panel has class "hidden md:flex", so on mobile it should not be visible
    await expect(brandingPanel).toBeHidden();

    // But the form should still be visible
    await expect(page.locator("form")).toBeVisible();
  });

  test("login page screenshot", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/login");
    await page.waitForTimeout(1000);
    await page.screenshot({ path: "test-results/admin-login-desktop.png" });

    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(500);
    await page.screenshot({ path: "test-results/admin-login-mobile.png" });
  });
});
