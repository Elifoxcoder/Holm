// HolyNotch server: everything HolyNotch needs from the internet, on one machine (your VPS).
//
//   Account   the subset of Firebase Auth and Firestore's REST APIs that HolyNotch uses (Holm.Core/Account/Firebase.cs,
//             mobile/lib/net/account.dart), so the apps only change their base URL:
//               POST /v1/accounts:signUp | :signInWithPassword | :delete      POST /v1/token (refresh)
//               GET | PATCH | DELETE /v1/projects/<p>/databases/(default)/documents/users/<uid>[/sync/<section>]
//             An account that is still in Holm's Firebase project moves here by itself at its first sign-in (same uid,
//             same encrypted documents), when MIGRATE_FIREBASE_KEY is set, until MIGRATE_UNTIL (end of January 2027).
//             That is the only contact with Holm's services left, and it ends then.
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
import { WebSocketServer, createWebSocketStream } from "ws";

const env = process.env;
const HTTP_HOST = env.HTTP_HOST ?? "127.0.0.1";
const HTTP_PORT = Number(env.HTTP_PORT ?? 8480);
const TRANSIT_PORT = Number(env.TRANSIT_PORT ?? 4001);
const DATA = env.DATA_DIR ?? "/var/lib/holynotch";
const ADMIN_KEY = env.ADMIN_KEY ?? "";
const MIGRATE_KEY = env.MIGRATE_FIREBASE_KEY ?? ""; // Holm's public Firebase web API key: moves old accounts over
const MIGRATE_PROJECT = env.MIGRATE_FIREBASE_PROJECT ?? "holm-accounts";
// After this moment Holm's Firebase is never contacted again: no account moves, and an account that came by refresh
// token without ever signing in with its password here can only be used by the apps that are signed in already.
const MIGRATE_UNTIL = Date.parse(env.MIGRATE_UNTIL ?? "2027-02-01T00:00:00Z");
const migrationOpen = () => MIGRATE_KEY !== "" && Number.isFinite(MIGRATE_UNTIL) && Date.now() < MIGRATE_UNTIL;
const PAGE = fs.readFileSync(new URL("./admin.html", import.meta.url), "utf8");

// The admin page uses inline script/styles and Google Fonts. Everything else stays denied, and it cannot be framed.
const ADMIN_HEADERS = Object.freeze({
  "content-security-policy": "default-src 'none'; script-src 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
  "x-content-type-options": "nosniff",
  "x-frame-options": "DENY",
  "referrer-policy": "no-referrer",
});
const HTTP_RATE = 300, SOCKET_RATE = 30, ADMIN_ATTEMPTS = 5;
const positiveInt = (value, fallback) => {
  const n = Number(value);
  return Number.isSafeInteger(n) && n > 0 ? n : fallback;
};
const MAX_NOTES = positiveInt(env.MAX_NOTES, 250_000);
const MAX_FOLLOWERS = positiveInt(env.MAX_FOLLOWERS, 50_000), FOLLOWERS_PER_IP = positiveInt(env.FOLLOWERS_PER_IP, 64);
const MAX_OPEN_INBOXES_PER_DAY = positiveInt(env.MAX_OPEN_INBOXES_PER_DAY, 50_000);

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

