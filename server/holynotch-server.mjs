// HolyNotch server: everything HolyNotch needs from the internet, on one machine (your VPS).
//
//   Account   the subset of Firebase Auth and Firestore's REST APIs that HolyNotch uses (Holm.Core/Account/Firebase.cs,
//             mobile/lib/net/account.dart), so the apps only change their base URL:
//               POST /v1/accounts:signUp | :signInWithPassword | :delete      POST /v1/token (refresh)
//               GET | PATCH | DELETE /v1/projects/<p>/databases/(default)/documents/users/<uid>[/sync/<section>]
//             An account that is still in Holm's Firebase project moves here by itself at its first sign-in (same uid,
//             same encrypted documents), when MIGRATE_FIREBASE_KEY is set.
//   Inboxes   the relay for friend notes (as relay/ on Cloudflare): POST /<topic>, GET /<topic>/json?poll=1, GET /<topic>/ws
//   Transfers a magic-wormhole style transit relay on TCP (TRANSIT_PORT): "please relay <token> for side <side>\n", "ok\n"
//   Admin     /admin (the page) and /admin/stats (JSON, "Authorization: Bearer <ADMIN_KEY>")
//
// Everything users store is encrypted by the apps before it arrives; the server sees ciphertext, inbox names and emails.
// Runs on Node 22+ (node:sqlite) with one package, ws. Configuration in environment variables, see README.md.

import http from "node:http";
import net from "node:net";
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { WebSocketServer } from "ws";

const env = process.env;
const HTTP_HOST = env.HTTP_HOST ?? "127.0.0.1";
const HTTP_PORT = Number(env.HTTP_PORT ?? 8480);
const TRANSIT_PORT = Number(env.TRANSIT_PORT ?? 4001);
const DATA = env.DATA_DIR ?? "/var/lib/holynotch";
const ADMIN_KEY = env.ADMIN_KEY ?? "";
const MIGRATE_KEY = env.MIGRATE_FIREBASE_KEY ?? ""; // Holm's public Firebase web API key: moves old accounts over
const MIGRATE_PROJECT = env.MIGRATE_FIREBASE_PROJECT ?? "holm-accounts";
const PAGE = fs.readFileSync(new URL("./admin.html", import.meta.url), "utf8");

fs.mkdirSync(DATA, { recursive: true });
const db = new DatabaseSync(path.join(DATA, "holynotch.db"));
db.exec(`
  PRAGMA journal_mode = WAL;
  PRAGMA synchronous = NORMAL;
  CREATE TABLE IF NOT EXISTS accounts (uid TEXT PRIMARY KEY, email TEXT NOT NULL UNIQUE, salt BLOB NOT NULL, hash BLOB NOT NULL,
                                       created INTEGER NOT NULL, migrated INTEGER NOT NULL DEFAULT 0);
  CREATE TABLE IF NOT EXISTS tokens (hash TEXT PRIMARY KEY, uid TEXT NOT NULL, kind TEXT NOT NULL, expires INTEGER NOT NULL);
  CREATE INDEX IF NOT EXISTS tokens_uid ON tokens (uid);
  CREATE TABLE IF NOT EXISTS docs (path TEXT PRIMARY KEY, uid TEXT NOT NULL, fields TEXT NOT NULL, created TEXT NOT NULL, updated TEXT NOT NULL);
  CREATE INDEX IF NOT EXISTS docs_uid ON docs (uid);
  CREATE TABLE IF NOT EXISTS notes (seq INTEGER PRIMARY KEY, topic TEXT NOT NULL, id TEXT NOT NULL, time INTEGER NOT NULL,
                                    expires INTEGER NOT NULL, message TEXT NOT NULL);
  CREATE INDEX IF NOT EXISTS notes_topic ON notes (topic, seq);
  CREATE TABLE IF NOT EXISTS events (time INTEGER NOT NULL, kind TEXT NOT NULL, inbox TEXT NOT NULL DEFAULT '', n INTEGER NOT NULL DEFAULT 1);
  CREATE INDEX IF NOT EXISTS events_time ON events (time);
`);

