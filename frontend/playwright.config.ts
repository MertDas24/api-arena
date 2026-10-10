import { defineConfig, devices } from "@playwright/test";

/**
 * E2E against the real stack: control API (:5150) + simulator (:5151) + Next.js (:3000) on a dedicated database.
 * E2E_SQL_CONNECTION overrides the default local SQL Server (CI passes its service container here).
 * Run through scripts/e2e.(ps1|sh): it builds backend + frontend and applies migrations before the hosts start.
 */
const sql =
  process.env.E2E_SQL_CONNECTION ?? "Server=localhost;Database=ApiArena_E2E;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";
const backendEnv = {
  ASPNETCORE_ENVIRONMENT: "Development",
  ConnectionStrings__ApiArena: sql,
  ApiArena__Email__FileDirectory: "../../../.local/e2e-mail",
  ApiArena__Auth__AuthPermitsPerMinute: "1000",
  ApiArena__Auth__SensitivePermitsPer15Minutes: "1000",
};

export default defineConfig({
  testDir: "./e2e",
  timeout: 90_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]],
  use: {
    baseURL: "http://localhost:3000",
    // Traces contain DOM snapshots (e.g. a revealed test run key); keep them local only, never in CI artefacts.
    trace: process.env.CI ? "off" : "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: [
    {
      command: "dotnet run --project ../backend/src/APIArena.Api --no-build --no-launch-profile --urls http://localhost:5150",
      url: "http://localhost:5150/health/ready",
      env: backendEnv,
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
    },
    {
      command: "dotnet run --project ../backend/src/APIArena.Simulator --no-build --no-launch-profile --urls http://localhost:5151",
      url: "http://localhost:5151/health/ready",
      env: backendEnv,
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
    },
    {
      command: "npm run start",
      url: "http://localhost:3000/login",
      env: { API_INTERNAL_URL: "http://localhost:5150" },
      reuseExistingServer: !process.env.CI,
      timeout: 300_000,
    },
  ],
});
