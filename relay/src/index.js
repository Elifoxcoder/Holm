// Holm relay: the inbox server for Holm's friend notes, on Cloudflare Workers with one Durable Object per inbox.
//
// It speaks the small part of the ntfy protocol Holm uses, so older Holm versions can leave notes here too:
//   POST /<topic>                    leave a note (text body); "Cache: no" delivers it live without keeping it
//   GET  /<topic>/json?poll=1&since= the notes kept since a time (Unix seconds, or 12h, 30m, all), one JSON per line
// and one thing ntfy doesn't have, which only Holm 1.18+ uses:
//   GET  /<topic>/ws?since=          a WebSocket: an "open" event, the kept notes since then, then each new note live
//
// The notes are sealed by Holm end to end (Friends.cs, FriendInvite.cs); this server only sees ciphertext and the inbox
// name. It takes nothing but Holm notes, keeps an inbox's last notes for a week (an invite card for 12 hours) and sleeps
// between notes: a waiting WebSocket costs nothing while its Durable Object hibernates.

import { DurableObject } from "cloudflare:workers";
import { isInvite, isNoteFor, isTopic, parseSince, now, keptNotes, INBOX_TTL, INVITE_TTL, MAX_NOTE } from "./notes.js";
import { admin, track } from "./stats.js";
import { createLimiter, declaredTooLarge, readText, requestIp, withAdminHeaders } from "./security.js";

const INBOX_KEEP = 300, INVITE_KEEP = 20; // notes kept per inbox: the oldest go first
const TOPIC_PER_MINUTE = 60; // notes one inbox takes per minute
const WRITES_PER_IP_TOPIC = 20, POSTS_PER_IP = 120, HTTP_RATE = 300, SOCKET_RATE = 30;
const NEW_TOPICS_PER_IP = 20, MAX_SEEN_TOPICS = 50_000, TOPIC_MEMORY_TTL = 86_400_000;
const SOCKETS = 8; // one identity can follow its inbox from a few PCs (Holm account)

// Edge limits are per isolate; the Durable Object below adds the authoritative per-topic write and follower limits.
const httpLimit = createLimiter(HTTP_RATE, 60), socketLimit = createLimiter(SOCKET_RATE, 60);
const ipPostLimit = createLimiter(POSTS_PER_IP, 60), ipTopicLimit = createLimiter(WRITES_PER_IP_TOPIC, 60, 20_000);
const newTopicLimit = createLimiter(NEW_TOPICS_PER_IP, 3600);
const seenTopics = new Map();
let nextTopicSweep = 0;

function json(status, body) {
  return new Response(JSON.stringify(body) + "\n", { status, headers: { "content-type": "application/json" } });
}
const fail = (status, error) => json(status, { code: status, error });

function randomId() {
  const alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
  const bytes = crypto.getRandomValues(new Uint8Array(12));
  return Array.from(bytes, (b) => alphabet[b % 62]).join("");
}

/** Bound topic churn before it can instantiate more Durable Objects. Returns null, 429 or 503. */
function claimTopic(topic, ip) {
  const t = Date.now();
  if (t >= nextTopicSweep) {
    nextTopicSweep = t + 60_000;
    for (const [name, seen] of seenTopics) if (seen <= t - TOPIC_MEMORY_TTL) seenTopics.delete(name);
  }
  const seen = seenTopics.get(topic);
  if (seen != null && seen > t - TOPIC_MEMORY_TTL) return null;
  seenTopics.delete(topic);
  if (seenTopics.size >= MAX_SEEN_TOPICS) return 503;
  return newTopicLimit.consume(ip) ? (seenTopics.set(topic, t), null) : 429;
}

