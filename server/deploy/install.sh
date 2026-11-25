#!/usr/bin/env bash
# Installs or updates the HolyNotch server on this machine (Ubuntu, nginx, certbot, Node 22+), as root.
#   ./install.sh <host name>       e.g. ./install.sh holynotch.elifox.ch
# Run it from the server/ folder copied to the machine. Safe to run again: keeps the database and /etc/holynotch.env.
set -euo pipefail
HOST="${1:?host name, e.g. holynotch.elifox.ch}"
SRC="$(cd "$(dirname "$0")/.." && pwd)"

id holynotch >/dev/null 2>&1 || useradd --system --home /var/lib/holynotch --shell /usr/sbin/nologin holynotch
install -d -o holynotch -g holynotch -m 700 /var/lib/holynotch
install -d -m 755 /opt/holynotch
cp "$SRC/holynotch-server.mjs" "$SRC/admin.html" "$SRC/package.json" /opt/holynotch/
# ws is plain JavaScript: npm installs it, or the node_modules copied along with server/ is used.
if command -v npm >/dev/null; then (cd /opt/holynotch && npm install --omit=dev --no-audit --no-fund --silent)
else rm -rf /opt/holynotch/node_modules && cp -r "$SRC/node_modules" /opt/holynotch/; fi

if [ ! -f /etc/holynotch.env ]; then
  umask 077
  cat > /etc/holynotch.env <<ENV
# HolyNotch server settings (server/README.md)
HTTP_HOST=127.0.0.1
HTTP_PORT=8480
TRANSIT_PORT=4001
DATA_DIR=/var/lib/holynotch
# The key for https://$HOST/admin
ADMIN_KEY=$(openssl rand -hex 24)
# Holm's public Firebase web API key: an account still at Holm moves here at its first sign-in. Empty: no moving.
MIGRATE_FIREBASE_KEY=AIzaSyBlVxpL7NiLGM-vDQ_VJqnHj8tWrP8qtmM
MIGRATE_FIREBASE_PROJECT=holm-accounts
ENV
  chmod 600 /etc/holynotch.env
fi

install -m 644 "$SRC/deploy/holynotch.service" /etc/systemd/system/holynotch.service
systemctl daemon-reload
systemctl enable --now holynotch
systemctl restart holynotch

if [ ! -f /etc/nginx/sites-available/holynotch.conf ]; then
  sed "s/HOLYNOTCH_HOST/$HOST/" "$SRC/deploy/nginx-holynotch.conf" > /etc/nginx/sites-available/holynotch.conf
  ln -sf /etc/nginx/sites-available/holynotch.conf /etc/nginx/sites-enabled/holynotch.conf
  nginx -t
  systemctl reload nginx
  certbot --nginx --non-interactive --redirect -d "$HOST"
fi

command -v ufw >/dev/null && ufw allow 4001/tcp comment "HolyNotch transfers" >/dev/null || true
sleep 1
curl -fsS "http://127.0.0.1:8480/health" && echo
echo "HolyNotch server is up: https://$HOST/  (admin key: sudo grep ADMIN_KEY /etc/holynotch.env)"
