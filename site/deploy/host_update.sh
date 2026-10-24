#!/bin/bash
# Sur l'hôte Proxmox (lancé par deploy_site.ps1) : envoie le site dans le conteneur LXC de S3btify,
# puis y lance ct_update.sh.   Usage : bash host_update.sh <conteneur>
set -euo pipefail
CT="${1:?numéro du conteneur}"
SRC="$(cd "$(dirname "$0")" && pwd)"

pct status "$CT" | grep -q running || { echo "Le conteneur $CT n'est pas démarré."; exit 1; }
pct exec "$CT" -- mkdir -p /tmp/s3bopus-deploy
pct push "$CT" "$SRC/site.tgz" /tmp/s3bopus-deploy/site.tgz
pct push "$CT" "$SRC/ct_update.sh" /tmp/s3bopus-deploy/ct_update.sh
pct exec "$CT" -- bash /tmp/s3bopus-deploy/ct_update.sh
rm -rf "$SRC"
