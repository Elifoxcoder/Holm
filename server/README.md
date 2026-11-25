# HolyNotch server

Everything HolyNotch needs from the internet, as one Node service on your own machine. It replaces three things that
Holm uses:

| What | Holm uses | HolyNotch server |
| --- | --- | --- |
| **Account** (sign-in, synced settings, profile, friends, achievements) | Firebase Auth + Firestore (Google) | `/v1/accounts:*`, `/v1/token`, `/v1/projects/…/documents/…`: the same REST subset, so the apps only changed their base URL |
| **Friend inboxes** (chat, requests, invites, presence, file offers) | `relay.holmnotch.ch` (Cloudflare Worker, `relay/`) | `POST /<topic>`, `GET /<topic>/json?poll=1`, `GET /<topic>/ws`: the same protocol |
| **Transfers** (Beam codes, files to friends and your phone, screen streams) | `transit.magic-wormhole.io:4001` | TCP port 4001: the same transit protocol |
| **Admin page** | `relay.holmnotch.ch/admin` | `/admin`: online now, active inboxes, notes per day, accounts, transfers |

The server only ever sees ciphertext: the apps encrypt every synced section, note and transfer themselves. It sees
emails (for sign-in), inbox names and IP addresses (only to limit abuse, never stored).

## No Holm servers

Since desktop 2.4.1 and Android 1.4.0 the apps talk only to this server. They never contact Holm's relay
(`relay.holmnotch.ch`), ntfy.sh or magic-wormhole's transit relay.

- **Friends still on Holm can't be reached.** A friend whose inbox is on one of Holm's servers is shown as unreachable;
  add them again with a new invite code once they use HolyNotch too.
- **Accounts move until the end of January 2027.** An account that is still in Holm's Firebase project is copied here,
  with the same uid and its encrypted documents, the first time a HolyNotch app signs in (password) or refreshes its
  token. This is the server's only contact with Holm's services, and it stops at `MIGRATE_UNTIL` (default
  `2027-02-01T00:00:00Z`), or at once with `MIGRATE_FIREBASE_KEY` empty. After that, people who hadn't moved create a
  new account here. An account that moved by refresh token and never signed in with its password here can then only
  be used on the devices that are signed in.

## Install or update (Ubuntu, nginx, certbot, Node 22+)

Copy `server/` (with `node_modules/`, if the machine has no npm) to the machine and run, as root:

```
./deploy/install.sh holynotch.example.ch
```

It creates the `holynotch` user, `/opt/holynotch`, `/var/lib/holynotch` (the SQLite database), `/etc/holynotch.env`
(settings, with a new random `ADMIN_KEY`), the `holynotch` systemd service, an nginx site with a Let's Encrypt
certificate, and opens TCP 4001 in ufw. Running it again updates the code and keeps the database and settings.

The admin key: `sudo grep ADMIN_KEY /etc/holynotch.env`. Use a long random value. Logs: `journalctl -u holynotch -f`.

## Security limits

- Every HTTP route and WebSocket upgrade is rate-limited per address. Admin key failures have a separate ten-minute
  throttle, fixed-length constant-time key comparison, generic errors and restrictive security headers.
- JSON, form, note and document bodies are capped while streaming; `Content-Length` is only an early rejection hint.
- Stored notes are capped by `MAX_NOTES` (default 250,000). A full store rejects new notes instead of evicting another
  inbox's history. Expired notes and old admin counters are removed automatically.
- Active followers are capped globally by `MAX_FOLLOWERS` (default 50,000) and per address by `FOLLOWERS_PER_IP`
  (default 64). Extra followers are refused, never used to evict an established client.
- Pending transit pairs are capped by `MAX_WAITING` (default 10,000) and `WAITING_PER_IP` (default 8). Active
  pairs are capped by `MAX_TRANSIT_ACTIVE` (default 1,000) and `TRANSIT_ACTIVE_PER_IP` (default 32).
- Inbox reads remain unauthenticated for compatibility with installed `WNF1.` clients. Notes and transfers stay
  end-to-end encrypted; per-address/topic write quotas limit unauthenticated availability attacks.

If nginx or another local proxy fronts the service, configure it to overwrite `X-Real-IP`; the server only trusts that
header from a loopback peer.

## Moving to another name

The apps name the server in one place each: `Holm.Core/HolyNotchServer.cs` and `mobile/lib/net/server.dart`. The
first install used `holynotch.62-83-85-236.sslip.io`, a free name that always resolves to the VPS's address. To use
your own (e.g. `holynotch.elifox.ch`): point an A record at the VPS, run `certbot --nginx -d <name>` and add the name
to the nginx site's `server_name`, change both files, release both apps. Keep the old name working until every app has
updated.

## Test

```
cd server
npm install
npm test        # starts the server on free ports with an empty database
```
