// Starts the server on free ports with an empty database and checks what the apps rely on.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import net from "node:net";
import http from "node:http";
import os from "node:os";
import fs from "node:fs";
import path from "node:path";
import WebSocket from "ws";
import { DatabaseSync } from "node:sqlite";

const HTTP = 18480 + Math.floor(Math.random() * 500), TRANSIT = 14001 + Math.floor(Math.random() * 500);
const base = `http://127.0.0.1:${HTTP}`;
const docs = `${base}/v1/projects/holynotch/databases/(default)/documents`;
let child, dir;

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "holynotch-"));
  const seed = new DatabaseSync(path.join(dir, "holynotch.db"));
  const minute = Math.floor(Date.now() / 60_000) * 60;
  seed.exec(`
    CREATE TABLE events (time INTEGER NOT NULL, kind TEXT NOT NULL, inbox TEXT NOT NULL DEFAULT '', n INTEGER NOT NULL DEFAULT 1);
    CREATE INDEX events_time ON events (time);
    INSERT INTO events (time, kind, inbox, n) VALUES (${minute + 1}, 'poll', 'aaaaaaaaaaaa', 2), (${minute + 2}, 'poll', 'bbbbbbbbbbbb', 3);
  `);
  seed.close();
  child = spawn(process.execPath, [new URL("../holynotch-server.mjs", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1")], {
    env: { ...process.env, HTTP_PORT: String(HTTP), TRANSIT_PORT: String(TRANSIT), DATA_DIR: dir, ADMIN_KEY: "test-admin-key",
           MAX_FOLLOWERS: "16", FOLLOWERS_PER_IP: "8", MAX_WAITING: "4", WAITING_PER_IP: "2",
           MAX_TRANSIT_ACTIVE: "3", TRANSIT_ACTIVE_PER_IP: "2", MAX_OPEN_INBOXES_PER_DAY: "4" },
    stdio: ["ignore", "pipe", "inherit"],
  });
  await new Promise((resolve) => child.stdout.on("data", (d) => { if (String(d).includes("Transit relay")) resolve(); }));
});
after(() => { child.kill(); try { fs.rmSync(dir, { recursive: true, force: true }); } catch { /* Windows keeps the db a moment */ } });

const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
const sealed = (n = 60) => "v1." + "A".repeat(n);
const ipHeaders = (ip) => ({ "x-real-ip": ip });
const alphabet = "abcdefghijklmnopqrstuvwxyz234567";
const numberedInbox = (n) => {
  let value = "";
  do { value = alphabet[n % 32] + value; n = Math.floor(n / 32); } while (n);
  return "wnf-" + value.padStart(26, "a");
};

/** No Content-Length: Node sends these writes with chunked transfer encoding. */
function chunkedPost(pathname, chunks, headers = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request(base + pathname, { method: "POST", headers: { "transfer-encoding": "chunked", ...headers } }, (res) => {
      let body = "";
      res.setEncoding("utf8");
      res.on("data", (d) => { body += d; });
      res.on("end", () => resolve({ status: res.statusCode, headers: res.headers, body }));
    });
    req.on("error", reject);
    req.setTimeout(5_000, () => req.destroy(new Error("chunked request timed out")));
    for (const chunk of chunks) req.write(chunk);
    req.end();
  });
}

function rawGet(pathname, headers = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request({ hostname: "127.0.0.1", port: HTTP, path: pathname, headers }, (res) => {
      let body = "";
      res.setEncoding("utf8");
      res.on("data", (d) => { body += d; });
      res.on("end", () => resolve({ status: res.statusCode, headers: res.headers, body }));
    });
    req.on("error", reject);
    req.setTimeout(5_000, () => req.destroy(new Error("request timed out")));
    req.end();
  });
}

function assertAdminHeaders(response) {
  assert.match(response.headers.get("content-security-policy") ?? "", /default-src 'none'/);
  assert.match(response.headers.get("content-security-policy") ?? "", /frame-ancestors 'none'/);
  assert.equal(response.headers.get("x-frame-options"), "DENY");
  assert.equal(response.headers.get("x-content-type-options"), "nosniff");
  assert.equal(response.headers.get("referrer-policy"), "no-referrer");
}

