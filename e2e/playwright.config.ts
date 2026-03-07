import { defineConfig, devices } from "@playwright/test";

const WEBAPP_URL =
  process.env.WEBAPP_URL || "https://dev-app.weredaconnect.com";
const ADMIN_URL =
  process.env.ADMIN_URL || "https://dev-admin.weredaconnect.com";

export default defineConfig({
  testDir: ".",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: [["html", { open: "never" }], ["list"]],
  timeout: 60_000,

  projects: [
    {
      name: "webapp",
      testDir: "./webapp",
      use: {
        ...devices["Desktop Chrome"],
        baseURL: WEBAPP_URL,
        screenshot: "only-on-failure",
      },
    },
    {
      name: "admin",
      testDir: "./admin",
      use: {
        ...devices["Desktop Chrome"],
        baseURL: ADMIN_URL,
        screenshot: "only-on-failure",
      },
    },
  ],
});