// Older servers wrote one row per request. Rebuild once, transactionally, into unique buckets (one identified open per inbox/day).
if (!db.prepare("SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'events_bucket'").get()) {
  db.exec("BEGIN IMMEDIATE");
  try {
    db.exec(`
      DROP TABLE IF EXISTS events_bucket_migration;
      CREATE TABLE events_bucket_migration (time INTEGER NOT NULL, kind TEXT NOT NULL, inbox TEXT NOT NULL DEFAULT '', n INTEGER NOT NULL DEFAULT 1);
    `);
    const migrateEvents = db.prepare(`
      WITH buckets AS (
        SELECT (time / 60) * 60 AS minute, kind,
               CASE WHEN kind = 'open' THEN inbox ELSE '' END AS inbox, COALESCE(SUM(n), 0) AS n
        FROM events GROUP BY (time / 60) * 60, kind, CASE WHEN kind = 'open' THEN inbox ELSE '' END
      ), open_ranked AS (
        SELECT MAX(minute) AS time, (minute / 86400) * 86400 AS day, inbox, SUM(n) AS n,
               ROW_NUMBER() OVER (PARTITION BY (minute / 86400) * 86400 ORDER BY inbox) AS rn
        FROM buckets WHERE kind = 'open' AND inbox != '' GROUP BY (minute / 86400) * 86400, inbox
      ), normalized AS (
        SELECT minute AS time, kind, inbox, n FROM buckets WHERE kind != 'open'
        UNION ALL
        SELECT time, 'open', inbox, n FROM open_ranked WHERE rn <= ?
        UNION ALL
        SELECT MAX(time), 'open', '', SUM(n) FROM open_ranked WHERE rn > ? GROUP BY day
        UNION ALL
        SELECT minute, 'open', '', n FROM buckets WHERE kind = 'open' AND inbox = ''
      )
      INSERT INTO events_bucket_migration (time, kind, inbox, n)
        SELECT time, kind, inbox, SUM(n) FROM normalized GROUP BY time, kind, inbox
    `);
    migrateEvents.run(MAX_OPEN_INBOXES_PER_DAY, MAX_OPEN_INBOXES_PER_DAY);
    db.exec(`
      DROP TABLE events;
      ALTER TABLE events_bucket_migration RENAME TO events;
      CREATE INDEX events_time ON events (time);
      CREATE UNIQUE INDEX events_bucket ON events (time, kind, inbox);
    `);
    db.exec("COMMIT");
  } catch (e) {
    db.exec("ROLLBACK");
    throw e;
  }
}

const now = () => Math.floor(Date.now() / 1000);
const sha = (s) => crypto.createHash("sha256").update(s).digest("hex");
const randomText = (n, alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") =>
  Array.from(crypto.randomBytes(n), (b) => alphabet[b % alphabet.length]).join("");
const log = (...a) => console.log(new Date().toISOString(), ...a);

// ─────────────────────────── counts (admin) ───────────────────────────

const addEvent = db.prepare(`INSERT INTO events (time, kind, inbox, n) VALUES (?, ?, ?, ?)
  ON CONFLICT(time, kind, inbox) DO UPDATE SET n = events.n + excluded.n`);
let openEventDay = Math.floor(now() / 86400);
const openEventInboxes = new Set(db.prepare("SELECT DISTINCT inbox FROM events WHERE kind = 'open' AND inbox != '' AND time >= ? AND time < ?")
  .all(openEventDay * 86400, (openEventDay + 1) * 86400).map((row) => row.inbox)); // survive restarts within the same UTC day
function track(kind, topic = "", n = 1) {
  try {
    const t = now(), minute = Math.floor(t / 60) * 60, day = Math.floor(t / 86400);
    let inbox = "", newHash = null;
    // Only the first open for an inbox each day needs its identity, for the admin page's distinct active counts.
    if (kind === "open" && topic.startsWith("wnf-")) {
      if (openEventDay !== day) { openEventDay = day; openEventInboxes.clear(); }
      const hash = sha(topic).slice(0, 12);
      if (!openEventInboxes.has(hash) && openEventInboxes.size < MAX_OPEN_INBOXES_PER_DAY) {
        newHash = hash;
        inbox = hash;
      }
    }
    addEvent.run(minute, kind, inbox, n);
    if (newHash) openEventInboxes.add(newHash);
  } catch { /* never breaks a request */ }
}

let storedNotes = db.prepare("SELECT count(*) AS n FROM notes").get().n;
const followers = new Map(); // topic -> Set<WebSocket>
const followerCounts = new Map(); // IP -> active WebSocket count
let activeFollowers = 0, transitOpen = 0;

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
  // Hashing first keeps the compared values fixed-length; neither key length nor a matching prefix is observable.
  const digest = (value) => crypto.createHash("sha256").update(Buffer.from(value ?? "")).digest();
  return crypto.timingSafeEqual(digest(a), digest(b));
}

// ─────────────────────────── HTTP helpers ───────────────────────────

