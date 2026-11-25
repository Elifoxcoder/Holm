# Holm relay

Holm's own inbox server for friend notes (invites, friend requests, chat, presence, file and stream offers): a
Cloudflare Worker with one Durable Object (SQLite) per inbox, on the Workers Free plan. It replaces ntfy.sh, whose
free tier allows about 250 notes a day per address, which Holm outgrew (`ntfy.sh refused the note (429)`).

## What it does

- Speaks the part of the ntfy protocol Holm uses: `POST /<topic>` and `GET /<topic>/json?poll=1&since=…`. So every
  Holm version can leave notes here; nothing else (no ntfy streaming, no other topics).
- `GET /<topic>/ws?since=…`: Holm 1.18+ follows its inbox with a hibernatable WebSocket. The Durable Object sleeps
  between notes (no duration billed); WebSocket pings keep the line and are free.
- Takes only Holm notes (`wnf1 …` for inboxes `wnf-…`, `hinv1 …` for invite codes `holm-inv-…`), 4 KB at most.
- Keeps an inbox's last 300 notes for 7 days (invite cards: 20, 12 hours). An alarm deletes expired notes; an empty
  inbox is deleted entirely. `Cache: no` (presence) is passed on live and never stored.
- Limits: 60 notes a minute per inbox, 120 a minute per address, 20 notes per address/topic, 20 new topics per
  address/hour, 300 HTTP requests per address/minute and 30 WebSocket upgrades per address/minute. Cloudflare's edge
  limiter is an additional approximate control; the Worker's own bounded counters work without it.
- An inbox accepts at most eight followers. A ninth is refused instead of displacing an established listener.
- Request bodies are read under a streaming byte cap; `Content-Length` is only an early rejection hint. Reads alone
  never create a notes table, so random polling cannot leave orphaned inbox schemas behind.
- Sees only ciphertext: notes are sealed end to end by Holm (`Friends.cs`, `FriendInvite.cs`). Inbox reads remain
  unauthenticated for compatibility with installed `WNF1.` clients; write quotas and follower limits reduce abuse.

## Admin page

`https://relay.holmnotch.ch/admin` shows anonymous counts: about how many Holms are online, active inboxes per day,
week and month, notes per day and how much of the Free plan's daily quota is used. Each request writes one Analytics
Engine data point (its kind and 12 hex characters of the inbox's SHA-256; no address, no note, kept 3 months).

Every `/admin` response includes a restrictive Content-Security-Policy, anti-framing, nosniff and no-referrer headers.
Failed key attempts are limited per address; key comparison hashes both values to a fixed length first.

Two secrets, set once in your own terminal (`relay/`), each asks for the value:
- `npx wrangler secret put ADMIN_KEY`: a long password of your choice; the page asks for it.
- `npx wrangler secret put CF_API_TOKEN`: an API token (dash.cloudflare.com › My Profile › API Tokens › Create
  Custom Token) with *Account › Account Analytics › Read* for this account, so the Worker can read the counts.

## Test locally

```
cd relay
npm install
npm test              # protocol, rate/body limits, admin headers and GET-only storage checks
npm run dev           # then, in another terminal, Holm's client against it:
$env:HOLM_RELAY_URL = "http://127.0.0.1:8787"; dotnet test Holm.Tests -c Release --filter "FullyQualifiedName~RelayWorkerTests"
```

## Set up (once, by someone with access to the Cloudflare account that has holmnotch.ch)

1. In your own terminal: `cd relay`, `npm install`, `npx wrangler login` (opens the browser to sign in to Cloudflare).
2. `npx wrangler deploy`. It creates the Worker `holm-relay`, the Durable Object class `Inbox` (SQLite) and the
   custom domain `relay.holmnotch.ch` (DNS record and certificate), all from `wrangler.toml`.
   If the deploy refuses the `[[ratelimits]]` block on the Free plan, delete it: the Worker works without it.
3. Check: `curl https://relay.holmnotch.ch/` answers "Holm relay…".
4. In the dashboard (Workers & Pages › holm-relay › Settings) leave the route on *fail closed*: over the daily limit
   Holm then gets an error and sends to ntfy instead.

Without step 2 nothing changes for users: Holm can't reach the relay and keeps using ntfy as before.

## Free plan budget (per day, reset 00:00 UTC)

| Limit | Free | One note costs |
| --- | --- | --- |
| Worker requests | 100,000 | 1 per note, 1 per inbox connection, 1 per invite lookup |
| Durable Object requests | 100,000 | the same again |
| DO rows written | 100,000 | 2 per kept note (insert, later delete); 0 for presence |
| DO rows read | 5,000,000 | up to 300 per inbox connection (catching up) |
| DO storage | 5 GB | at most ~1.2 MB per inbox (300 × 4 KB), mostly far less |

Over any limit the Free plan doesn't charge, it fails: Holm then sends to ntfy as before.