test("event rows migrate to bounded minute buckets and GET-only inboxes create no notes", { timeout: 20_000 }, async () => {
  const database = new DatabaseSync(path.join(dir, "holynotch.db"));
  try {
    assert.ok(database.prepare("SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'events_bucket'").get());
    const seeded = database.prepare("SELECT n FROM events WHERE kind = 'poll' AND inbox = ''").get();
    assert.equal(seeded.n, 5, "legacy duplicate rows were summed into one bucket");
    const before = database.prepare("SELECT count(*) AS n FROM events").get().n;

    for (let i = 0; i < 25; i++) {
      const response = await fetch(`${base}/${numberedInbox(50 + i)}/json?poll=1&since=all`);
      assert.equal(response.status, 200);
      await response.arrayBuffer();
    }
    const ws = new WebSocket(`ws://127.0.0.1:${HTTP}/${numberedInbox(100)}/ws`);
    await new Promise((resolve, reject) => { ws.once("open", resolve); ws.once("error", reject); });
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("GET-only follower close timed out")), 5_000);
      ws.once("close", () => { clearTimeout(timer); resolve(); });
      ws.close();
    });

    const after = database.prepare("SELECT count(*) AS n FROM events").get().n;
    assert.ok(after <= before + 1, "poll requests upsert one bucket instead of adding one row each");
    assert.equal(database.prepare("SELECT count(*) AS n FROM events WHERE kind = 'poll' AND inbox != ''").get().n, 0);
    assert.equal(database.prepare("SELECT count(*) AS n FROM notes").get().n, 0, "GET and WebSocket reads inserted no note state");
  } finally {
    database.close();
  }
});

test("open-event inbox cardinality is capped daily while minute counts are preserved", { timeout: 20_000 }, async () => {
  const database = new DatabaseSync(path.join(dir, "holynotch.db"));
  const openClose = async (topic) => {
    const ws = new WebSocket(`ws://127.0.0.1:${HTTP}/${topic}/ws`);
    await new Promise((resolve, reject) => { ws.once("open", resolve); ws.once("error", reject); });
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("follower close timed out")), 5_000);
      ws.once("close", () => { clearTimeout(timer); resolve(); });
      ws.close();
    });
  };
  try {
    const beforeRows = database.prepare("SELECT count(*) AS n FROM events").get().n;
    const beforeOpens = database.prepare("SELECT COALESCE(SUM(n), 0) AS n FROM events WHERE kind = 'open'").get().n;
    for (let i = 0; i < 12; i++) await openClose(numberedInbox(130 + i));
    await openClose(numberedInbox(130)); // a reconnect must not create another identified row for this inbox today
    const afterRows = database.prepare("SELECT count(*) AS n FROM events").get().n;
    const afterOpens = database.prepare("SELECT COALESCE(SUM(n), 0) AS n FROM events WHERE kind = 'open'").get().n;
    const identified = database.prepare("SELECT count(DISTINCT inbox) AS n FROM events WHERE kind = 'open' AND inbox != ''").get().n;
    assert.equal(afterOpens, beforeOpens + 13, "all opens are still counted");
    assert.ok(identified <= 4, "the test's daily inbox cap is enforced");
    assert.ok(afterRows <= beforeRows + 6, "reconnects upsert a few buckets instead of adding one row per open");
  } finally {
    database.close();
  }
});