const now = () => Math.floor(Date.now() / 1000);
const sha = (s) => crypto.createHash("sha256").update(s).digest("hex");
const randomText = (n, alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") =>
  Array.from(crypto.randomBytes(n), (b) => alphabet[b % alphabet.length]).join("");
const log = (...a) => console.log(new Date().toISOString(), ...a);

// ─────────────────────────── counts (admin) ───────────────────────────

const addEvent = db.prepare("INSERT INTO events (time, kind, inbox, n) VALUES (?, ?, ?, ?)");
function track(kind, topic = "", n = 1) {
  try { addEvent.run(now(), kind, topic.startsWith("wnf-") ? sha(topic).slice(0, 12) : "", n); } catch { /* never breaks a request */ }
}

const followers = new Map(); // topic -> Set<WebSocket>
let transitOpen = 0;

function stats() {
  const t = now();
  const q = (sql, ...a) => db.prepare(sql).all(...a);
  const active = (days) => q("SELECT count(DISTINCT inbox) AS n FROM events WHERE kind = 'open' AND inbox != '' AND time > ?", t - days * 86400)[0].n;
  const last24h = Object.fromEntries(q("SELECT kind, SUM(n) AS n FROM events WHERE time > ? GROUP BY kind", t - 86400).map((r) => [r.kind, r.n]));
  const perDay = {};
  for (const r of q("SELECT (time / 86400) * 86400 AS day, kind, SUM(n) AS n FROM events WHERE time > ? GROUP BY day, kind ORDER BY day", t - 14 * 86400))
    (perDay[r.day] ??= {})[r.kind] = r.n;
  const one = (sql, ...a) => db.prepare(sql).get(...a).n;
  return {
    online: followers.size,
    active: { day: active(1), week: active(7), month: active(30) },
    last24h,
    server: {
      accounts: one("SELECT count(*) AS n FROM accounts"),
      migrated: one("SELECT count(*) AS n FROM accounts WHERE migrated = 1"),
      inboxes: one("SELECT count(DISTINCT topic) AS n FROM notes"),
      notes: one("SELECT count(*) AS n FROM notes"),
      transfersNow: transitOpen,
      database: fs.statSync(path.join(DATA, "holynotch.db")).size,
      uptime: Math.round(process.uptime()),
    },
    days: Object.entries(perDay).map(([day, kinds]) => ({ day: Number(day), ...kinds })),
  };
}

function sameKey(a, b) {
  const x = Buffer.from(a ?? ""), y = Buffer.from(b ?? "");
  return x.length === y.length && x.length > 0 && crypto.timingSafeEqual(x, y);
}

// ─────────────────────────── HTTP helpers ───────────────────────────

function send(res, status, body, headers = {}) {
  const text = typeof body === "string" ? body : JSON.stringify(body);
  res.writeHead(status, { "content-type": typeof body === "string" ? "text/plain; charset=utf-8" : "application/json; charset=utf-8",
                          "cache-control": "no-store", ...headers });
  res.end(text);
}

const MAX_BODY = 1_200_000; // a synced section is at most 900 000 characters
function readBody(req, limit = MAX_BODY) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on("data", (c) => {
      size += c.length;
      if (size > limit) { reject(Object.assign(new Error("too large"), { status: 413 })); req.destroy(); return; }
      chunks.push(c);
    });
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

const clientIp = (req) => (req.headers["x-real-ip"] ?? req.socket.remoteAddress ?? "?").toString();

/** At most `limit` hits per key in `seconds`. */
function limiter(limit, seconds) {
  const hits = new Map();
  setInterval(() => { const t = now(); for (const [k, v] of hits) if (v.every((x) => x <= t - seconds)) hits.delete(k); }, 60_000).unref();
  return (key) => {
    const t = now(), list = (hits.get(key) ?? []).filter((x) => x > t - seconds);
    if (list.length >= limit) { hits.set(key, list); return false; }
    list.push(t); hits.set(key, list);
    return true;
  };
}

// ─────────────────────────── account (Firebase-compatible) ───────────────────────────

const authError = (res, code, status = 400) => send(res, status, { error: { code: status, message: code, errors: [{ message: code, reason: "invalid" }] } });
const storeError = (res, status, state, message = state) => send(res, status, { error: { code: status, message, status: state } });

const ID_LIFE = 3600;
const signInLimit = limiter(20, 600), signUpLimit = limiter(5, 3600);

function scrypt(secret, salt) { return crypto.scryptSync(secret, salt, 32, { N: 16384, r: 8, p: 1 }); }

function issue(uid, email) {
  const id = randomText(48), refresh = randomText(64);
  db.prepare("INSERT INTO tokens (hash, uid, kind, expires) VALUES (?, ?, 'id', ?)").run(sha(id), uid, now() + ID_LIFE);
  db.prepare("INSERT INTO tokens (hash, uid, kind, expires) VALUES (?, ?, 'refresh', ?)").run(sha(refresh), uid, now() + 400 * 86400);
  return { kind: "identitytoolkit#VerifyPasswordResponse", localId: uid, email, idToken: id, refreshToken: refresh, expiresIn: String(ID_LIFE), registered: true };
}

/** The uid an ID token stands for, or null. */
function bearer(req) {
  const token = /^Bearer (.+)$/.exec(req.headers.authorization ?? "")?.[1];
  if (!token) return null;
  const row = db.prepare("SELECT uid FROM tokens WHERE hash = ? AND kind = 'id' AND expires > ?").get(sha(token), now());
  return row?.uid ?? null;
}

const cleanEmail = (e) => (typeof e === "string" ? e.trim().toLowerCase() : "");
const validEmail = (e) => e.length <= 254 && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(e);

function createAccount(uid, email, secret, migrated) {
  const salt = crypto.randomBytes(16);
  db.prepare("INSERT INTO accounts (uid, email, salt, hash, created, migrated) VALUES (?, ?, ?, ?, ?, ?)")
    .run(uid, email, salt, scrypt(secret, salt), now(), migrated ? 1 : 0);
}

const FIREBASE_AUTH = "https://identitytoolkit.googleapis.com/v1/accounts";

async function firebase(url, body, form = false) {
  const r = await fetch(url, form
    ? { method: "POST", headers: { "content-type": "application/x-www-form-urlencoded" }, body: new URLSearchParams(body).toString() }
    : { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) }).catch(() => null);
  return r?.ok ? r.json() : null;
}

