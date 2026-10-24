#!/bin/bash
# Essai sans écran : Xvfb + xdotool. Copie un fichier et un dossier par la file, vérifie le contenu, puis supprime.
set -e
D=$(mktemp -d); trap 'kill $XV $AP 2>/dev/null; rm -rf $D' EXIT
mkdir -p $D/a/sub $D/b; echo hello > $D/a/f.txt; head -c 3000000 /dev/urandom > $D/a/sub/big.bin
Xvfb :77 -screen 0 1280x800x24 & XV=$!; sleep 1; export DISPLAY=:77
./s3bopus $D/a $D/b & AP=$!; sleep 1
W=$(xdotool search --name S3bopus | head -1); xdotool windowfocus $W
k() { xdotool key --window $W "$@"; sleep 0.3; }
k Down; k space; k space; k F5; sleep 3   # coche sub puis f.txt
cmp $D/a/f.txt $D/b/f.txt; cmp $D/a/sub/big.bin $D/b/sub/big.bin
import -window root ${SHOT:-/tmp/s3shot.png} 2>/dev/null || true
k Tab; k Down; k F8; k o; k Return; sleep 1
[ ! -e $D/b/sub ] || { echo "ECHEC suppression"; exit 1; }
kill -0 $AP && echo "OK : copie, comparaison et suppression"