test("an account: sign up, sign in, refresh, documents with preconditions, delete", async () => {
  let r = await post(`${base}/v1/accounts:signUp?key=x`, { email: "Lena@Example.ch", password: "secret-from-the-app", returnSecureToken: true });
  assert.equal(r.status, 200);
  const up = await r.json();
  assert.match(up.localId, /^[A-Za-z0-9]{28}$/);
  assert.equal(up.expiresIn, "3600");

  r = await post(`${base}/v1/accounts:signUp`, { email: "lena@example.ch", password: "another-secret" });
  assert.equal((await r.json()).error.message, "EMAIL_EXISTS");
  r = await post(`${base}/v1/accounts:signInWithPassword`, { email: "lena@example.ch", password: "wrong" });
  assert.equal((await r.json()).error.message, "INVALID_LOGIN_CREDENTIALS");

  r = await post(`${base}/v1/accounts:signInWithPassword`, { email: "lena@example.ch", password: "secret-from-the-app" });
  const s = await r.json();
  assert.equal(s.localId, up.localId);
  const auth = { authorization: `Bearer ${s.idToken}`, "content-type": "application/json" };
  const user = `${docs}/users/${s.localId}`;

  r = await fetch(user, { headers: auth });
  assert.equal(r.status, 404);
  assert.equal((await r.json()).error.status, "NOT_FOUND");

  const put = (url, value, pre) => fetch(`${url}?${pre}`, { method: "PATCH", headers: auth, body: JSON.stringify({ fields: { [url.includes("/sync/") ? "data" : "vault"]: { stringValue: value } } }) });
  r = await put(user, sealed(), "currentDocument.exists=false");
  assert.equal(r.status, 200);
  const first = await r.json();
  r = await put(user, sealed(), "currentDocument.exists=false");
  assert.equal((await r.json()).error.status, "ALREADY_EXISTS");
  r = await put(user, sealed(70), "currentDocument.updateTime=" + encodeURIComponent(first.updateTime));
  assert.equal(r.status, 200);
  r = await put(user, sealed(), "currentDocument.updateTime=" + encodeURIComponent(first.updateTime));
  assert.equal((await r.json()).error.status, "FAILED_PRECONDITION");
  r = await fetch(user, { headers: auth });
  assert.equal((await r.json()).fields.vault.stringValue, sealed(70));

  // The rules: sealed text only, known sections, own documents.
  assert.equal((await put(`${user}/sync/settings`, "plain text", "currentDocument.exists=false")).status, 403);
  assert.equal((await put(`${user}/sync/secrets`, sealed(), "currentDocument.exists=false")).status, 403);
  assert.equal((await put(`${user}/sync/settings`, sealed(500), "currentDocument.exists=false")).status, 200);
  assert.equal((await fetch(`${docs}/users/someoneElse123`, { headers: auth })).status, 403);
  assert.equal((await fetch(user)).status, 401);

  r = await fetch(`${base}/v1/token?key=x`, { method: "POST", headers: { "content-type": "application/x-www-form-urlencoded" }, body: `grant_type=refresh_token&refresh_token=${encodeURIComponent(s.refreshToken)}` });
  const fresh = await r.json();
  assert.equal(fresh.user_id, s.localId);
  assert.equal((await fetch(user, { headers: { authorization: `Bearer ${fresh.id_token}` } })).status, 200);

  assert.equal((await fetch(`${user}/sync/settings`, { method: "DELETE", headers: auth })).status, 200);
  r = await post(`${base}/v1/accounts:delete`, { idToken: s.idToken });
  assert.equal(r.status, 200);
  r = await post(`${base}/v1/accounts:signInWithPassword`, { email: "lena@example.ch", password: "secret-from-the-app" });
  assert.equal((await r.json()).error.message, "INVALID_LOGIN_CREDENTIALS");
});

const inbox = "wnf-" + "abcdefghijklmnopqrstuvwxyz".slice(0, 26);
const note = "wnf1 " + "B".repeat(60) + " " + "C".repeat(40);

test("an inbox: notes kept and polled, followed live over a WebSocket, nothing else taken", async () => {
  let r = await fetch(`${base}/${inbox}`, { method: "POST", body: note });
  assert.equal(r.status, 200);
  r = await fetch(`${base}/${inbox}`, { method: "POST", body: "hello" });
  assert.equal(r.status, 400);
  r = await fetch(`${base}/not-an-inbox`, { method: "POST", body: note });
  assert.equal(r.status, 404);

  r = await fetch(`${base}/${inbox}/json?poll=1&since=all`);
  const lines = (await r.text()).trim().split("\n").map((l) => JSON.parse(l));
  assert.equal(lines.length, 1);
  assert.equal(lines[0].message, note);

  const ws = new WebSocket(`ws://127.0.0.1:${HTTP}/${inbox}/ws?since=all`);
  const got = [];
  await new Promise((resolve) => ws.on("message", (m) => { got.push(JSON.parse(String(m))); if (got.length === 2) resolve(); }));
  assert.deepEqual(got.map((g) => g.event), ["open", "message"]);
  const live = new Promise((resolve) => ws.once("message", (m) => resolve(JSON.parse(String(m)))));
  await fetch(`${base}/${inbox}`, { method: "POST", body: note, headers: { cache: "no" } });
  assert.equal((await live).message, note);
  ws.close();
  r = await fetch(`${base}/${inbox}/json?poll=1&since=all`);
  assert.equal((await r.text()).trim().split("\n").length, 1, "a presence note isn't kept");
});