/** Whether the email and sign-in secret open the account at Holm's Firebase (its uid and ID token), or null. */
async function firebaseSignIn(email, secret) {
  if (!MIGRATE_KEY) return null;
  const s = await firebase(`${FIREBASE_AUTH}:signInWithPassword?key=${encodeURIComponent(MIGRATE_KEY)}`, { email, password: secret, returnSecureToken: true });
  return s && /^[A-Za-z0-9]{6,64}$/.test(s.localId ?? "") ? { uid: s.localId, idToken: s.idToken } : null;
}

/**
 * Copies an account's encrypted documents from Holm's Firebase project with the same uid (the vault is bound to it)
 * and creates the account here: with the sign-in secret's hash when we have it (a sign-in), without one when the
 * account came through a refresh token (a signed-in app): its next password sign-in is then checked at Firebase once.
 * Returns false when the account is here already.
 */
async function moveAccount(uid, idToken, email, secret) {
  const base = `https://firestore.googleapis.com/v1/projects/${MIGRATE_PROJECT}/databases/(default)/documents`;
  const docs = [];
  for (const p of [`users/${uid}`, ...SECTIONS.map((s) => `users/${uid}/sync/${s}`)]) {
    const r = await fetch(`${base}/${p}`, { headers: { authorization: `Bearer ${idToken}` } });
    if (r.status === 404) continue;
    if (!r.ok) throw new Error(`Firebase answered ${r.status} for ${p}`);
    const d = await r.json();
    const fields = {};
    for (const [k, v] of Object.entries(d.fields ?? {})) if (typeof v?.stringValue === "string") fields[k] = v.stringValue;
    docs.push({ path: p, fields, created: d.createTime ?? stamp(), updated: d.updateTime ?? stamp() });
  }
  db.exec("BEGIN");
  try {
    if (db.prepare("SELECT 1 FROM accounts WHERE uid = ? OR email = ?").get(uid, email)) { db.exec("ROLLBACK"); return false; }
    if (secret != null) createAccount(uid, email, secret, true);
    else db.prepare("INSERT INTO accounts (uid, email, salt, hash, created, migrated) VALUES (?, ?, x'', x'', ?, 1)").run(uid, email, now());
    for (const d of docs) db.prepare("INSERT OR REPLACE INTO docs (path, uid, fields, created, updated) VALUES (?, ?, ?, ?, ?)")
      .run(d.path, uid, JSON.stringify(d.fields), d.created, d.updated);
    db.exec("COMMIT");
  } catch (e) { db.exec("ROLLBACK"); throw e; }
  log(`moved an account from Holm's Firebase (${docs.length} documents, ${secret != null ? "sign-in" : "refresh token"})`);
  track("migrated");
  return true;
}