function send(res, status, body, headers = {}) {
  const text = typeof body === "string" ? body : JSON.stringify(body);
  let path = (res.req?.url ?? "").split("?", 1)[0];
  try { path = new URL(res.req?.url ?? "/", "http://holynotch").pathname; } catch { /* use the raw path */ }
  const admin = path === "/admin" || path.startsWith("/admin/");
  res.writeHead(status, { "content-type": typeof body === "string" ? "text/plain; charset=utf-8" : "application/json; charset=utf-8",
                          "cache-control": "no-store", ...headers, ...(admin ? ADMIN_HEADERS : {}) });
  res.end(text);
}

const MAX_BODY = 1_200_000; // a synced section is at most 900 000 characters
function readBody(req, limit = MAX_BODY) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0, settled = false;
    const fail = (e) => { if (!settled) { settled = true; reject(e); } };
    const onData = (c) => {
      size += c.length;
      if (size > limit) {
        // Stop buffering immediately. Drain without retaining data so the 413 can still reach the client.
        req.off("data", onData);
        req.off("end", onEnd);
        req.resume();
        fail(Object.assign(new Error("too large"), { status: 413 }));
        return;
      }
      chunks.push(c);
    };
    const onEnd = () => { if (!settled) { settled = true; resolve(Buffer.concat(chunks, size).toString("utf8")); } };
    req.on("data", onData);
    req.on("end", onEnd);
    req.on("error", fail);
  });
}

const clientIp = (req) => {
  const remote = req.socket.remoteAddress ?? "?";
  const localProxy = remote === "127.0.0.1" || remote === "::1" || remote === "::ffff:127.0.0.1";
  const forwarded = Array.isArray(req.headers["x-real-ip"]) ? req.headers["x-real-ip"][0] : req.headers["x-real-ip"];
  return localProxy && typeof forwarded === "string" && net.isIP(forwarded.trim()) ? forwarded.trim() : remote;
};

/** At most `limit` hits per key in `seconds`, with a hard bound on remembered keys. */
function limiter(limit, seconds, maxKeys = 10_000) {
  const hits = new Map();
  const clock = () => Date.now();
  setInterval(() => {
    const cutoff = clock() - seconds * 1000;
    for (const [k, list] of hits) {
      const kept = list.filter((x) => x > cutoff);
      if (kept.length) hits.set(k, kept); else hits.delete(k);
    }
  }, 60_000).unref();
  const listFor = (key, t) => (hits.get(key) ?? []).filter((x) => x > t - seconds * 1000);
  const check = (key) => {
    const t = clock(), list = listFor(key, t);
    if (list.length) { hits.set(key, list); return list.length < limit; }
    hits.delete(key);
    return hits.size < maxKeys;
  };
  const hit = (key) => {
    const t = clock(), list = listFor(key, t);
    if (list.length >= limit || (!list.length && hits.size >= maxKeys)) return false;
    list.push(t); hits.set(key, list);
    return true;
  };
  const consume = (key) => check(key) && hit(key);
  consume.check = check;
  consume.hit = hit;
  return consume;
}

const httpLimit = limiter(HTTP_RATE, 60), socketLimit = limiter(SOCKET_RATE, 60), adminLimit = limiter(ADMIN_ATTEMPTS, 600);

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
  if (!migrationOpen()) return null;
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
  if (!migrationOpen()) return null;
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
const MAX_INBOXES = 50_000, NEW_INBOXES_PER_IP = 20, WRITES_PER_IP_TOPIC = 20;
const isTopic = (t) => INBOX.test(t) || INVITE.test(t);
const isInvite = (t) => INVITE.test(t);
const postLimit = limiter(120, 60), topicLimit = limiter(60, 60, 20_000);
const ipTopicLimit = limiter(WRITES_PER_IP_TOPIC, 60, 20_000), newTopicLimit = limiter(NEW_INBOXES_PER_IP, 3600);

/** Null when a retained note may create this topic; reads and live-only notes create no SQLite inbox state. */
function newInboxStatus(req, topic) {
  if (db.prepare("SELECT 1 FROM notes WHERE topic = ? LIMIT 1").get(topic)) return null;
  const count = db.prepare("SELECT count(DISTINCT topic) AS n FROM notes").get().n;
  if (count >= MAX_INBOXES) return 503;
  return newTopicLimit(clientIp(req)) ? null : 429;
}

