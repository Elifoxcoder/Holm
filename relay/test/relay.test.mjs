// Runs the relay locally (wrangler dev: the real workerd runtime with local Durable Objects and SQLite) and checks it
// the way Holm uses it. `npm test` in relay/.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { isNoteFor, parseSince } from "../src/notes.js";

const PORT = 8799;
const BASE = `http://127.0.0.1:${PORT}`;
let dev, state;
const tempRoot = path.join(os.tmpdir(), "opencode");
fs.mkdirSync(tempRoot, { recursive: true });
state = fs.mkdtempSync(path.join(tempRoot, "holm-relay-"));

const letters = "abcdefghijklmnopqrstuvwxyz234567";
const topic = (prefix, n) => prefix + Array.from(crypto.getRandomValues(new Uint8Array(n)), (b) => letters[b & 31]).join("");
const inbox = () => topic("wnf-", 26);
const invite = () => topic("holm-inv-", 20);
const inboxAt = (n) => {
  let value = "";
  do { value = letters[n & 31] + value; n = Math.floor(n / 32); } while (n);
  return "wnf-" + value.padStart(26, "a");
};
const note = (body = "B".repeat(120)) => `wnf1 ${"A".repeat(91)} ${body}`;
const card = () => `hinv1 ${"C".repeat(80)}`;
// Each run is its own address to the per-address limit, so runs right after one another don't share a minute.
const ip = `10.${[0, 0, 0].map(() => Math.floor(Math.random() * 250)).join(".")}`;
const post = (t, text, headers = {}) => fetch(`${BASE}/${t}`, { method: "POST", body: text, headers: { "cf-connecting-ip": ip, ...headers } });
const poll = async (t, since = "all") => (await (await fetch(`${BASE}/${t}/json?poll=1&since=${since}`)).text())
  .split("\n").filter(Boolean).map((l) => JSON.parse(l));
const assertAdminHeaders = (response) => {
  assert.match(response.headers.get("content-security-policy") ?? "", /default-src 'none'/);
  assert.match(response.headers.get("content-security-policy") ?? "", /frame-ancestors 'none'/);
  assert.equal(response.headers.get("x-frame-options"), "DENY");
  assert.equal(response.headers.get("x-content-type-options"), "nosniff");
  assert.equal(response.headers.get("referrer-policy"), "no-referrer");
};

before(async () => {
  dev = spawn(`npx wrangler dev --port ${PORT} --ip 127.0.0.1 --persist-to "${state}" --show-interactive-dev-session=false --var ADMIN_KEY:test-admin-key`,
              { cwd: fileURLToPath(new URL("..", import.meta.url)), shell: true, stdio: ["ignore", "pipe", "pipe"] });
  await new Promise((resolve, reject) => {
    let output = "";
    const fail = (message) => reject(new Error(`${message}${output ? `\n${output.slice(-8_000)}` : ""}`));
    const timer = setTimeout(() => fail("wrangler dev didn't start"), 60_000);
    const look = (d) => {
      output += String(d);
      if (/Ready on/.test(String(d))) { clearTimeout(timer); resolve(); }
    };
    dev.stdout.on("data", look);
    dev.stderr.on("data", look);
    dev.once("error", (e) => { clearTimeout(timer); fail(e.message); });
    dev.once("exit", (code) => { clearTimeout(timer); fail(`wrangler dev exited (${code})`); });
  });
});

after(() => {
  if (dev) {
    if (process.platform === "win32") spawnSync("taskkill", ["/pid", String(dev.pid), "/t", "/f"], { stdio: "ignore" });
    else dev.kill();
  }
  try { fs.rmSync(state, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); } catch { /* a closing runtime may hold it briefly */ }
});

test("only Holm notes and Holm inboxes", async () => {
  assert.ok(isNoteFor(inbox(), note()));
  assert.ok(!isNoteFor(inbox(), card()));
  assert.ok(!isNoteFor(invite(), note()));
  assert.ok(!isNoteFor(inbox(), note("B".repeat(5000))));
  assert.equal((await post(inbox(), "hello")).status, 400);
  assert.equal((await post("my-topic", note())).status, 404);
  assert.equal((await post(inbox(), note("B".repeat(5000)))).status, 400);
  assert.equal((await post(inbox(), note("B".repeat(9000)))).status, 413);
});