/** An account still at Holm, moved here at its first sign-in. Its uid, or null. */
async function migrate(email, secret) {
  const s = await firebaseSignIn(email, secret);
  if (!s) return null;
  return (await moveAccount(s.uid, s.idToken, email, secret)) ? s.uid : null;
}

/**
 * An app that was signed in at Holm's Firebase refreshes its token here: the account moves with that token and the app
 * stays signed in, with the same refresh token. The uid, or null.
 */
async function migrateByRefresh(token) {
  if (!MIGRATE_KEY) return null;
  const t = await firebase(`https://securetoken.googleapis.com/v1/token?key=${encodeURIComponent(MIGRATE_KEY)}`,
                           { grant_type: "refresh_token", refresh_token: token }, true);
  if (!t || !/^[A-Za-z0-9]{6,64}$/.test(t.user_id ?? "")) return null;
  const known = db.prepare("SELECT uid FROM accounts WHERE uid = ?").get(t.user_id);
  if (!known) {
    const who = await firebase(`${FIREBASE_AUTH}:lookup?key=${encodeURIComponent(MIGRATE_KEY)}`, { idToken: t.id_token });
    const email = cleanEmail(who?.users?.[0]?.email);
    if (!validEmail(email) || !(await moveAccount(t.user_id, t.id_token, email, null))) return null;
  }
  db.prepare("INSERT OR REPLACE INTO tokens (hash, uid, kind, expires) VALUES (?, ?, 'refresh', ?)").run(sha(token), t.user_id, now() + 400 * 86400);
  return t.user_id;
}

async function signIn(req, res, body) {
  const email = cleanEmail(body.email), secret = typeof body.password === "string" ? body.password : "";
  if (!validEmail(email)) return authError(res, "INVALID_EMAIL");
  if (!signInLimit(clientIp(req)) || !signInLimit("e:" + email)) return authError(res, "TOO_MANY_ATTEMPTS_TRY_LATER : wait a few minutes");
  let row = db.prepare("SELECT uid, salt, hash FROM accounts WHERE email = ?").get(email);
  if (!row) {
    const uid = await migrate(email, secret).catch((e) => { log("migration failed:", e.message); return null; });
    if (uid) { track("signin"); return send(res, 200, issue(uid, email)); }
    return authError(res, "INVALID_LOGIN_CREDENTIALS");
  }
  if (row.hash.length === 0) {
    // Moved through a refresh token, so no secret yet: Holm's Firebase checks it this once.
    const s = await firebaseSignIn(email, secret);
    if (!s || s.uid !== row.uid) return authError(res, "INVALID_LOGIN_CREDENTIALS");
    const salt = crypto.randomBytes(16);
    db.prepare("UPDATE accounts SET salt = ?, hash = ? WHERE uid = ?").run(salt, scrypt(secret, salt), row.uid);
  } else if (!crypto.timingSafeEqual(scrypt(secret, row.salt), row.hash)) return authError(res, "INVALID_LOGIN_CREDENTIALS");
  track("signin");
  send(res, 200, issue(row.uid, email));
}

async function signUp(req, res, body) {
  const email = cleanEmail(body.email), secret = typeof body.password === "string" ? body.password : "";
  if (!validEmail(email)) return authError(res, "INVALID_EMAIL");
  if (secret.length < 6) return authError(res, "WEAK_PASSWORD : Password should be at least 6 characters");
  if (secret.length > 512) return authError(res, "INVALID_PASSWORD");
  if (!signUpLimit(clientIp(req))) return authError(res, "TOO_MANY_ATTEMPTS_TRY_LATER : wait an hour");
  if (db.prepare("SELECT 1 FROM accounts WHERE email = ?").get(email)) return authError(res, "EMAIL_EXISTS");
  // Someone with a Holm account who chose "create" here: their account moves instead of starting empty.
  const moved = await migrate(email, secret).catch(() => null);
  if (moved) return authError(res, "EMAIL_EXISTS");
  const uid = randomText(28);
  createAccount(uid, email, secret, false);
  track("signup");
  send(res, 200, { ...issue(uid, email), kind: "identitytoolkit#SignupNewUserResponse" });
}