test("the transit relay pairs two sides with the same token and pipes their bytes", async () => {
  const token = "ab".repeat(32);
  const open = (side) => new Promise((resolve) => {
    const s = net.connect(TRANSIT, "127.0.0.1", () => s.write(`please relay ${token} for side ${side}\n`));
    s.once("data", (d) => resolve({ s, first: String(d) }));
  });
  const [a, b] = await Promise.all([open("aaaa"), open("bbbb")]);
  assert.equal(a.first, "ok\n");
  assert.equal(b.first, "ok\n");
  const echo = new Promise((resolve) => b.s.once("data", (d) => resolve(String(d))));
  a.s.write("encrypted bytes");
  assert.equal(await echo, "encrypted bytes");
  // The receiver confirms and closes at once: the confirmation still reaches the sender.
  const last = new Promise((resolve) => { let got = ""; a.s.on("data", (d) => { got += d; }); a.s.on("end", () => resolve(got)); });
  b.s.end("received");
  assert.equal(await last, "received");
  a.s.destroy(); b.s.destroy();
});

test("the admin numbers need the key", async () => {
  assert.equal((await fetch(`${base}/admin/stats`)).status, 401);
  const r = await fetch(`${base}/admin/stats`, { headers: { authorization: "Bearer test-admin-key" } });
  const s = await r.json();
  assert.equal(typeof s.server.accounts, "number");
  assert.ok(s.last24h.note >= 1);
  assert.ok(s.last24h.transfer >= 1);
  assert.equal((await fetch(`${base}/admin`)).status, 200);
});

test("chunked account and inbox bodies are rejected by streamed byte limits", { timeout: 15_000 }, async () => {
  const headers = ipHeaders("192.0.2.41");
  const account = await chunkedPost("/v1/accounts:signUp", [Buffer.alloc(5_000, "x"), Buffer.alloc(5_000, "x"),
    Buffer.alloc(5_000, "x"), Buffer.alloc(5_000, "x")], headers);
  assert.equal(account.status, 413);
  assert.equal(JSON.parse(account.body).error.message, "REQUEST_TOO_LARGE");

  const inbox = await chunkedPost(`/${numberedInbox(200)}`, [Buffer.alloc(5_000, "x"), Buffer.alloc(5_000, "x")], headers);
  assert.equal(inbox.status, 413);
  assert.deepEqual(JSON.parse(inbox.body), { code: 413, error: "note too large" });
  assert.equal((await fetch(`${base}/health`, { headers })).status, 200, "the server still serves requests");
});

test("GET routes have a per-IP rate limit", { timeout: 30_000 }, async () => {
  const headers = ipHeaders("192.0.2.42");
  for (let i = 0; i < 300; i++) {
    const response = await fetch(`${base}/health`, { headers });
    assert.equal(response.status, 200, `request ${i + 1}`);
    await response.arrayBuffer();
  }
  const limited = await fetch(`${base}/health`, { headers });
  assert.equal(limited.status, 429);
  assert.deepEqual(await limited.json(), { code: 429, error: "too many requests, wait a minute" });
  const adminLimited = await fetch(`${base}/admin/not-found`, { headers });
  assert.equal(adminLimited.status, 429);
  assertAdminHeaders(adminLimited);
  await adminLimited.arrayBuffer();
});

test("a ninth follower is rejected without disconnecting the established eight", { timeout: 20_000 }, async () => {
  const topic = numberedInbox(201), headers = ipHeaders("192.0.2.43");
  const followers = [];
  let extra;
  try {
    for (let i = 0; i < 8; i++) {
      const ws = new WebSocket(`ws://127.0.0.1:${HTTP}/${topic}/ws`, { headers });
      followers.push(ws);
      await new Promise((resolve, reject) => { ws.once("open", resolve); ws.once("error", reject); });
    }
    extra = new WebSocket(`ws://127.0.0.1:${HTTP}/${topic}/ws`, { headers });
    extra.on("error", () => { /* the expected refusal also closes the client */ });
    const status = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("follower refusal timed out")), 5_000);
      extra.once("unexpected-response", (_request, response) => {
        clearTimeout(timer);
        response.resume();
        resolve(response.statusCode);
      });
      extra.once("open", () => { clearTimeout(timer); resolve(101); });
    });
    assert.equal(status, 503);

    const received = followers.map((ws) => new Promise((resolve, reject) => {
      ws.once("message", resolve);
      ws.once("error", reject);
    }));
    const response = await fetch(`${base}/${topic}`, { method: "POST", headers: { ...headers, cache: "no" }, body: note });
    assert.equal(response.status, 200);
    assert.equal((await Promise.all(received)).length, 8);
    assert.ok(followers.every((ws) => ws.readyState === WebSocket.OPEN));
  } finally {
    extra?.terminate();
    await Promise.all(followers.map((ws) => new Promise((resolve) => {
      const timer = setTimeout(resolve, 5_000);
      ws.once("close", () => { clearTimeout(timer); resolve(); });
      ws.close();
    })));
  }
});