test("chunked note bodies are capped without Content-Length", { timeout: 15_000 }, async () => {
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(new Uint8Array(5_000).fill(120));
      controller.enqueue(new Uint8Array(5_000).fill(120));
      controller.close();
    },
  });
  const response = await fetch(`${BASE}/${inbox()}`, {
    method: "POST", body, duplex: "half", headers: { "cf-connecting-ip": "192.0.2.51" },
  });
  assert.equal(response.status, 413);
  assert.deepEqual(await response.json(), { code: 413, error: "note too large" });
});

test("GET routes have a per-IP rate limit", { timeout: 60_000 }, async () => {
  const headers = { "cf-connecting-ip": "192.0.2.52" };
  for (let i = 0; i < 300; i++) {
    const response = await fetch(`${BASE}/`, { headers });
    assert.equal(response.status, 200, `request ${i + 1}`);
    await response.arrayBuffer();
  }
  const limited = await fetch(`${BASE}/`, { headers });
  assert.equal(limited.status, 429);
  assert.deepEqual(await limited.json(), { code: 429, error: "too many requests, wait a minute" });
  const adminLimited = await fetch(`${BASE}/admin/not-found`, { headers });
  assert.equal(adminLimited.status, 429);
  assertAdminHeaders(adminLimited);
  await adminLimited.arrayBuffer();
});

test("since as ntfy reads it", () => {
  assert.equal(parseSince("all"), 0);
  assert.equal(parseSince("1700000000"), 1700000000);
  assert.equal(parseSince("12h", 100000), 100000 - 43200);
  assert.equal(parseSince(""), null);
  assert.equal(parseSince("abc"), null);
});

test("a note is kept and found by polling, as ntfy answers", async () => {
  const t = inbox();
  const sent = await post(t, note());
  assert.equal(sent.status, 200);
  const body = await sent.json();
  assert.equal(body.event, "message");
  const kept = await poll(t);
  assert.equal(kept.length, 1);
  assert.equal(kept[0].message, note());
  assert.equal(kept[0].event, "message");
  assert.equal(kept[0].id, body.id);
  assert.deepEqual(await poll(t, String(body.time + 1)), []);
  assert.equal((await fetch(`${BASE}/${t}/json`)).status, 400, "no ntfy streaming: Holm follows with /ws");
});

test("an invite card waits at its code's inbox", async () => {
  const t = invite();
  assert.equal((await post(t, card())).status, 200);
  assert.equal((await poll(t, "12h"))[0].message, card());
  assert.deepEqual(await poll(invite(), "12h"), []);
});

test("Cache: no is delivered live but not kept", async () => {
  const t = inbox();
  assert.equal((await post(t, note("P".repeat(60)), { "X-Cache": "no" })).status, 200);
  assert.deepEqual(await poll(t), []);
});

function follow(t, since) {
  const ws = new WebSocket(`ws://127.0.0.1:${PORT}/${t}/ws${since != null ? `?since=${since}` : ""}`);
  const events = [];
  const waiters = [];
  ws.addEventListener("message", (e) => { events.push(JSON.parse(e.data)); waiters.splice(0).forEach((w) => w()); });
  const until = async (n) => {
    const deadline = Date.now() + 10_000;
    while (events.length < n) {
      if (Date.now() > deadline) throw new Error(`only ${events.length} of ${n} events`);
      await new Promise((r) => { waiters.push(r); setTimeout(r, 200); });
    }
    return events;
  };
  const opened = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("WebSocket open timed out")), 10_000);
    ws.addEventListener("open", () => { clearTimeout(timer); resolve(); }, { once: true });
    ws.addEventListener("error", (e) => { clearTimeout(timer); reject(e); }, { once: true });
  });
  return { ws, events, until, opened };
}

test("a follower gets the open event, what was kept, then new notes live", async () => {
  const t = inbox();
  await post(t, note("1".repeat(40)));
  const f = follow(t, "all");
  await f.opened;
  const [open, first] = await f.until(2);
  assert.equal(open.event, "open");
  assert.ok(open.time > 1_700_000_000);
  assert.equal(first.message, note("1".repeat(40)));
  await post(t, note("2".repeat(40)));
  await post(t, note("3".repeat(40)), { Cache: "no" }); // presence: live only
  const events = await f.until(4);
  assert.deepEqual(events.slice(2).map((e) => e.message), [note("2".repeat(40)), note("3".repeat(40))]);
  f.ws.close();
});

test("without since a follower gets only new notes", async () => {
  const t = inbox();
  await post(t, note("o".repeat(40)));
  const f = follow(t);
  await f.opened;
  await f.until(1);
  await post(t, note("n".repeat(40)));
  const events = await f.until(2);
  assert.equal(events[1].message, note("n".repeat(40)));
  assert.equal(events.length, 2);
  f.ws.close();
});