function deleteAccount(req, res, body) {
  const row = typeof body.idToken === "string" && db.prepare("SELECT uid FROM tokens WHERE hash = ? AND kind = 'id' AND expires > ?").get(sha(body.idToken), now());
  if (!row) return authError(res, "INVALID_ID_TOKEN");
  db.exec("BEGIN");
  db.prepare("DELETE FROM docs WHERE uid = ?").run(row.uid);
  db.prepare("DELETE FROM tokens WHERE uid = ?").run(row.uid);
  db.prepare("DELETE FROM accounts WHERE uid = ?").run(row.uid);
  db.exec("COMMIT");
  track("deleted");
  send(res, 200, { kind: "identitytoolkit#DeleteAccountResponse" });
}

async function refresh(req, res, form) {
  const params = new URLSearchParams(form);
  const token = params.get("refresh_token") ?? "";
  if (params.get("grant_type") !== "refresh_token" || !token) return authError(res, "INVALID_GRANT_TYPE");
  let row = db.prepare("SELECT uid FROM tokens WHERE hash = ? AND kind = 'refresh' AND expires > ?").get(sha(token), now());
  if (!row && token.length > 64 && signInLimit(clientIp(req))) { // a Firebase refresh token (ours are 64 characters)
    const uid = await migrateByRefresh(token).catch((e) => { log("migration by refresh failed:", e.message); return null; });
    if (uid) row = { uid };
  }
  if (!row || !db.prepare("SELECT 1 FROM accounts WHERE uid = ?").get(row.uid)) return authError(res, "INVALID_REFRESH_TOKEN");
  const id = randomText(48);
  db.prepare("INSERT INTO tokens (hash, uid, kind, expires) VALUES (?, ?, 'id', ?)").run(sha(id), row.uid, now() + ID_LIFE);
  send(res, 200, { access_token: id, id_token: id, expires_in: String(ID_LIFE), token_type: "Bearer", refresh_token: token, user_id: row.uid, project_id: "holynotch" });
}

// The documents, with the rules of Holm's firestore.rules: own documents only, sealed string fields, five sections.
const SECTIONS = ["identity", "friends", "profile", "settings", "achievements"];
const SEALED = /^v1\.[A-Za-z0-9_-]+$/;
function stamp() {
  const d = new Date(), micros = String(process.hrtime.bigint() % 1000n).padStart(3, "0");
  return d.toISOString().replace("Z", micros + "Z"); // RFC 3339 with microseconds, unique enough per write
}

function docRules(docPath) {
  let m = /^users\/([A-Za-z0-9]{6,64})$/.exec(docPath);
  if (m) return { uid: m[1], field: "vault", max: 200 };
  m = /^users\/([A-Za-z0-9]{6,64})\/sync\/([a-z]+)$/.exec(docPath);
  if (m && SECTIONS.includes(m[2])) return { uid: m[1], field: "data", max: 900000 };
  return null;
}

const docJson = (p, row) => ({
  name: `projects/holynotch/databases/(default)/documents/${p}`,
  fields: Object.fromEntries(Object.entries(JSON.parse(row.fields)).map(([k, v]) => [k, { stringValue: v }])),
  createTime: row.created, updateTime: row.updated,
});

async function documents(req, res, url, docPath) {
  const uid = bearer(req);
  if (!uid) return storeError(res, 401, "UNAUTHENTICATED", "Sign in again.");
  const rules = docRules(docPath);
  if (!rules || rules.uid !== uid) return storeError(res, 403, "PERMISSION_DENIED", "Missing or insufficient permissions.");
  const row = db.prepare("SELECT fields, created, updated FROM docs WHERE path = ?").get(docPath);

  if (req.method === "GET") return row ? send(res, 200, docJson(docPath, row)) : storeError(res, 404, "NOT_FOUND", "No document.");
  if (req.method === "DELETE") { db.prepare("DELETE FROM docs WHERE path = ?").run(docPath); return send(res, 200, {}); }
  if (req.method !== "PATCH") return storeError(res, 405, "INVALID_ARGUMENT");

  let body;
  try { body = JSON.parse(await readBody(req)); } catch (e) { return storeError(res, e.status ?? 400, "INVALID_ARGUMENT"); }
  const fields = {};
  for (const [k, v] of Object.entries(body?.fields ?? {})) fields[k] = v?.stringValue;
  const keys = Object.keys(fields);
  if (keys.length !== 1 || keys[0] !== rules.field || typeof fields[rules.field] !== "string"
      || !SEALED.test(fields[rules.field]) || fields[rules.field].length > rules.max)
    return storeError(res, 403, "PERMISSION_DENIED", "Missing or insufficient permissions.");

  const exists = url.searchParams.get("currentDocument.exists"), updateTime = url.searchParams.get("currentDocument.updateTime");
  if (exists === "false" && row) return storeError(res, 409, "ALREADY_EXISTS", "Document already exists.");
  if (exists === "true" && !row) return storeError(res, 404, "NOT_FOUND", "No document to update.");
  if (updateTime != null && (!row || row.updated !== updateTime)) return storeError(res, 400, "FAILED_PRECONDITION", "The document changed.");

  const t = stamp(), created = row?.created ?? t;
  db.prepare("INSERT OR REPLACE INTO docs (path, uid, fields, created, updated) VALUES (?, ?, ?, ?, ?)").run(docPath, uid, JSON.stringify(fields), created, t);
  track("sync");
  send(res, 200, docJson(docPath, { fields: JSON.stringify(fields), created, updated: t }));
}

