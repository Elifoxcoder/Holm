// Small, dependency-free guards shared by the Worker routes and the admin page.

export const ADMIN_HEADERS = Object.freeze({
  "content-security-policy": "default-src 'none'; script-src 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
  "x-content-type-options": "nosniff",
  "x-frame-options": "DENY",
  "referrer-policy": "no-referrer",
});

/** Enforce the admin policy even on a 404 or an error produced outside stats.js. */
export function withAdminHeaders(response) {
  const headers = new Headers(response.headers);
  for (const [name, value] of Object.entries(ADMIN_HEADERS)) headers.set(name, value);
  return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
}

/** Cloudflare supplies this header; the fallback is for wrangler dev and direct bindings. */
export const requestIp = (request) => {
  const ip = request.headers.get("cf-connecting-ip")?.trim();
  return ip && ip.length <= 64 ? ip : "local";
};

/** A fixed-length digest comparison: key length and matching prefixes never reach the compare loop. */
export async function sameAdminKey(a, b) {
  const encode = (value) => new TextEncoder().encode(value ?? "");
  const [x, y] = await Promise.all([
    crypto.subtle.digest("SHA-256", encode(a)),
    crypto.subtle.digest("SHA-256", encode(b)),
  ]);
  const left = new Uint8Array(x), right = new Uint8Array(y);
  let diff = 0;
  for (let i = 0; i < left.length; i++) diff |= left[i] ^ right[i];
  return diff === 0;
}

/**
 * At most `limit` hits per key in `seconds`. `check`/`hit` allow throttling failures without charging successful
 * admin polling. The map is bounded, so arbitrary addresses and topics cannot grow it forever.
 */
export function createLimiter(limit, seconds, maxKeys = 10_000, clock = () => Date.now()) {
  const hits = new Map();
  let nextSweep = 0;
  const sweep = (t) => {
    if (t < nextSweep) return;
    nextSweep = t + seconds * 1000;
    const cutoff = t - seconds * 1000;
    for (const [key, list] of hits) {
      const kept = list.filter((time) => time > cutoff);
      if (kept.length) hits.set(key, kept); else hits.delete(key);
    }
  };
  const listFor = (key, t) => (hits.get(key) ?? []).filter((time) => time > t - seconds * 1000);
  const check = (key) => {
    const t = clock();
    sweep(t);
    const list = listFor(key, t);
    if (list.length) { hits.set(key, list); return list.length < limit; }
    hits.delete(key);
    return hits.size < maxKeys;
  };
  const hit = (key) => {
    const t = clock();
    sweep(t);
    const list = listFor(key, t);
    if (list.length >= limit || (!list.length && hits.size >= maxKeys)) return false;
    list.push(t);
    hits.set(key, list);
    return true;
  };
  return {
    check,
    hit,
    consume: (key) => check(key) && hit(key),
    size: () => hits.size,
  };
}

/** Content-Length is only an early hint; the stream reader below enforces the real byte limit. */
export function declaredTooLarge(request, limit) {
  const value = request.headers.get("content-length");
  if (value == null || value === "") return false;
  if (!/^\d+$/.test(value)) return true;
  const size = Number(value);
  return !Number.isSafeInteger(size) || size > limit;
}

/** Read at most `limit` bytes, cancelling an oversized stream instead of buffering the rest of it. */
export async function readText(request, limit) {
  if (!request.body) return "";
  const reader = request.body.getReader();
  const decoder = new TextDecoder();
  const chunks = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > limit) throw Object.assign(new Error("too large"), { status: 413 });
      const text = decoder.decode(value, { stream: true });
      if (text) chunks.push(text);
    }
    const tail = decoder.decode();
    if (tail) chunks.push(tail);
    return chunks.join("");
  } catch (e) {
    try { await reader.cancel(); } catch { /* already closed */ }
    throw e;
  }
}
