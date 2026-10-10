import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError, resetCsrf } from "./api";

function json(body: unknown, status = 200, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json", ...headers } });
}

describe("api client", () => {
  afterEach(() => resetCsrf());

  it("sends the CSRF token only on state-changing requests and never a token in the URL", async () => {
    const calls: Array<{ url: string; init?: RequestInit }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, init });
      if (url === "/api/auth/csrf") return json({ token: "tok-1" });
      return json({ ok: true });
    }));

    await api("/api/projects");
    await api("/api/projects", { method: "POST", body: { name: "x" } });

    const get = calls.find((c) => c.url === "/api/projects" && (c.init?.method ?? "GET") === "GET");
    const post = calls.find((c) => c.init?.method === "POST");
    expect((get?.init?.headers as Record<string, string>)["X-CSRF-TOKEN"]).toBeUndefined();
    expect((post?.init?.headers as Record<string, string>)["X-CSRF-TOKEN"]).toBe("tok-1");
    expect(calls.every((c) => !c.url.includes("tok-1"))).toBe(true);
    expect(post?.init?.credentials).toBe("same-origin");
  });

  it("refreshes the CSRF token once on csrf_failed", async () => {
    let tokenCalls = 0;
    let postCalls = 0;
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      if (url === "/api/auth/csrf") return json({ token: `tok-${++tokenCalls}` });
      postCalls++;
      return postCalls === 1 ? json({ code: "csrf_failed", title: "x" }, 403) : json({ ok: true });
    }));

    await expect(api("/api/projects", { method: "POST", body: {} })).resolves.toEqual({ ok: true });
    expect(tokenCalls).toBe(2);
  });

  it("maps ProblemDetails into ApiError with field errors and correlation id", async () => {
    vi.stubGlobal("fetch", vi.fn(async () =>
      json({ code: "validation_failed", title: "Geçersiz", correlationId: "c-1", errors: { Name: ["Zorunlu"] } }, 400)));
    const error = await api("/api/projects").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    const e = error as ApiError;
    expect(e.code).toBe("validation_failed");
    expect(e.correlationId).toBe("c-1");
    expect(e.fieldErrors.name).toEqual(["Zorunlu"]);
  });

  it("reports an unreachable API as a platform problem rather than a simulation result", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("Internal Server Error", { status: 500 })));
    const e = (await api("/api/health").catch((x: unknown) => x)) as ApiError;
    expect(e.code).toBe("api_unreachable");
  });
});