function parseSince(value, t = now()) {
  if (value == null || value === "") return null;
  if (value === "all") return 0;
  if (/^\d{1,12}$/.test(value)) return Number(value);
  const d = /^(\d{1,6})([smhd])$/.exec(value);
  return d ? t - Number(d[1]) * { s: 1, m: 60, h: 3600, d: 86400 }[d[2]] : null;
}

const kept = (topic, since) => since == null ? []
  : db.prepare("SELECT id, time, message FROM notes WHERE topic = ? AND time >= ? AND expires > ? ORDER BY seq").all(topic, since, now());
const insertNote = db.prepare("INSERT INTO notes (topic, id, time, expires, message) VALUES (?, ?, ?, ?, ?)");
const trimNotes = db.prepare("DELETE FROM notes WHERE topic = ? AND seq <= (SELECT seq FROM notes WHERE topic = ? ORDER BY seq DESC LIMIT 1 OFFSET ?)");

async function post(req, res, topic) {
  const declared = Number(req.headers["content-length"] ?? 0);
  if (Number.isFinite(declared) && declared > MAX_NOTE * 2) {
    req.resume(); // discard any arriving bytes without buffering them
    track("refused");
    return send(res, 413, { code: 413, error: "note too large" });
  }
  const ip = clientIp(req);
  if (!postLimit(ip)) { track("refused"); return send(res, 429, { code: 429, error: "too many notes, wait a minute" }); }
  let text;
  try { text = await readBody(req, MAX_NOTE * 2); } catch { track("refused"); return send(res, 413, { code: 413, error: "note too large" }); }
  if (text.length > MAX_NOTE || !(isInvite(topic) ? CARD : NOTE).test(text)) { track("refused"); return send(res, 400, { code: 400, error: "not a HolyNotch note" }); }
  const keep = !/^no$/i.test((req.headers["cache"] ?? req.headers["x-cache"] ?? "").toString());
  if (keep && storedNotes >= MAX_NOTES) { track("refused"); return send(res, 503, { code: 503, error: "note storage is full" }); }
  if (keep) {
    const status = newInboxStatus(req, topic);
    if (status) { track("refused"); return send(res, status, { code: status, error: status === 429 ? "too many new inboxes, wait an hour" : "too many active inboxes" }); }
  }
  if (!ipTopicLimit(`${ip}\0${topic}`)) { track("refused"); return send(res, 429, { code: 429, error: "this address sends no more notes to this inbox this minute" }); }
  if (!topicLimit(topic)) { track("refused"); return send(res, 429, { code: 429, error: "this inbox takes no more notes this minute" }); }
  const t = now(), note = { id: randomText(12), time: t, event: "message", topic, message: text };
  if (keep) {
    // The check, insert and same-topic ring trim are synchronous and transactional: the global cap cannot be exceeded,
    // and another topic's notes are never removed to make room.
    db.exec("BEGIN IMMEDIATE");
    try {
      insertNote.run(topic, note.id, t, t + (isInvite(topic) ? INVITE_TTL : INBOX_TTL), text);
      storedNotes++;
      const removed = trimNotes.run(topic, topic, isInvite(topic) ? INVITE_KEEP : INBOX_KEEP);
      storedNotes -= Number(removed.changes);
      db.exec("COMMIT");
    } catch (e) {
      db.exec("ROLLBACK");
      storedNotes = db.prepare("SELECT count(*) AS n FROM notes").get().n;
      throw e;
    }
  }
  const line = JSON.stringify(note);
  for (const ws of followers.get(topic) ?? []) { try { ws.send(line); } catch { /* closing */ } }
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
const transitWss = new WebSocketServer({ noServer: true, maxPayload: 4 * 1024 * 1024 });
function rejectUpgrade(socket, status, error) {
  const body = JSON.stringify({ code: status, error });
  socket.end(`HTTP/1.1 ${status} ${http.STATUS_CODES[status]}\r\nConnection: close\r\nContent-Type: application/json\r\n` +
             `Content-Length: ${Buffer.byteLength(body)}\r\nCache-Control: no-store\r\n\r\n${body}`);
}
function follow(req, socket, head, url, topic) {
  const ip = clientIp(req), current = followers.get(topic);
  if (current?.size >= SOCKETS) return rejectUpgrade(socket, 503, "too many followers");
  if ((followerCounts.get(ip) ?? 0) >= FOLLOWERS_PER_IP) return rejectUpgrade(socket, 503, "too many active followers from this address");
  if (activeFollowers >= MAX_FOLLOWERS) return rejectUpgrade(socket, 503, "too many active followers");
  // Following an empty topic creates only this bounded active connection, never a SQLite inbox or note row.
  wss.handleUpgrade(req, socket, head, (ws) => {
    let set = followers.get(topic);
    // Recheck the latest counts before creating a set, so a rejected newcomer cannot leave an empty topic behind.
    if ((set?.size ?? 0) >= SOCKETS || (followerCounts.get(ip) ?? 0) >= FOLLOWERS_PER_IP || activeFollowers >= MAX_FOLLOWERS) {
      ws.on("error", () => {});
      ws.close(1013, "too many followers");
      return;
    }
    if (!set) followers.set(topic, set = new Set());
    let counted = true;
    const release = () => {
      if (!counted) return;
      counted = false;
      set.delete(ws);
      if (set.size === 0) followers.delete(topic);
      activeFollowers--;
      const count = followerCounts.get(ip) ?? 0;
      if (count <= 1) followerCounts.delete(ip); else followerCounts.set(ip, count - 1);
      track("close", topic);
    };
    set.add(ws);
    activeFollowers++;
    followerCounts.set(ip, (followerCounts.get(ip) ?? 0) + 1);
    track("open", topic);
    ws.isAlive = true;
    ws.on("pong", () => { ws.isAlive = true; });
    ws.on("message", () => { /* followers only listen */ });
    ws.on("close", release);
    ws.on("error", () => { release(); ws.terminate(); });
    try {
      ws.send(JSON.stringify({ id: randomText(12), time: now(), event: "open", topic }));
      for (const n of kept(topic, parseSince(url.searchParams.get("since")))) ws.send(JSON.stringify({ ...n, event: "message", topic }));
    } catch { ws.terminate(); }
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
  const expired = db.prepare("DELETE FROM notes WHERE expires <= ?").run(t);
  storedNotes -= Number(expired.changes);
  db.prepare("DELETE FROM tokens WHERE expires <= ?").run(t);
  // Active-inbox counts need 30 days; charts and quotas need 14. Nothing reads the old 92-day history.
  db.prepare("DELETE FROM events WHERE (kind = 'open' AND time <= ?) OR (kind != 'open' AND time <= ?)").run(t - 31 * 86400, t - 14 * 86400);
}, 600_000).unref();

// ─────────────────────────── HTTP server ───────────────────────────

const server = http.createServer(async (req, res) => {
  if (!httpLimit(clientIp(req))) return send(res, 429, { code: 429, error: "too many requests, wait a minute" });
  try {
    const url = new URL(req.url, "http://holynotch");
    const p = url.pathname;
    if (p === "/" && req.method === "GET") return send(res, 200, "HolyNotch server: accounts, friend inboxes and transfers for HolyNotch.\n");
    if (p === "/health") return send(res, 200, { ok: true });

    if (p === "/admin" || p === "/admin/") return send(res, 200, PAGE, { "content-type": "text/html; charset=utf-8" });
    if (p === "/admin/stats") {
      const origin = req.headers.origin ?? "";
      const cors = /^https:\/\/holynotch\.elifox\.ch$|^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/i.test(origin)
        ? { "access-control-allow-origin": origin, "access-control-allow-headers": "authorization, content-type", "access-control-allow-methods": "GET, OPTIONS", "vary": "Origin" }
        : {};
      if (req.method === "OPTIONS") return send(res, 204, "", cors);
      if (!ADMIN_KEY) return send(res, 503, { error: "ADMIN_KEY isn't set on the server" }, cors);
      const ip = clientIp(req);
      if (!adminLimit.check(ip)) return send(res, 429, { error: "too many attempts, wait ten minutes" }, { "retry-after": "600", ...cors });
      if (!sameKey((req.headers.authorization ?? "").replace(/^Bearer /, ""), ADMIN_KEY)) {
        adminLimit.hit(ip);
        return send(res, 401, { error: "wrong key" }, cors);
      }
      try { return send(res, 200, stats(), cors); }
      catch (e) { log("admin stats failed:", e?.message ?? e); return send(res, 500, { error: "internal error" }, cors); }
    }

    if (req.method === "POST" && p.startsWith("/v1/accounts:")) {
      let body;
      try { body = JSON.parse(await readBody(req, 16_384)); }
      catch (e) { return authError(res, e?.status === 413 ? "REQUEST_TOO_LARGE" : "INVALID_JSON", e?.status ?? 400); }
      switch (p.slice("/v1/accounts:".length)) {
        case "signInWithPassword": return await signIn(req, res, body);
        case "signUp": return await signUp(req, res, body);
        case "delete": return deleteAccount(req, res, body);
        default: return authError(res, "OPERATION_NOT_ALLOWED", 404);
      }
    }
    if (req.method === "POST" && p === "/v1/token") {
      let form;
      try { form = await readBody(req, 16_384); }
      catch (e) { return authError(res, e?.status === 413 ? "REQUEST_TOO_LARGE" : "INVALID_JSON", e?.status ?? 400); }
      return await refresh(req, res, form);
    }
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
    if (!res.headersSent) {
      const status = e?.status === 413 ? 413 : 500;
      send(res, status, { error: { code: status, message: status === 413 ? "PAYLOAD_TOO_LARGE" : "INTERNAL", status: status === 413 ? "PAYLOAD_TOO_LARGE" : "INTERNAL" } });
    }
  }
});

server.on("upgrade", (req, socket, head) => {
  const ip = clientIp(req);
  if (!httpLimit(ip)) return rejectUpgrade(socket, 429, "too many requests, wait a minute");
  if (!socketLimit(ip)) return rejectUpgrade(socket, 429, "too many WebSocket connections, wait a minute");
  const url = new URL(req.url, "http://holynotch");
  const parts = url.pathname.split("/").filter(Boolean);
  if (parts.length === 2 && parts[1] === "ws" && isTopic(parts[0])) return follow(req, socket, head, url, parts[0]);
  if (parts.length === 2 && parts[0] === "transit" && /^[0-9a-f]{64}$/.test(parts[1])) return transitWs(req, socket, head, url, parts[1], ip);
  socket.destroy();
});

server.headersTimeout = 10_000;
server.requestTimeout = 30_000;
server.keepAliveTimeout = 5_000;
server.listen(HTTP_PORT, HTTP_HOST, () => log(`HolyNotch server on http://${HTTP_HOST}:${HTTP_PORT}`));

// ─────────────────────────── transit relay (file transfers) ───────────────────────────

const waiting = new Map(); // token -> { side, socket, ip, rest, paired }
const waitingByIp = new Map();
const TRANSIT_WAIT = 10 * 60 * 1000, TRANSIT_IDLE = 5 * 60 * 1000;
const MAX_WAITING = positiveInt(env.MAX_WAITING, 10_000), WAITING_PER_IP = positiveInt(env.WAITING_PER_IP, 8);
const MAX_TRANSIT_ACTIVE = positiveInt(env.MAX_TRANSIT_ACTIVE, 1_000), TRANSIT_ACTIVE_PER_IP = positiveInt(env.TRANSIT_ACTIVE_PER_IP, 32);
const activeTransitByIp = new Map();
const transitIpLimit = limiter(120, 60);

function releaseWaiting(token) {
  const entry = waiting.get(token);
  if (!entry) return;
  waiting.delete(token);
  const count = waitingByIp.get(entry.ip) ?? 0;
  if (count <= 1) waitingByIp.delete(entry.ip); else waitingByIp.set(entry.ip, count - 1);
}

function reserveWaiting(token, entry) {
  waiting.set(token, entry);
  waitingByIp.set(entry.ip, (waitingByIp.get(entry.ip) ?? 0) + 1);
}

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
    if (!paired && token && waiting.get(token)?.socket === socket) releaseWaiting(token);
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
      // Replacing a token releases its old slot first, so the established replacement behavior needs no extra capacity.
      const globalAfter = waiting.size - (other ? 1 : 0);
      const ipAfter = (waitingByIp.get(ip) ?? 0) - (other?.ip === ip ? 1 : 0);
      if (globalAfter >= MAX_WAITING || ipAfter >= WAITING_PER_IP) { socket.end("error relay busy\n"); return; }
      if (other) { releaseWaiting(token); other.socket.destroy(); }
      // Told when its partner arrives: from then on the pair's own timeouts apply.
      reserveWaiting(token, { side, socket, ip, rest, paired: () => { paired = true; clearTimeout(giveUp); } });
      return;
    }
    releaseWaiting(token);
    paired = true;
    clearTimeout(giveUp);
    other.paired();
    pair(other.socket, other.rest, socket, rest, other.ip, ip);
  };
  socket.on("data", onData);
});

