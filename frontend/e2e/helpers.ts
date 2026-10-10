import { expect, type Page } from "@playwright/test";

export const SIM = "http://localhost:5151";

export function uniqueEmail(prefix = "e2e") {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@example.test`;
}

export const PASSWORD = "e2e-correct-horse-battery";

/** Register → open the verification link from the development mail sink → log in. */
export async function registerVerifyLogin(page: Page, email = uniqueEmail()) {
  await page.goto("/register");
  await page.getByLabel("E-posta").fill(email);
  await page.getByLabel("Parola").fill(PASSWORD);
  await page.getByRole("button", { name: "Kayıt ol" }).click();
  await expect(page.getByText("Kayıt isteğiniz alındı")).toBeVisible();

  await page.goto(`/dev/mailbox?to=${encodeURIComponent(email)}`);
  const link = page.getByRole("link", { name: /verify-email/ }).first();
  await expect(link).toBeVisible();
  await link.click();
  await expect(page.getByText("E-posta adresiniz doğrulandı.")).toBeVisible();

  await page.goto("/login");
  await page.getByLabel("E-posta").fill(email);
  await page.getByLabel("Parola").fill(PASSWORD);
  await page.getByRole("button", { name: "Giriş yap" }).click();
  await expect(page).toHaveURL(/\/dashboard/);
  return email;
}

/** Fails if the page scrolls horizontally at the current viewport (mobile overflow check). */
export async function expectNoHorizontalOverflow(page: Page) {
  const { overflow, offenders } = await page.evaluate(() => {
    const width = document.documentElement.clientWidth;
    const offenders = Array.from(document.querySelectorAll("body *"))
      .filter((el) => el.getBoundingClientRect().right > width + 1 && getComputedStyle(el).position !== "fixed")
      .filter((el) => !el.closest(".overflow-x-auto, .overflow-auto, pre"))
      .slice(0, 5)
      .map((el) => `${el.tagName.toLowerCase()}.${(el.getAttribute("class") ?? "").split(" ").slice(0, 4).join(".")} → ${Math.round(el.getBoundingClientRect().right)}px`);
    return { overflow: document.documentElement.scrollWidth - width, offenders };
  });
  expect(overflow, `horizontal overflow in px on ${page.url()}: ${offenders.join(" | ")}`).toBeLessThanOrEqual(1);
}