/** What a request was, for the counts (stats.js); null for the plain home page. */
function kindOf(request, url, response, topic) {
  if (!topic) return null;
  if (response.status >= 400) return "refused";
  if (request.method === "POST" || request.method === "PUT") {
    if (isInvite(topic)) return "invite";
    return /^no$/i.test(request.headers.get("cache") ?? request.headers.get("x-cache") ?? "") ? "live" : "note";
  }
  return url.pathname.endsWith("/ws") ? "open" : "poll";
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const adminPath = url.pathname === "/admin" || url.pathname.startsWith("/admin/");
    const finish = (response) => adminPath ? withAdminHeaders(response) : response;
    const ip = requestIp(request);
    if (!httpLimit.consume(ip)) return finish(fail(429, "too many requests, wait a minute"));
    try {
      if (adminPath) return finish((await admin(request, env, url.pathname)) ?? fail(404, "no such page"));
      if (request.method === "GET" && url.pathname.endsWith("/ws") && request.headers.get("upgrade")?.toLowerCase() === "websocket"
          && !socketLimit.consume(ip)) return fail(429, "too many WebSocket connections, wait a minute");
      const response = await route(request, env, url);
      const topic = url.pathname.split("/").filter(Boolean)[0];
      const kind = kindOf(request, url, response, topic);
      if (kind) await track(env, kind, topic);
      return response;
    } catch (e) {
      console.error(`relay request failed (${e?.status ?? "internal"})`);
      return finish(fail(500, "internal error"));
    }
  },
};

async function route(request, env, url) {
  const parts = url.pathname.split("/").filter(Boolean);
  if (parts.length === 0) return new Response("Holm relay: inboxes for Holm's friend notes.\n");
  const topic = parts[0];
  if (!isTopic(topic) || parts.length > 2) return fail(404, "no such inbox");
  const stub = env.INBOX.get(env.INBOX.idFromName(topic));
  const action = parts[1] ?? "";
  const ip = requestIp(request);

  if ((request.method === "POST" || request.method === "PUT") && action === "") {
    if (declaredTooLarge(request, MAX_NOTE * 2)) return fail(413, "note too large");
    // Keep Cloudflare's approximate per-location limiter, but also work when it is unavailable (including local tests).
    if (!ipPostLimit.consume(ip)) return fail(429, "too many notes, wait a minute");
    if (env.POST_LIMIT && !(await env.POST_LIMIT.limit({ key: ip })).success) return fail(429, "too many notes, wait a minute");
    let text;
    try { text = await readText(request, MAX_NOTE * 2); }
    catch (e) { if (e?.status === 413) return fail(413, "note too large"); throw e; }
    if (!isNoteFor(topic, text)) return fail(400, "not a Holm note");
    if (!ipTopicLimit.consume(`${ip}\0${topic}`)) return fail(429, "this address sends no more notes to this inbox this minute");
    const claimed = claimTopic(topic, ip);
    if (claimed) return fail(claimed, claimed === 429 ? "too many new inboxes, wait an hour" : "too many active inboxes");
    const keep = !/^no$/i.test(request.headers.get("cache") ?? request.headers.get("x-cache") ?? "");
    return stub.fetch(`https://inbox/${topic}`, { method: "POST", body: text, headers: { "x-keep": keep ? "1" : "0" } });
  }
  if (request.method === "GET" && action === "json") {
    if (!["1", "true", "yes"].includes(url.searchParams.get("poll") ?? "")) return fail(400, "only polling: use /ws to follow");
    const claimed = claimTopic(topic, ip);
    if (claimed) return fail(claimed, claimed === 429 ? "too many new inboxes, wait an hour" : "too many active inboxes");
    return stub.fetch(`https://inbox/${topic}/json?since=${encodeURIComponent(url.searchParams.get("since") ?? "")}`);
  }
  if (request.method === "GET" && action === "ws") {
    if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return fail(426, "a WebSocket only");
    const claimed = claimTopic(topic, ip);
    if (claimed) return fail(claimed, claimed === 429 ? "too many new inboxes, wait an hour" : "too many active inboxes");
    return stub.fetch(request);
  }
  return fail(405, "not here");
}

