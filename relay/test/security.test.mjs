import { test } from "node:test";
import assert from "node:assert/strict";
import { createLimiter, declaredTooLarge, readText, sameAdminKey, withAdminHeaders } from "../src/security.js";

test("limiters expire at the window boundary and bound remembered keys", () => {
  let time = 1_000_000;
  const limit = createLimiter(2, 60, 2, () => time);
  assert.equal(limit.consume("a"), true);
  assert.equal(limit.consume("a"), true);
  assert.equal(limit.consume("a"), false);
  assert.equal(limit.consume("b"), true);
  assert.equal(limit.consume("c"), false, "a full map fails closed");
  time += 60_001;
  assert.equal(limit.consume("a"), true);
  assert.equal(limit.consume("b"), true);
  assert.equal(limit.consume("c"), false, "the two fresh keys fill the bounded map again");
});

test("failed-auth throttling does not charge successful admin polling", () => {
  let time = 2_000_000;
  const failures = createLimiter(2, 600, 10, () => time);
  for (let i = 0; i < 2; i++) {
    assert.equal(failures.check("ip"), true);
    failures.hit("ip");
  }
  assert.equal(failures.check("ip"), false);
  time += 600_001;
  assert.equal(failures.check("ip"), true);
});

test("Content-Length is only a hint and malformed declarations fail closed", () => {
  assert.equal(declaredTooLarge(new Request("https://relay/", { method: "POST" }), 8192), false);
  assert.equal(declaredTooLarge(new Request("https://relay/", { method: "POST", headers: { "content-length": "8192" } }), 8192), false);
  assert.equal(declaredTooLarge(new Request("https://relay/", { method: "POST", headers: { "content-length": "8193" } }), 8192), true);
  assert.equal(declaredTooLarge(new Request("https://relay/", { method: "POST", headers: { "content-length": "nonsense" } }), 8192), true);
});

test("chunked bodies without Content-Length are capped and cancelled", async () => {
  let cancelled = 0;
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(new Uint8Array(5_000).fill(120));
      controller.enqueue(new Uint8Array(5_000).fill(120));
    },
    cancel() { cancelled++; },
  });
  const request = new Request("https://relay/wnf-example", { method: "POST", body, duplex: "half" });
  await assert.rejects(readText(request, 8192), (error) => error.status === 413);
  assert.equal(cancelled, 1);
});

test("the stream reader preserves UTF-8 split across chunks", async () => {
  const bytes = new TextEncoder().encode("€");
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(bytes.slice(0, 1));
      controller.enqueue(bytes.slice(1));
      controller.close();
    },
  });
  const request = new Request("https://relay/wnf-example", { method: "POST", body, duplex: "half" });
  assert.equal(await readText(request, 3), "€");
});

test("admin key comparison handles unequal lengths and Unicode", async () => {
  assert.equal(await sameAdminKey("test-admin-key", "test-admin-key"), true);
  assert.equal(await sameAdminKey("test-admin-key", "test-admin-kez"), false);
  assert.equal(await sameAdminKey("test-admin-key", "x"), false);
  assert.equal(await sameAdminKey("", "test-admin-key"), false);
  assert.equal(await sameAdminKey("schlüssel-🔑", "schlüssel-🔑"), true);
});

test("admin hardening preserves the response and adds every required header", async () => {
  const response = withAdminHeaders(new Response("not found", { status: 404, headers: { "content-type": "text/plain" } }));
  assert.equal(response.status, 404);
  assert.equal(await response.text(), "not found");
  assert.match(response.headers.get("content-security-policy"), /default-src 'none'/);
  assert.match(response.headers.get("content-security-policy"), /frame-ancestors 'none'/);
  assert.equal(response.headers.get("x-frame-options"), "DENY");
  assert.equal(response.headers.get("x-content-type-options"), "nosniff");
  assert.equal(response.headers.get("referrer-policy"), "no-referrer");
});
