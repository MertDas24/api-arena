import { describe, expect, it } from "vitest";
import { safeReturnUrl } from "./url";

describe("safeReturnUrl (AUTH-10)", () => {
  it.each([
    ["/projects/abc", "/projects/abc"],
    ["%2Fruns%2F1%3Ftab%3Dlive", "/runs/1?tab=live"],
  ])("accepts same-site path %s", (input, expected) => {
    expect(safeReturnUrl(input)).toBe(expected);
  });

  it.each([
    "https://evil.example/",
    "//evil.example",
    "/\\evil.example",
    "%2F%2Fevil.example",
    "javascript:alert(1)",
    "/login",
    "/a\u0000b",
    "",
    null,
  ])("rejects %s", (input) => {
    expect(safeReturnUrl(input as string | null)).toBe("/dashboard");
  });
});