// ─────────────────────────── inboxes (relay) ───────────────────────────

const INBOX = /^wnf-[a-z2-7]{26}$/, INVITE = /^holm-inv-[a-z2-7]{20}$/;
const NOTE = /^wnf1 [A-Za-z0-9_-]{40,200} [A-Za-z0-9_-]{30,}$/, CARD = /^hinv1 [A-Za-z0-9_-]{30,}$/;
const MAX_NOTE = 4096, INBOX_TTL = 7 * 86400, INVITE_TTL = 12 * 3600, INBOX_KEEP = 300, INVITE_KEEP = 20, SOCKETS = 8;
const isTopic = (t) => INBOX.test(t) || INVITE.test(t);
const isInvite = (t) => INVITE.test(t);
const postLimit = limiter(120, 60), topicLimit = limiter(60, 60);

function parseSince(value, t = now()) {
  if (value == null || value === "") return null;
  if (value === "all") return 0;
  if (/^\d{1,12}$/.test(value)) return Number(value);
  const d = /^(\d{1,6})([smhd])$/.exec(value);
  return d ? t - Number(d[1]) * { s: 1, m: 60, h: 3600, d: 86400 }[d[2]] : null;
}

const kept = (topic, since) => since == null ? []
  : db.prepare("SELECT id, time, message FROM notes WHERE topic = ? AND time >= ? AND expires > ? ORDER BY seq").all(topic, since, now());

async function post(req, res, topic) {
  if (Number(req.headers["content-length"] ?? 0) > MAX_NOTE * 2) { track("refused"); return send(res, 413, { code: 413, error: "note too large" }); }
  if (!postLimit(clientIp(req))) { track("refused"); return send(res, 429, { code: 429, error: "too many notes, wait a minute" }); }
  let text;
  try { text = await readBody(req, MAX_NOTE * 2); } catch { track("refused"); return send(res, 413, { code: 413, error: "note too large" }); }
  if (text.length > MAX_NOTE || !(isInvite(topic) ? CARD : NOTE).test(text)) { track("refused"); return send(res, 400, { code: 400, error: "not a HolyNotch note" }); }
  if (!topicLimit(topic)) { track("refused"); return send(res, 429, { code: 429, error: "this inbox takes no more notes this minute" }); }
  const keep = !/^no$/i.test((req.headers["cache"] ?? req.headers["x-cache"] ?? "").toString());
  const t = now(), note = { id: randomText(12), time: t, event: "message", topic, message: text };
  const line = JSON.stringify(note);
  for (const ws of followers.get(topic) ?? []) { try { ws.send(line); } catch { /* closing */ } }
  if (keep) {
    const { seq } = db.prepare("INSERT INTO notes (topic, id, time, expires, message) VALUES (?, ?, ?, ?, ?) RETURNING seq")
      .get(topic, note.id, t, t + (isInvite(topic) ? INVITE_TTL : INBOX_TTL), text);
    const limit = isInvite(topic) ? INVITE_KEEP : INBOX_KEEP;
    db.prepare("DELETE FROM notes WHERE topic = ? AND seq <= (SELECT seq FROM notes WHERE topic = ? ORDER BY seq DESC LIMIT 1 OFFSET ?)").run(topic, topic, limit);
    void seq;
  }
  track(isInvite(topic) ? "invite" : keep ? "note" : "live", topic);
  send(res, 200, note);
}

