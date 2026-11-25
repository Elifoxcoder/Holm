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

## Holm users and existing accounts

- **Accounts move by themselves.** An account that is still in Holm's Firebase project is copied here, with the same uid
  and its encrypted documents, the first time a HolyNotch app signs in (password) or refreshes its token (an app that
  was signed in stays signed in). Set `MIGRATE_FIREBASE_KEY` empty to switch this off.
- **Friends on Holm still reach you.** Your friend code now names this server, and HolyNotch sends it to all your friends
  once. Until a friend has it (or if they stay on Holm), they write to Holm's relay, which HolyNotch keeps following.
  Invite codes are left on both.
- **Files to and from Holm still work.** HolyNotch waits on this relay and on magic-wormhole's at the same time. Two
  HolyNotch apps meet here; a Holm app only ever comes to magic-wormhole's, and HolyNotch takes that after 2 seconds.

## Install or update (Ubuntu, nginx, certbot, Node 22+)

Copy `server/` (with `node_modules/`, if the machine has no npm) to the machine and run, as root:

```
./deploy/install.sh holynotch.example.ch
```

It creates the `holynotch` user, `/opt/holynotch`, `/var/lib/holynotch` (the SQLite database), `/etc/holynotch.env`
(settings, with a new random `ADMIN_KEY`), the `holynotch` systemd service, an nginx site with a Let's Encrypt
certificate, and opens TCP 4001 in ufw. Running it again updates the code and keeps the database and settings.

The admin key: `sudo grep ADMIN_KEY /etc/holynotch.env`. Logs: `journalctl -u holynotch -f`.

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