function pair(a, aRest, b, bRest, aIp, bIp) {
  const ips = new Set([aIp, bIp]);
  if (transitOpen >= MAX_TRANSIT_ACTIVE || [...ips].some((ip) => (activeTransitByIp.get(ip) ?? 0) >= TRANSIT_ACTIVE_PER_IP)) {
    a.end("error relay busy\n");
    b.end("error relay busy\n");
    return;
  }
  transitOpen++;
  for (const ip of ips) activeTransitByIp.set(ip, (activeTransitByIp.get(ip) ?? 0) + 1);
  track("transfer");
  let bytes = 0, closed = 0;
  // One side closing (the receiver right after its "received") must not cut what is still on its way to the other:
  // the other side gets everything, then the end. Only an idle line or an error ends both at once.
  const counted = () => {
    if (++closed !== 2) return;
    transitOpen--;
    for (const ip of ips) {
      const count = activeTransitByIp.get(ip) ?? 0;
      if (count <= 1) activeTransitByIp.delete(ip); else activeTransitByIp.set(ip, count - 1);
    }
    track("transferBytes", "", bytes);
  };
  const kill = () => { a.destroy(); b.destroy(); };
  for (const [s, other] of [[a, b], [b, a]]) {
    if (typeof s.setTimeout === "function") s.setTimeout(TRANSIT_IDLE, kill);
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

/**
 * WebSocket transit relay over HTTPS port 443 (/transit/<64-hex-token>?side=<16-hex-side>):
 * pairs directly with both other WSS clients AND raw TCP 4001 clients on the same token.
 */
function transitWs(req, rawSocket, head, url, token, ip) {
  const side = url.searchParams.get("side") ?? "";
  if (!/^[0-9a-f]{1,32}$/.test(side)) return rejectUpgrade(rawSocket, 400, "bad side");
  if (!transitIpLimit(ip)) return rejectUpgrade(rawSocket, 429, "too many connections");
  transitWss.handleUpgrade(req, rawSocket, head, (ws) => {
    const stream = createWebSocketStream(ws);
    let paired = false;
    const giveUp = setTimeout(() => { try { stream.end("error no partner\n"); } catch {} }, TRANSIT_WAIT);
    stream.on("error", () => stream.destroy());
    stream.on("close", () => {
      clearTimeout(giveUp);
      if (!paired && waiting.get(token)?.socket === stream) releaseWaiting(token);
    });
    const other = waiting.get(token);
    if (!other || other.side === side) {
      const globalAfter = waiting.size - (other ? 1 : 0);
      const ipAfter = (waitingByIp.get(ip) ?? 0) - (other?.ip === ip ? 1 : 0);
      if (globalAfter >= MAX_WAITING || ipAfter >= WAITING_PER_IP) { stream.end("error relay busy\n"); return; }
      if (other) { releaseWaiting(token); other.socket.destroy(); }
      reserveWaiting(token, { side, socket: stream, ip, rest: Buffer.alloc(0), paired: () => { paired = true; clearTimeout(giveUp); } });
      return;
    }
    releaseWaiting(token);
    paired = true;
    clearTimeout(giveUp);
    other.paired();
    pair(other.socket, other.rest, stream, Buffer.alloc(0), other.ip, ip);
  });
}

transit.listen(TRANSIT_PORT, "0.0.0.0", () => log(`Transit relay on tcp ${TRANSIT_PORT}`));

for (const sig of ["SIGINT", "SIGTERM"]) process.on(sig, () => { log("stopping"); server.close(); transit.close(); db.close(); process.exit(0); });