test("a ninth follower is refused while the established eight keep receiving", { timeout: 30_000 }, async () => {
  const t = inbox(), followers = [];
  let extra;
  try {
    for (let i = 0; i < 8; i++) {
      const f = follow(t);
      followers.push(f);
      await f.opened;
      await f.until(1);
    }
    extra = follow(t);
    extra.opened.catch(() => { /* the newcomer is expected to be refused */ });
    const refused = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("follower refusal timed out")), 5_000);
      extra.ws.addEventListener("error", () => { clearTimeout(timer); resolve(true); }, { once: true });
      extra.ws.addEventListener("open", () => { clearTimeout(timer); resolve(false); }, { once: true });
    });
    assert.equal(refused, true, "the newcomer must not get a WebSocket");
    assert.ok(followers.every((f) => f.ws.readyState === WebSocket.OPEN));
    assert.equal((await post(t, note("S".repeat(40)))).status, 200);
    await Promise.all(followers.map((f) => f.until(2)));
  } finally {
    extra?.ws.close();
    for (const f of followers) f.ws.close();
  }
});

test("an inbox takes 60 notes a minute", async () => {
  const t = inbox();
  const statuses = [];
  for (let i = 0; i < 61; i++) {
    statuses.push((await post(t, note(String(i).padStart(40, "x")), { "cf-connecting-ip": `198.51.100.${i + 1}` })).status);
  }
  assert.equal(statuses.filter((s) => s === 200).length, 60, statuses.join(","));
  assert.equal(statuses.at(-1), 429);
});

test("one address cannot flood another inbox", async () => {
  const t = inbox(), sender = "192.0.2.61";
  const statuses = [];
  for (let i = 0; i < 21; i++) statuses.push((await post(t, note(), { "cf-connecting-ip": sender, cache: "no" })).status);
  assert.equal(statuses.filter((s) => s === 200).length, 20, statuses.join(","));
  assert.equal(statuses.at(-1), 429);
});

test("new inbox creation is bounded per address", async () => {
  const sender = "192.0.2.62", statuses = [];
  for (let i = 0; i < 21; i++) statuses.push((await post(inboxAt(400 + i), note(), { "cf-connecting-ip": sender })).status);
  assert.equal(statuses.filter((s) => s === 200).length, 20, statuses.join(","));
  assert.equal(statuses.at(-1), 429);
});

test("only a WebSocket follows", async () => {
  assert.equal((await fetch(`${BASE}/${inbox()}/ws`)).status, 426);
  assert.equal((await fetch(`${BASE}/${inbox()}/other`)).status, 405);
});

test("the admin page is open, its numbers need the key", { timeout: 20_000 }, async () => {
  const page = await fetch(`${BASE}/admin`);
  assert.equal(page.status, 200);
  assertAdminHeaders(page);
  assert.match(await page.text(), /<title>Holm relay<\/title>/);
  const unauthenticated = await fetch(`${BASE}/admin/stats`);
  assert.equal(unauthenticated.status, 401);
  assertAdminHeaders(unauthenticated);
  await unauthenticated.arrayBuffer();
  const wrong = await fetch(`${BASE}/admin/stats`, { headers: { authorization: "Bearer wrong" } });
  assert.equal(wrong.status, 401);
  assertAdminHeaders(wrong);
  await wrong.arrayBuffer();

  const attempts = { "cf-connecting-ip": "192.0.2.53" };
  for (let i = 0; i < 5; i++) {
    const response = await fetch(`${BASE}/admin/stats`, { headers: { ...attempts, authorization: `Bearer wrong-${i}` } });
    assert.equal(response.status, 401);
    await response.arrayBuffer();
  }
  const limited = await fetch(`${BASE}/admin/stats`, { headers: { ...attempts, authorization: "Bearer test-admin-key" } });
  assert.equal(limited.status, 429);
  assert.equal(limited.headers.get("retry-after"), "600");
  assertAdminHeaders(limited);
  await limited.arrayBuffer();

  // The right key gets past the door; locally there is no API token to read the counts with.
  const stats = await fetch(`${BASE}/admin/stats`, { headers: { authorization: "Bearer test-admin-key" } });
  assert.equal(stats.status, 503);
  assertAdminHeaders(stats);
  assert.match((await stats.json()).error, /CF_API_TOKEN/);
  const missing = await fetch(`${BASE}/admin/other`);
  assert.equal(missing.status, 404);
  assertAdminHeaders(missing);
  await missing.arrayBuffer();
});
