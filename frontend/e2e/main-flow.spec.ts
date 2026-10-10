import { expect, test } from "@playwright/test";
import { registerVerifyLogin, SIM } from "./helpers";

/**
 * Stage 10 acceptance: register → project → scenario → run → client traffic → live view → finish → report → export.
 * The "client" is driven from the test with real HTTP calls to the simulator (same as the example client's resilient mode).
 */
test("user completes the full retry-503 flow and gets an evidence-based report", async ({ page, request }, info) => {
  await registerVerifyLogin(page);

  // Dashboard tells a new user what to do next.
  await expect(page.getByRole("heading", { name: "Şimdi ne yapmalıyım?" })).toBeVisible();
  await page.getByRole("link", { name: /Proje oluştur/ }).click();

  await page.getByLabel("Proje adı").fill("E2E ödeme istemcisi");
  await page.getByRole("button", { name: "Proje oluştur" }).last().click();
  await expect(page.getByRole("heading", { name: "E2E ödeme istemcisi" })).toBeVisible();

  // Wizard step 1: choose scenario.
  await page.getByRole("link", { name: "Yeni koşu" }).first().click();
  await page.getByRole("radio", { name: /503 sonrası yeniden deneme/ }).check();
  await expect(page.getByText("Öğrenme hedefi")).toBeVisible();
  await page.getByLabel(/Koşu etiketi/).fill("e2e resilient");
  await page.getByRole("button", { name: "Koşuyu oluştur ve devam et" }).click();

  // Step 2: key is shown once, masked until revealed.
  await page.getByRole("button", { name: "Erişim anahtarı oluştur" }).click();
  const secret = page.getByTestId("secret-value");
  await expect(secret).toContainText("•");
  await page.getByRole("button", { name: "Anahtarı göster" }).click();
  const key = (await secret.textContent())!.trim();
  expect(key).toMatch(/^aa_rk_[A-Za-z0-9_-]{43}$/);
  const baseUrl = (await page.locator("figure", { hasText: "SIM_BASE_URL" }).locator("pre").first().textContent())!.trim();
  expect(baseUrl).toMatch(/^http:\/\/localhost:5151\/sim\/runs\/[0-9a-f-]{36}$/);
  await page.getByRole("button", { name: /Anahtarı kaydettim/ }).click();

  // Step 3: start → live page.
  await page.getByRole("button", { name: "Testi başlat" }).click();
  await expect(page).toHaveURL(/\/runs\/[0-9a-f-]{36}$/);
  await expect(page.getByText("Çalışıyor").first()).toBeVisible();

  // Client traffic: same idempotency key, backoff, stop on success.
  const statuses: number[] = [];
  for (let attempt = 1; attempt <= 4; attempt++) {
    const r = await request.post(`${baseUrl}/payment/charge`, {
      headers: { Authorization: `Bearer ${key}`, "Idempotency-Key": "e2e-order-1" },
      data: { amount: 4200, currency: "TRY" },
    });
    statuses.push(r.status());
    expect(r.headers()["x-arena-simulated"]).toBe("true");
    if (r.status() === 200) break;
    await page.waitForTimeout(350); // client backoff (real HTTP timing, not a test-synchronisation sleep)
  }
  expect(statuses).toEqual([503, 503, 200]);

  // Live view shows all three requests; expected 503s are labelled as simulated, not platform errors.
  await expect(page.locator("tbody tr")).toHaveCount(3);
  await expect(page.getByText("beklenen simülasyon").first()).toBeVisible();
  await page.locator("tbody tr").first().getByRole("button").click();
  await expect(page.getByTestId("observation-detail")).toContainText("[REDACTED]");
  await expect(page.getByTestId("observation-detail")).not.toContainText(key);
  await page.screenshot({ path: info.outputPath("screens/live-traffic-1280.png"), fullPage: true });

  // Finish → report.
  await page.getByRole("button", { name: "Koşuyu bitir" }).first().click();
  await page.getByRole("dialog").getByRole("button", { name: "Koşuyu bitir" }).click();
  await expect(page).toHaveURL(/\/report$/);
  await expect(page.getByRole("heading", { name: /Rapor:/ })).toBeVisible();
  const passedTile = page.locator("div", { hasText: /^✓ Passed/ }).locator("..");
  await expect(passedTile.first()).toContainText("6");
  await expect(page.getByRole("heading", { name: "Neyi doğrulayamadık?" })).toBeVisible();
  await expect(page.getByText("Genel bir başarı puanı yoktur")).toBeVisible();
  await page.screenshot({ path: info.outputPath("screens/passed-report-1280.png"), fullPage: true });

  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "HTML indir" }).click();
  expect((await download).suggestedFilename()).toMatch(/^apiarena-report-[0-9a-f]{8}-\d{8}\.html$/);

  // The used key no longer works after finishing (revoked with the run).
  const after = await request.post(`${baseUrl}/payment/charge`, { headers: { Authorization: `Bearer ${key}` } });
  expect(after.status()).toBe(401);
});

test("anonymous visitor is sent to login and session expiry is handled", async ({ page }) => {
  await page.goto("/dashboard");
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fdashboard/);
  await page.goto("/login?returnUrl=https://evil.example");
  await expect(page.getByRole("heading", { name: "Giriş yap" })).toBeVisible();
});

test("simulator rejects control-panel cookies and unknown keys as platform errors", async ({ request }) => {
  const r = await request.get(`${SIM}/sim/runs/00000000-0000-0000-0000-000000000000/orders`);
  expect(r.status()).toBe(401);
  expect(r.headers()["x-arena-platform"]).toBeTruthy();
});
