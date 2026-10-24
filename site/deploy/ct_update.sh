#!/bin/bash
# Sur le serveur Caddy de s3btify.com (lancé par deploy_site.ps1) : installe le site S3bopus sous /s3bopus,
# son compteur (port 8091) et la configuration Caddy. Ne touche ni au site S3btify ni à son compteur.
set -euo pipefail
D=/tmp/s3bopus-deploy
WEB=/usr/share/caddy/s3bopus
CADDYFILE=/etc/caddy/Caddyfile
SNIPPET=/etc/caddy/s3bopus.caddy
COUNTER_DIR=/opt/s3bopus-counter
DATA=/var/lib/s3bopus-counter

tar -xzf "$D/site.tgz" -C "$D"

# 1) Le site (les téléchargements d'avant sont remplacés par ceux de cette version)
mkdir -p "$WEB/downloads"
cp -r "$D/site/www/." "$WEB/"
find "$WEB" -type d -exec chmod 755 {} +
find "$WEB" -type f -exec chmod 644 {} +
echo "   site mis à jour dans $WEB"

# 2) Le compteur (service systemd, compte système sans droits)
command -v python3 >/dev/null || { echo "python3 absent : apt install python3"; exit 1; }
install -D -m 644 "$D/site/counter.py" "$COUNTER_DIR/counter.py"
id s3bopus-counter >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin s3bopus-counter
mkdir -p "$DATA"; chown -R s3bopus-counter: "$DATA"; chmod 750 "$DATA"
# Même adresse de contact que le compteur de S3btify
CONTACT=$(sed -n 's/.*--contact \([^ ]*\).*/\1/p' /etc/systemd/system/s3btify-counter.service 2>/dev/null | head -1)
CONTACT_ARG=${CONTACT:+--contact $CONTACT}
if command -v systemctl >/dev/null && [ -d /run/systemd/system ]; then
  cat > /etc/systemd/system/s3bopus-counter.service <<EOF
[Unit]
Description=Compteur de visites et de téléchargements du site S3bopus
After=network.target

[Service]
ExecStart=/usr/bin/python3 $COUNTER_DIR/counter.py --port 8091 --data $DATA/counts.json $CONTACT_ARG
User=s3bopus-counter
NoNewPrivileges=yes
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl enable --quiet s3bopus-counter
  systemctl restart s3bopus-counter
  echo "   compteur : service s3bopus-counter (totaux dans $DATA)"
else
  pkill -f "$COUNTER_DIR/counter.py" || true
  nohup runuser -u s3bopus-counter -- python3 "$COUNTER_DIR/counter.py" --port 8091 --data "$DATA/counts.json" $CONTACT_ARG >/dev/null 2>&1 &
  echo "   compteur lancé (pas de systemd : à relancer après un redémarrage)"
fi

# 3) Caddy : un fichier à part, importé une seule fois avant le « handle { » de fin du Caddyfile
install -D -m 644 "$D/site/s3bopus.caddy" "$SNIPPET"
if ! grep -q "s3bopus.caddy" "$CADDYFILE"; then
  cp "$CADDYFILE" "$CADDYFILE.bak-$(date +%Y%m%d-%H%M%S)"
  python3 - "$CADDYFILE" <<'PY'
import re, sys
path = sys.argv[1]
text = open(path, encoding="utf-8").read()
m = re.search(r"^([ \t]*)handle \{[ \t]*$", text, re.M)
if not m:
    sys.exit("Caddyfile : « handle { » de fin introuvable, ajoutez à la main : import /etc/caddy/s3bopus.caddy")
text = text[:m.start()] + m.group(1) + "import /etc/caddy/s3bopus.caddy\n\n" + text[m.start():]
open(path, "w", encoding="utf-8").write(text)
PY
fi
if ! caddy validate --config "$CADDYFILE" --adapter caddyfile >/dev/null 2>&1; then
  echo "Caddyfile invalide : ancienne version remise"
  cp "$(ls -t "$CADDYFILE".bak-* | head -1)" "$CADDYFILE"
  exit 1
fi
if command -v systemctl >/dev/null && systemctl is-active --quiet caddy; then
  systemctl reload caddy
else
  caddy reload --config "$CADDYFILE" --adapter caddyfile
fi
echo "   Caddy rechargé"

# 4) Vérification (lecture seule : n'ajoute pas de visite)
sleep 1
python3 - <<'PY'
import json, urllib.request
for name, url in (("compteur", "http://127.0.0.1:8091/count"), ("via Caddy", "http://127.0.0.1/s3bopus/count")):
    try:
        with urllib.request.urlopen(url, timeout=5) as r:
            print(f"   {name} : OK {json.load(r)}")
    except Exception as exc:
        print(f"   {name} : ERREUR {exc}")
try:
    with urllib.request.urlopen("http://127.0.0.1/s3bopus/", timeout=5) as r:
        print("   page /s3bopus/ :", r.status)
except Exception as exc:
    print("   page /s3bopus/ : ERREUR", exc)
PY
rm -rf "$D"
