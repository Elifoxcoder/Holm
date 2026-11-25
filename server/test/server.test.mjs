// Starts the server on free ports with an empty database and checks what the apps rely on.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import net from "node:net";
import os from "node:os";
import fs from "node:fs";
import path from "node:path";
import WebSocket from "ws";

const HTTP = 18480 + Math.floor(Math.random() * 500), TRANSIT = 14001 + Math.floor(Math.random() * 500);
const base = `http://127.0.0.1:${HTTP}`;
const docs = `${base}/v1/projects/holynotch/databases/(default)/documents`;
let child, dir;

before(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "holynotch-"));
  child = spawn(process.execPath, [new URL("../holynotch-server.mjs", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1")], {
    env: { ...process.env, HTTP_PORT: String(HTTP), TRANSIT_PORT: String(TRANSIT), DATA_DIR: dir, ADMIN_KEY: "test-admin-key" },
    stdio: ["ignore", "pipe", "inherit"],
  });
  await new Promise((resolve) => child.stdout.on("data", (d) => { if (String(d).includes("Transit relay")) resolve(); }));
});
after(() => { child.kill(); try { fs.rmSync(dir, { recursive: true, force: true }); } catch { /* Windows keeps the db a moment */ } });

const post = (url, body) => fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
const sealed = (n = 60) => "v1." + "A".repeat(n);

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
