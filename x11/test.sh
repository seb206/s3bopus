#!/bin/bash
# Essai sans écran : Xvfb + xdotool + xclip.
#  1. copie par la file + comparaison, suppression
#  2. archives .tgz et .zip : entrer, extraire un dossier, un nom « .. » piégé n'est pas extrait
#  3. collage Ctrl+V d'un texte puis d'une grosse image (transfert INCR)
set -e
export LANG=C.UTF-8
D=$(mktemp -d); trap 'kill $XV $AP 2>/dev/null; rm -rf $D' EXIT
Xvfb :77 -screen 0 1280x800x24 >/dev/null 2>&1 & XV=$!; sleep 1; export DISPLAY=:77
start() { ./s3bopus "$@" & AP=$!; sleep 1; W=$(xdotool search --name S3bopus | head -1); xdotool windowfocus $W; }
stop() { kill $AP; wait $AP 2>/dev/null || true; }
k() { xdotool key --window $W "$@"; sleep 0.3; }

# 1. copie / suppression
mkdir -p $D/a/sub $D/b; echo hello > $D/a/f.txt; head -c 3000000 /dev/urandom > $D/a/sub/big.bin
start $D/a $D/b
k Down; k space; k space; k F5; sleep 3   # coche sub puis f.txt
cmp $D/a/f.txt $D/b/f.txt; cmp $D/a/sub/big.bin $D/b/sub/big.bin
import -window root ${SHOT:-/tmp/s3shot.png} 2>/dev/null || true
k Tab; k Down; k F8; k o; k Return; sleep 1
[ ! -e $D/b/sub ] || { echo "ECHEC suppression"; exit 1; }
stop; echo "OK copie et suppression"

# 2. archives
mkdir -p $D/src/proj/deep $D/z $D/b2; echo "hello arch" > $D/src/proj/hello.txt; echo deep > $D/src/proj/deep/x.txt; head -c 200000 /dev/urandom > $D/src/proj/rand.bin
tar -czf $D/z/z.tgz -C $D/src proj
python3 - $D <<'PY'
import sys, zipfile, os
d = sys.argv[1]
with zipfile.ZipFile(d + '/z/z.zip', 'w', zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(d + '/src/proj'):
        for f in files:
            full = os.path.join(root, f); z.write(full, os.path.relpath(full, d + '/src'))
    z.writestr('proj/../../evil.txt', 'piege')
PY
start $D/z $D/b2
k Down; k Return; k Down; k F5; sleep 2          # z.tgz : entrer, proj, extraire
diff -r $D/src/proj $D/b2/proj
k BackSpace; k Down; k Return; k Down; k F5; sleep 2   # retour dans le dossier, z.zip : même chose
diff -r $D/src/proj "$D/b2/proj (2)"
[ ! -e $D/evil.txt ] && [ ! -e $D/b2/evil.txt ] || { echo "ECHEC zip-slip"; exit 1; }
stop; echo "OK archives tgz et zip"

# 3. presse-papiers
export DISPLAY=:77
mkdir -p $D/c1 $D/c2
printf 'bonjour presse-papiers\n' | xclip -selection clipboard -i
start $D/c1 $D/c2
k ctrl+v; sleep 1
cmp $D/c1/"Pasted text.txt" <(printf 'bonjour presse-papiers\n')
convert -size 1600x1200 plasma:fractal $D/p.png; echo "image $(stat -c %s $D/p.png) octets"
xclip -selection clipboard -t image/png -i $D/p.png; sleep 0.5
k ctrl+v; sleep 3
cmp $D/p.png "$D/c1/Pasted image.png"
stop; echo "OK collage texte et image"