test("WebSocket upgrades have a per-IP rate limit", { timeout: 30_000 }, async () => {
  const topic = numberedInbox(204), headers = ipHeaders("192.0.2.54");
  for (let i = 0; i < 30; i++) {
    const ws = new WebSocket(`ws://127.0.0.1:${HTTP}/${topic}/ws`, { headers });
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`upgrade ${i + 1} timed out`)), 5_000);
      ws.once("open", () => { clearTimeout(timer); resolve(); });
      ws.once("error", (e) => { clearTimeout(timer); reject(e); });
    });
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`close ${i + 1} timed out`)), 5_000);
      ws.once("close", () => { clearTimeout(timer); resolve(); });
      ws.close();
    });
  }
  const extra = new WebSocket(`ws://127.0.0.1:${HTTP}/${topic}/ws`, { headers });
  extra.on("error", () => { /* expected refusal */ });
  const status = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("upgrade refusal timed out")), 5_000);
    extra.once("unexpected-response", (_request, response) => { clearTimeout(timer); response.resume(); resolve(response.statusCode); });
    extra.once("open", () => { clearTimeout(timer); resolve(101); });
  });
  assert.equal(status, 429);
});

test("active followers are bounded globally and per address without eviction", { timeout: 30_000 }, async () => {
  const sockets = [], accepted = [];
  const open = async (ip, topic) => {
    const ws = new WebSocket(`ws://127.0.0.1:${HTTP}/${topic}/ws`, { headers: ipHeaders(ip) });
    sockets.push(ws);
    accepted.push(ws);
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`follower ${topic} timed out`)), 5_000);
      ws.once("open", () => { clearTimeout(timer); resolve(); });
      ws.once("error", (e) => { clearTimeout(timer); reject(e); });
    });
  };
  const refused = async (ip, topic) => {
    const ws = new WebSocket(`ws://127.0.0.1:${HTTP}/${topic}/ws`, { headers: ipHeaders(ip) });
    sockets.push(ws);
    ws.on("error", () => {});
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`refusal ${topic} timed out`)), 5_000);
      ws.once("unexpected-response", (_request, response) => {
        clearTimeout(timer);
        response.resume();
        resolve({ status: response.statusCode, body: "" });
      });
      ws.once("open", () => { clearTimeout(timer); resolve({ status: 101 }); });
    });
  };
  const online = async () => (await (await fetch(`${base}/admin/stats`, {
    headers: { ...ipHeaders("192.0.2.73"), authorization: "Bearer test-admin-key" },
  })).json()).online;
  try {
    for (let i = 0; i < 8; i++) await open("192.0.2.70", numberedInbox(500 + i));
    const perIp = await refused("192.0.2.70", numberedInbox(508));
    assert.equal(perIp.status, 503, "the ninth socket from one address is refused");
    assert.equal(await online(), 8, "a rejected upgrade left no empty topic set");
    for (let i = 0; i < 8; i++) await open("192.0.2.71", numberedInbox(510 + i));
    const global = await refused("192.0.2.72", numberedInbox(520));
    assert.equal(global.status, 503, "the seventeenth global follower is refused");
    assert.equal(await online(), 16, "the global rejection also left no empty topic set");
    assert.equal(accepted.length, 16);
    assert.ok(accepted.every((ws) => ws.readyState === WebSocket.OPEN));
  } finally {
    await Promise.all(sockets.map((ws) => new Promise((resolve) => {
      if (ws.readyState === WebSocket.CLOSED) return resolve();
      const timer = setTimeout(resolve, 5_000);
      ws.once("close", () => { clearTimeout(timer); resolve(); });
      if (ws.readyState === WebSocket.OPEN) ws.close(); else ws.terminate();
    })));
  }
});

