import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { expectNoHorizontalOverflow, registerVerifyLogin } from "./helpers";

test("custom scenario editor previews with server semantics, rejects invalid input and saves versions", async ({ page }) => {
  await registerVerifyLogin(page);
  await page.goto("/scenarios");
  await page.getByRole("link", { name: /Özel senaryo/ }).click();
  await expect(page).toHaveURL(/\/scenarios\/custom\/new$/);

  await page.getByLabel("Başlık").fill("E2E sipariş hatası");
  const preview = page.getByTestId("custom-preview");
  await expect(preview).toContainText("503");
  await expect(preview.locator("li")).toHaveCount(3); // 2 steps + "repeats last step"

  await page.getByLabel(/Route/).fill("../admin");
  await expect(page.getByText("Tanım geçersiz")).toBeVisible();
  await expect(page.getByRole("button", { name: "Senaryoyu oluştur" })).toBeDisabled();
  await page.getByLabel(/Route/).fill("orders/create");
  await expect(preview).toContainText("200");

  await page.setViewportSize({ width: 360, height: 800 });
  await expectNoHorizontalOverflow(page);
  const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).analyze();
  expect(axe.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  await page.setViewportSize({ width: 1280, height: 900 });

  await page.getByRole("button", { name: "Senaryoyu oluştur" }).click();
  await expect(page).toHaveURL(/\/scenarios\/custom\/[0-9a-f-]{36}/);
  await expect(page.getByRole("heading", { name: "E2E sipariş hatası" })).toBeVisible();
  await expect(page.getByText(/sürümler: v1/)).toBeVisible();
});

test("report can be shared read-only, viewed anonymously and revoked", async ({ page, browser }) => {
  await registerVerifyLogin(page);
  const csrf = async () => (await (await page.request.get("/api/auth/csrf")).json()).token as string;
  const post = async (url: string, data: unknown) =>
    (await page.request.post(url, { data, headers: { "X-CSRF-TOKEN": await csrf(), Origin: "http://localhost:3000" } })).json();
  const project = await post("/api/projects", { name: "Paylaşım projesi (gizli ad)" });
  const run = await post(`/api/projects/${project.id}/runs`, { scenario: "rate-limit-v1", label: "gizli etiket" });
  await post(`/api/runs/${run.id}/cancel`, {});

  await page.goto(`/runs/${run.id}/report`);
  await page.getByRole("button", { name: "Paylaşım bağlantısı oluştur" }).click();
  const link = (await page.locator("code", { hasText: "/share/aa_sh_" }).textContent())!.trim();
  expect(link).toMatch(/\/share\/aa_sh_[A-Za-z0-9_-]{43}$/);

  const anonymous = await browser.newContext();
  const viewer = await anonymous.newPage();
  const response = await viewer.goto(new URL(link).pathname);
  expect(response?.headers()["x-robots-tag"]).toContain("noindex");
  await expect(viewer.getByRole("heading", { name: /Paylaşılan rapor/ })).toBeVisible();
  await expect(viewer.getByText("Paylaşım projesi (gizli ad)")).toHaveCount(0);
  await expect(viewer.getByText("gizli etiket")).toHaveCount(0);
  await viewer.setViewportSize({ width: 360, height: 800 });
  await expectNoHorizontalOverflow(viewer);

  await page.getByRole("button", { name: "İptal et" }).first().click();
  await page.getByRole("dialog").getByRole("button", { name: "İptal et" }).click();
  await viewer.reload();
  await expect(viewer.getByText("Paylaşım bulunamadı")).toBeVisible();
  await anonymous.close();
});

test("webhook screen states clearly that the module is disabled (default) and is accessible on mobile", async ({ page }) => {
  await registerVerifyLogin(page);
  const csrf = (await (await page.request.get("/api/auth/csrf")).json()).token as string;
  const project = await (await page.request.post("/api/projects", { data: { name: "Webhook ekranı" }, headers: { "X-CSRF-TOKEN": csrf, Origin: "http://localhost:3000" } })).json();
  await page.setViewportSize({ width: 360, height: 800 });
  await page.goto(`/projects/${project.id}/webhooks`);
  await expect(page.getByText("Webhook modülü bu ortamda kapalı")).toBeVisible();
  await expectNoHorizontalOverflow(page);
  const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).analyze();
  expect(axe.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  const features = await (await page.request.get("/api/features")).json();
  expect(features.webhooks).toBe(false);
});