function poll(res, url, topic) {
  if (!["1", "true", "yes"].includes(url.searchParams.get("poll") ?? "")) return send(res, 400, { code: 400, error: "only polling: use /ws to follow" });
  track("poll", topic);
  const lines = kept(topic, parseSince(url.searchParams.get("since"))).map((n) => JSON.stringify({ ...n, event: "message", topic }) + "\n").join("");
  res.writeHead(200, { "content-type": "application/x-ndjson", "cache-control": "no-store" });
  res.end(lines);
}

const wss = new WebSocketServer({ noServer: true, maxPayload: 1024 });
function follow(req, socket, head, url, topic) {
  wss.handleUpgrade(req, socket, head, (ws) => {
    let set = followers.get(topic);
    if (!set) followers.set(topic, set = new Set());
    if (set.size >= SOCKETS) { const [oldest] = set; try { oldest.close(1013, "too many followers"); } catch { /* gone */ } set.delete(oldest); }
    set.add(ws);
    track("open", topic);
    ws.isAlive = true;
    ws.on("pong", () => { ws.isAlive = true; });
    ws.on("message", () => { /* followers only listen */ });
    ws.on("close", () => {
      set.delete(ws);
      if (set.size === 0) followers.delete(topic);
      track("close", topic);
    });
    ws.on("error", () => ws.terminate());
    ws.send(JSON.stringify({ id: randomText(12), time: now(), event: "open", topic }));
    for (const n of kept(topic, parseSince(url.searchParams.get("since")))) ws.send(JSON.stringify({ ...n, event: "message", topic }));
  });
}

// Dead connections go; a live one answers the ping (Cloudflare and nginx would drop a silent line too).
setInterval(() => {
  for (const set of followers.values()) for (const ws of set) {
    if (!ws.isAlive) { ws.terminate(); continue; }
    ws.isAlive = false;
    try { ws.ping(); } catch { /* closing */ }
  }
}, 30_000).unref();

// Expired notes, ID tokens and old counts go every ten minutes.
setInterval(() => {
  const t = now();
  db.prepare("DELETE FROM notes WHERE expires <= ?").run(t);
  db.prepare("DELETE FROM tokens WHERE expires <= ?").run(t);
  db.prepare("DELETE FROM events WHERE time <= ?").run(t - 92 * 86400);
}, 600_000).unref();

// ─────────────────────────── HTTP server ───────────────────────────

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, "http://holynotch");
    const p = url.pathname;
    if (p === "/" && req.method === "GET") return send(res, 200, "HolyNotch server: accounts, friend inboxes and transfers for HolyNotch.\n");
    if (p === "/health") return send(res, 200, { ok: true });

    if (p === "/admin" || p === "/admin/") return send(res, 200, PAGE, { "content-type": "text/html; charset=utf-8" });
    if (p === "/admin/stats") {
      if (!ADMIN_KEY) return send(res, 503, { error: "ADMIN_KEY isn't set on the server" });
      if (!sameKey((req.headers.authorization ?? "").replace(/^Bearer /, ""), ADMIN_KEY)) return send(res, 401, { error: "wrong key" });
      return send(res, 200, stats());
    }

    if (req.method === "POST" && p.startsWith("/v1/accounts:")) {
      let body;
      try { body = JSON.parse(await readBody(req, 16_384)); } catch { return authError(res, "INVALID_JSON"); }
      switch (p.slice("/v1/accounts:".length)) {
        case "signInWithPassword": return await signIn(req, res, body);
        case "signUp": return await signUp(req, res, body);
        case "delete": return deleteAccount(req, res, body);
        default: return authError(res, "OPERATION_NOT_ALLOWED", 404);
      }
    }
    if (req.method === "POST" && p === "/v1/token") return await refresh(req, res, await readBody(req, 16_384));
    const doc = /^\/v1\/projects\/[^/]+\/databases\/\(default\)\/documents\/(.+)$/.exec(decodeURIComponent(p));
    if (doc) return await documents(req, res, url, doc[1]);

    const parts = p.split("/").filter(Boolean);
    const topic = parts[0];
    if (!isTopic(topic) || parts.length > 2) return send(res, 404, { code: 404, error: "no such inbox" });
    const action = parts[1] ?? "";
    if ((req.method === "POST" || req.method === "PUT") && action === "") return await post(req, res, topic);
    if (req.method === "GET" && action === "json") return poll(res, url, topic);
    if (action === "ws") return send(res, 426, { code: 426, error: "a WebSocket only" });
    send(res, 405, { code: 405, error: "not here" });
  } catch (e) {
    log("error:", e?.stack ?? e);
    if (!res.headersSent) send(res, e?.status ?? 500, { error: { code: 500, message: "INTERNAL", status: "INTERNAL" } });
  }
});

