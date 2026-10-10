import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { expectNoHorizontalOverflow, registerVerifyLogin } from "./helpers";

const widths = [360, 390, 768, 1440];

/** Creates a project, a cancelled run (→ report) and a running run through the same-origin API, like the UI does. */
async function seed(page: Page) {
  const csrf = async () => (await (await page.request.get("/api/auth/csrf")).json()).token as string;
  const post = async (url: string, data: unknown) =>
    (await page.request.post(url, { data, headers: { "X-CSRF-TOKEN": await csrf(), Origin: "http://localhost:3000" } })).json();
  const project = await post("/api/projects", { name: "Responsive QA projesi" });
  const finished = await post(`/api/projects/${project.id}/runs`, { scenario: "rate-limit-v1", label: "qa" });
  await post(`/api/runs/${finished.id}/cancel`, {});
  const live = await post(`/api/projects/${project.id}/runs`, { scenario: "timeout-v1" });
  await post(`/api/runs/${live.id}/access-keys`, {});
  await post(`/api/runs/${live.id}/start`, {});
  return { projectId: project.id as string, reportRun: finished.id as string, liveRun: live.id as string };
}

test("key screens have no horizontal overflow at 360/390/768/1440 and pass axe (serious/critical)", async ({ page }, info) => {
  await registerVerifyLogin(page);
  const ids = await seed(page);
  const screens: Record<string, string> = {
    landing: "/",
    dashboard: "/dashboard",
    projects: "/projects",
    project: `/projects/${ids.projectId}`,
    catalog: "/scenarios",
    wizard: `/projects/${ids.projectId}/runs/new`,
    live: `/runs/${ids.liveRun}`,
    report: `/runs/${ids.reportRun}/report`,
    account: "/account",
  };

  for (const width of widths) {
    await page.setViewportSize({ width, height: width < 768 ? 800 : 900 });
    for (const [name, url] of Object.entries(screens)) {
      await page.goto(url);
      await expect(page.locator("h1").first()).toBeVisible();
      await expectNoHorizontalOverflow(page);
      await page.screenshot({ path: info.outputPath(`screens/${name}-${width}.png`), fullPage: true });
      if (width === 390 || width === 1440) {
        const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).analyze();
        const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
        expect(serious.map((v) => `${name}@${width}: ${v.id} ${v.nodes.map((n) => `${n.target.join(" ")} ${n.any[0]?.message ?? ""}`).join(" | ")}`)).toEqual([]);
      }
    }
  }
});

test("keyboard users can reach the main navigation and skip link", async ({ page }) => {
  await registerVerifyLogin(page);
  await page.goto("/dashboard");
  await page.keyboard.press("Tab");
  await expect(page.getByRole("link", { name: "İçeriğe geç" })).toBeFocused();
});

test.describe("dark colour scheme", () => {
  test.use({ colorScheme: "dark" });

  test("key screens pass axe contrast checks in the dark theme too", async ({ page }) => {
    await registerVerifyLogin(page);
    const ids = await seed(page);
    await page.setViewportSize({ width: 390, height: 844 });
    for (const url of ["/", "/dashboard", "/projects", `/projects/${ids.projectId}`, "/scenarios", `/runs/${ids.liveRun}`, `/runs/${ids.reportRun}/report`, "/account"]) {
      await page.goto(url);
      await expect(page.locator("h1").first()).toBeVisible();
      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).analyze();
      const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
      expect(serious.map((v) => `${url}: ${v.id}`)).toEqual([]);
    }
  });
});