/** One inbox: its notes in SQLite, and the WebSockets following it. */
export class Inbox extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.ready = false;
    this.posts = []; // times of the last minute's notes; starts empty after the object slept, which is fine
  }

  table() {
    if (this.ready) return;
    this.sql.exec("CREATE TABLE IF NOT EXISTS notes (seq INTEGER PRIMARY KEY, id TEXT NOT NULL, time INTEGER NOT NULL, expires INTEGER NOT NULL, message TEXT NOT NULL)");
    this.ready = true;
  }

  async fetch(request) {
    const url = new URL(request.url);
    const [topic, action] = url.pathname.split("/").filter(Boolean);
    if (request.method === "POST") {
      let text;
      try { text = await readText(request, MAX_NOTE * 2); }
      catch (e) { if (e?.status === 413) return fail(413, "note too large"); throw e; }
      return this.post(topic, text, request.headers.get("x-keep") === "1");
    }
    const since = parseSince(url.searchParams.get("since"));
    if (action === "json") return new Response(this.kept(since).map((n) => JSON.stringify({ ...n, event: "message", topic }) + "\n").join(""),
                                               { headers: { "content-type": "application/x-ndjson" } });
    return this.follow(topic, since);
  }

  async post(topic, text, keep) {
    const t = now();
    this.posts = this.posts.filter((p) => p > t - 60);
    if (this.posts.length >= TOPIC_PER_MINUTE) return fail(429, "this inbox takes no more notes this minute");
    this.posts.push(t);
    const note = { id: randomId(), time: t, event: "message", topic, message: text };
    const line = JSON.stringify(note);
    for (const ws of this.ctx.getWebSockets()) {
      try { ws.send(line); } catch { /* closing */ }
    }
    if (keep) {
      this.table();
      const ttl = isInvite(topic) ? INVITE_TTL : INBOX_TTL;
      const { seq } = this.sql.exec("INSERT INTO notes (id, time, expires, message) VALUES (?, ?, ?, ?) RETURNING seq",
                                    note.id, t, t + ttl, text).one();
      const keepCount = isInvite(topic) ? INVITE_KEEP : INBOX_KEEP;
      if (seq > keepCount) this.sql.exec("DELETE FROM notes WHERE seq <= ?", seq - keepCount); // by primary key: reads only what goes
      if (!this.alarmSet) {
        this.alarmSet = (await this.ctx.storage.getAlarm()) != null;
        if (!this.alarmSet) { await this.ctx.storage.setAlarm((t + ttl + 60) * 1000); this.alarmSet = true; }
      }
    }
    return json(200, note);
  }

  /** The kept notes since a time, oldest first; none for "only new ones". Reads never create storage. */
  kept(since) {
    return keptNotes(this.sql, since);
  }

  follow(topic, since) {
    const open = this.ctx.getWebSockets();
    // A full inbox refuses the newcomer; established followers must never be displaced.
    if (open.length >= SOCKETS) return fail(503, "too many followers");
    const [client, server] = Object.values(new WebSocketPair());
    this.ctx.acceptWebSocket(server); // hibernatable: no cost while nothing happens
    server.serializeAttachment({ topic });
    server.send(JSON.stringify({ id: randomId(), time: now(), event: "open", topic }));
    for (const n of this.kept(since)) server.send(JSON.stringify({ ...n, event: "message", topic }));
    return new Response(null, { status: 101, webSocket: client });
  }

  webSocketMessage() { /* followers only listen; keepalives are protocol pings, answered without waking us */ }

  async webSocketClose(ws, code) {
    try { ws.close(code === 1005 ? 1000 : code, "bye"); } catch { /* already closed */ }
    await track(this.env, "close", ws.deserializeAttachment()?.topic);
  }

  async webSocketError(ws) {
    await track(this.env, "close", ws.deserializeAttachment()?.topic);
  }

  /** Expired notes go; an inbox with none left is deleted entirely, so it takes no storage. */
  async alarm() {
    this.alarmSet = false;
    this.table();
    this.sql.exec("DELETE FROM notes WHERE expires <= ?", now());
    const next = this.sql.exec("SELECT MIN(expires) AS e FROM notes").one().e;
    if (next != null) {
      await this.ctx.storage.setAlarm((next + 60) * 1000);
      this.alarmSet = true;
    } else {
      await this.ctx.storage.deleteAll();
      this.ready = false;
    }
  }
}