server.on("upgrade", (req, socket, head) => {
  const url = new URL(req.url, "http://holynotch");
  const parts = url.pathname.split("/").filter(Boolean);
  if (parts.length === 2 && parts[1] === "ws" && isTopic(parts[0])) return follow(req, socket, head, url, parts[0]);
  socket.destroy();
});

server.listen(HTTP_PORT, HTTP_HOST, () => log(`HolyNotch server on http://${HTTP_HOST}:${HTTP_PORT}`));

// ─────────────────────────── transit relay (file transfers) ───────────────────────────

const waiting = new Map(); // token -> { side, socket }
const TRANSIT_WAIT = 10 * 60 * 1000, TRANSIT_IDLE = 5 * 60 * 1000;
const transitIpLimit = limiter(120, 60);

const transit = net.createServer((socket) => {
  socket.setNoDelay(true);
  socket.setKeepAlive(true, 30_000);
  const ip = socket.remoteAddress ?? "?";
  if (!transitIpLimit(ip)) { socket.end("error too many connections\n"); return; }
  let buffer = Buffer.alloc(0), paired = false, token = null;
  const giveUp = setTimeout(() => { socket.end("error no partner\n"); }, TRANSIT_WAIT);
  socket.on("error", () => socket.destroy());
  socket.on("close", () => {
    clearTimeout(giveUp);
    if (!paired && token && waiting.get(token)?.socket === socket) waiting.delete(token);
  });
  const onData = (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    const nl = buffer.indexOf(10);
    if (nl < 0) { if (buffer.length > 200) socket.destroy(); return; }
    socket.off("data", onData);
    const line = buffer.subarray(0, nl).toString("ascii"), rest = buffer.subarray(nl + 1);
    const m = /^please relay ([0-9a-f]{64}) for side ([0-9a-f]{1,32})$/.exec(line);
    if (!m) { socket.end("bad handshake\n"); return; }
    token = m[1];
    const side = m[2], other = waiting.get(token);
    if (!other || other.side === side) {
      if (other) other.socket.destroy();
      // Told when its partner arrives: from then on the pair's own timeouts apply.
      waiting.set(token, { side, socket, rest, paired: () => { paired = true; clearTimeout(giveUp); } });
      return;
    }
    waiting.delete(token);
    paired = true;
    clearTimeout(giveUp);
    other.paired();
    pair(other.socket, other.rest, socket, rest);
  };
  socket.on("data", onData);
});

function pair(a, aRest, b, bRest) {
  transitOpen++;
  track("transfer");
  let bytes = 0, closed = 0;
  // One side closing (the receiver right after its "received") must not cut what is still on its way to the other:
  // the other side gets everything, then the end. Only an idle line or an error ends both at once.
  const counted = () => { if (++closed === 2) { transitOpen--; track("transferBytes", "", bytes); } };
  const kill = () => { a.destroy(); b.destroy(); };
  for (const [s, other] of [[a, b], [b, a]]) {
    s.setTimeout(TRANSIT_IDLE, kill);
    s.on("error", () => other.end());
    s.on("close", () => { other.end(); setTimeout(() => other.destroy(), 30_000).unref(); counted(); });
  }
  a.write("ok\n"); b.write("ok\n");
  if (aRest.length) b.write(aRest);
  if (bRest.length) a.write(bRest);
  a.on("data", (d) => { bytes += d.length; });
  b.on("data", (d) => { bytes += d.length; });
  a.pipe(b); b.pipe(a);
}

transit.listen(TRANSIT_PORT, "0.0.0.0", () => log(`Transit relay on tcp ${TRANSIT_PORT}`));

for (const sig of ["SIGINT", "SIGTERM"]) process.on(sig, () => { log("stopping"); server.close(); transit.close(); db.close(); process.exit(0); });