test("one address cannot flood a topic, and the aggregate topic quota still applies", { timeout: 30_000 }, async () => {
  const topic = numberedInbox(202), headers = ipHeaders("192.0.2.44");
  for (let i = 0; i < 20; i++) {
    const response = await fetch(`${base}/${topic}`, { method: "POST", headers: { ...headers, cache: "no" }, body: note });
    assert.equal(response.status, 200, `per-IP write ${i + 1}`);
    await response.arrayBuffer();
  }
  const limited = await fetch(`${base}/${topic}`, { method: "POST", headers: { ...headers, cache: "no" }, body: note });
  assert.equal(limited.status, 429);
  assert.match((await limited.json()).error, /this address/);

  const aggregate = numberedInbox(203);
  for (let i = 0; i < 60; i++) {
    const response = await fetch(`${base}/${aggregate}`, {
      method: "POST", headers: { ...ipHeaders(`198.51.100.${i + 1}`), cache: "no" }, body: note,
    });
    assert.equal(response.status, 200, `aggregate write ${i + 1}`);
    await response.arrayBuffer();
  }
  const full = await fetch(`${base}/${aggregate}`, {
    method: "POST", headers: { ...ipHeaders("203.0.113.1"), cache: "no" }, body: note,
  });
  assert.equal(full.status, 429);
  assert.match((await full.json()).error, /this inbox/);
});

test("new inbox creation is bounded per address", { timeout: 30_000 }, async () => {
  const headers = ipHeaders("192.0.2.45");
  for (let i = 0; i < 20; i++) {
    const response = await fetch(`${base}/${numberedInbox(300 + i)}`, { method: "POST", headers, body: note });
    assert.equal(response.status, 200, `new inbox ${i + 1}`);
    await response.arrayBuffer();
  }
  const limited = await fetch(`${base}/${numberedInbox(320)}`, { method: "POST", headers, body: note });
  assert.equal(limited.status, 429);
  assert.match((await limited.json()).error, /new inboxes/);
});

test("pending transit pairs have global and per-IP caps while token replacement still works", { timeout: 30_000 }, async (t) => {
  const external = [...new Set(Object.values(os.networkInterfaces()).flat()
    .filter((info) => info && !info.internal && (info.family === "IPv4" || info.family === 4)).map((info) => info.address))];
  if (external.length < 2) return t.skip("two non-loopback IPv4 addresses are needed to test the caps independently");
  const sockets = [];
  const start = (host, number, side = "aa") => new Promise((resolve, reject) => {
    const token = number.toString(16).padStart(64, "0");
    const socket = net.connect(TRANSIT, host);
    sockets.push(socket);
    let received = "", lineResolve;
    socket.on("data", (d) => {
      received += String(d);
      if (received.includes("\n") && lineResolve) { const resolve = lineResolve; lineResolve = null; resolve(received); }
    });
    socket.once("error", reject);
    socket.once("connect", () => {
      socket.write(`please relay ${token} for side ${side}\n`);
      resolve({
        socket,
        line: () => received.includes("\n") ? Promise.resolve(received) : new Promise((resolveLine, rejectLine) => {
          const timer = setTimeout(() => rejectLine(new Error("transit response timed out")), 5_000);
          lineResolve = (line) => { clearTimeout(timer); resolveLine(line); };
        }),
      });
    });
  });
  const closed = (socket) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("replaced transit socket did not close")), 5_000);
    socket.once("close", () => { clearTimeout(timer); resolve(); });
  });
  const replace = async (entry, host, number) => {
    const oldClosed = closed(entry.socket);
    const replacement = await start(host, number);
    await oldClosed;
    return replacement;
  };
  try {
    const a = await replace(await start("127.0.0.1", 1), "127.0.0.1", 1);
    const b = await replace(await start("127.0.0.1", 2), "127.0.0.1", 2);
    assert.equal(a.socket.destroyed, false);
    assert.equal(b.socket.destroyed, false);
    assert.equal((await (await start("127.0.0.1", 3)).line()).toString(), "error relay busy\n");

    await replace(await start(external[0], 4), external[0], 4); // third global slot
    await replace(await start(external[1], 5), external[1], 5); // fourth global slot
    assert.equal((await (await start(external[0], 6)).line()).toString(), "error relay busy\n");
  } finally {
    await Promise.all(sockets.map((socket) => new Promise((resolve) => {
      if (socket.destroyed) return resolve();
      const timer = setTimeout(resolve, 5_000);
      socket.once("close", () => { clearTimeout(timer); resolve(); });
      socket.end();
    })));
  }
});

test("active transit pairs have global and per-IP caps and release slots on close", { timeout: 30_000 }, async (t) => {
  const external = [...new Set(Object.values(os.networkInterfaces()).flat()
    .filter((info) => info && !info.internal && (info.family === "IPv4" || info.family === 4)).map((info) => info.address))];
  if (external.length < 2) return t.skip("two non-loopback IPv4 addresses are needed to test the caps independently");
  const sockets = [];
  const start = (host, number, side) => new Promise((resolve, reject) => {
    const token = number.toString(16).padStart(64, "0");
    const socket = net.connect(TRANSIT, host);
    sockets.push(socket);
    let received = "", lineResolve;
    socket.on("data", (d) => {
      received += String(d);
      if (received.includes("\n") && lineResolve) { const resolveLine = lineResolve; lineResolve = null; resolveLine(received); }
    });
    socket.once("error", reject);
    socket.once("connect", () => {
      socket.write(`please relay ${token} for side ${side}\n`);
      resolve({
        socket,
        line: () => received.includes("\n") ? Promise.resolve(received) : new Promise((resolveLine, rejectLine) => {
          const timer = setTimeout(() => rejectLine(new Error("transit pair response timed out")), 5_000);
          lineResolve = (line) => { clearTimeout(timer); resolveLine(line); };
        }),
      });
    });
  });
  const makePair = async (aHost, bHost, number) => {
    const [a, b] = await Promise.all([start(aHost, number, "aa"), start(bHost, number, "bb")]);
    return { a, b, lines: await Promise.all([a.line(), b.line()]) };
  };
  const closePair = async (pair) => Promise.all([pair.a, pair.b].map(({ socket }) => new Promise((resolve) => {
    if (socket.destroyed) return resolve();
    const timer = setTimeout(resolve, 5_000);
    socket.once("close", () => { clearTimeout(timer); resolve(); });
    socket.end();
  })));
  const transfers = async () => (await (await fetch(`${base}/admin/stats`, {
    headers: { authorization: "Bearer test-admin-key" },
  })).json()).server.transfersNow;
  const waitTransfers = async (wanted) => {
    const deadline = Date.now() + 5_000;
    while (await transfers() !== wanted) {
      if (Date.now() > deadline) throw new Error(`transfers did not return to ${wanted}`);
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  };
  try {
    await waitTransfers(0);
    const first = await makePair("127.0.0.1", "127.0.0.1", 10);
    assert.deepEqual(first.lines, ["ok\n", "ok\n"]);
    const second = await makePair("127.0.0.1", "127.0.0.1", 11);
    assert.deepEqual(second.lines, ["ok\n", "ok\n"]);
    const perIp = await makePair("127.0.0.1", external[0], 12);
    assert.deepEqual(perIp.lines, ["error relay busy\n", "error relay busy\n"]);
    const third = await makePair(external[0], external[1], 13);
    assert.deepEqual(third.lines, ["ok\n", "ok\n"]);
    const full = await makePair(external[0], external[1], 14);
    assert.deepEqual(full.lines, ["error relay busy\n", "error relay busy\n"]);
    assert.equal(await transfers(), 3);

    await closePair(first);
    await waitTransfers(2);
    const replacementCapacity = await makePair("127.0.0.1", external[0], 15);
    assert.deepEqual(replacementCapacity.lines, ["ok\n", "ok\n"], "closed pairs release their global and per-IP slots");
  } finally {
    await Promise.all(sockets.map((socket) => new Promise((resolve) => {
      if (socket.destroyed) return resolve();
      const timer = setTimeout(resolve, 5_000);
      socket.once("close", () => { clearTimeout(timer); resolve(); });
      socket.end();
    })));
  }
});

test("admin comparisons, headers, generic errors and failed-auth throttling", { timeout: 15_000 }, async () => {
  for (const path of ["/admin", "/admin/", "/admin/stats", "/admin/not-found"]) {
    const response = await fetch(base + path, { headers: ipHeaders("192.0.2.46") });
    assertAdminHeaders(response);
    await response.arrayBuffer();
  }
  const normalized = await rawGet("/elsewhere/../admin/stats", ipHeaders("192.0.2.46"));
  assert.equal(normalized.status, 401, "dot segments cannot bypass the admin header policy");
  assert.equal(normalized.headers["x-frame-options"], "DENY");
  assert.match(normalized.headers["content-security-policy"], /frame-ancestors 'none'/);

  for (const key of ["x", "test-admin-kez", "test-admin-key-extra"]) {
    const response = await fetch(`${base}/admin/stats`, { headers: { ...ipHeaders("192.0.2.46"), authorization: `Bearer ${key}` } });
    assert.equal(response.status, 401);
    assertAdminHeaders(response);
    await response.arrayBuffer();
  }

  const attempts = ipHeaders("192.0.2.47");
  for (let i = 0; i < 5; i++) {
    const response = await fetch(`${base}/admin/stats`, { headers: { ...attempts, authorization: "Bearer wrong" } });
    assert.equal(response.status, 401);
    await response.arrayBuffer();
  }
  const limited = await fetch(`${base}/admin/stats`, { headers: { ...attempts, authorization: "Bearer test-admin-key" } });
  assert.equal(limited.status, 429);
  assert.equal(limited.headers.get("retry-after"), "600");
  assertAdminHeaders(limited);
  await limited.arrayBuffer();
  const otherAddress = await fetch(`${base}/admin/stats`, { headers: { ...ipHeaders("192.0.2.48"), authorization: "Bearer test-admin-key" } });
  assert.equal(otherAddress.status, 200);
  assertAdminHeaders(otherAddress);
  await otherAddress.arrayBuffer();

  const malformed = await fetch(`${base}/admin/%E0%A4%A`, { headers: ipHeaders("192.0.2.49") });
  assert.equal(malformed.status, 500);
  assertAdminHeaders(malformed);
  const error = await malformed.json();
  assert.deepEqual(error, { error: { code: 500, message: "INTERNAL", status: "INTERNAL" } });
});

test("a low global note cap rejects without eviction and survives restart", { timeout: 30_000 }, async () => {
  const port = 19_000 + Math.floor(Math.random() * 400), transitPort = 15_000 + Math.floor(Math.random() * 400);
  const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), "holynotch-cap-"));
  const script = new URL("../holynotch-server.mjs", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1");
  let capped;
  const start = async () => {
    capped = spawn(process.execPath, [script], {
      env: { ...process.env, HTTP_PORT: String(port), TRANSIT_PORT: String(transitPort), DATA_DIR: dataDir, ADMIN_KEY: "cap-admin", MAX_NOTES: "2" },
      stdio: ["ignore", "pipe", "inherit"],
    });
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("capped server didn't start")), 10_000);
      capped.stdout.on("data", (d) => { if (String(d).includes("Transit relay")) { clearTimeout(timer); resolve(); } });
      capped.once("error", (e) => { clearTimeout(timer); reject(e); });
      capped.once("exit", (code) => { clearTimeout(timer); reject(new Error(`capped server exited (${code})`)); });
    });
  };
  const stop = async () => {
    if (!capped || capped.exitCode != null) return;
    capped.kill();
    await new Promise((resolve) => {
      const timer = setTimeout(resolve, 5_000);
      capped.once("exit", () => { clearTimeout(timer); resolve(); });
    });
  };
  const url = `http://127.0.0.1:${port}`;
  try {
    await start();
    assert.equal((await fetch(`${url}/${numberedInbox(600)}`, { method: "POST", body: note })).status, 200);
    assert.equal((await fetch(`${url}/${numberedInbox(601)}`, { method: "POST", body: note })).status, 200);
    const full = await fetch(`${url}/${numberedInbox(602)}`, { method: "POST", body: note });
    assert.equal(full.status, 503);
    assert.deepEqual(await full.json(), { code: 503, error: "note storage is full" });
    for (const n of [600, 601]) {
      const polled = await (await fetch(`${url}/${numberedInbox(n)}/json?poll=1&since=all`)).text();
      assert.equal(polled.trim().split("\n").length, 1, "another topic's stored note was not evicted");
    }
    await stop();
    await start();
    const afterRestart = await fetch(`${url}/${numberedInbox(603)}`, { method: "POST", body: note });
    assert.equal(afterRestart.status, 503, "the cached stored-note count is rebuilt on restart");
  } finally {
    await stop();
    try { fs.rmSync(dataDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); } catch { /* closing SQLite may hold it */ }
  }
});
