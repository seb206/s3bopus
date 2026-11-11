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

# 4. boîte de sélection partie hors du nom (colonne date / vide) : mode ligne puis mode nom
mkdir -p $D/h/.config $D/s1 $D/s2 $D/s3 $D/s4; for f in a b c; do echo $f > $D/s1/$f.txt; done
band() { xdotool mousemove 500 300 mousedown 1 mousemove 505 200 mousemove "$1" 50 mouseup 1; sleep 0.3; }
export HOME=$D/h
start $D/s1 $D/s2; band 520; k F5; sleep 2
[ "$(ls $D/s2 | wc -l)" = 3 ] || { echo "ECHEC boîte en mode ligne"; exit 1; }
stop
printf 'rowsel=0\n' > $D/h/.config/s3bopus.conf
start $D/s1 $D/s3; band 520; k F5; sleep 2
[ "$(ls $D/s3 | wc -l)" = 0 ] || { echo "ECHEC mode nom : la boîte hors des noms sélectionne"; exit 1; }
band 20; k F5; sleep 2
[ "$(ls $D/s3 | wc -l)" = 3 ] || { echo "ECHEC mode nom : la boîte sur les noms ne sélectionne pas"; exit 1; }
stop; echo "OK boîte de sélection (ligne et nom)"

# 5. tar forgés (taille négative en base 256, ligne pax plus longue que son bloc) : ni plantage ni boucle ; dossier copié dans lui-même refusé
mkdir -p $D/t $D/t2 $D/sc/in; echo z > $D/t/z.txt
python3 - $D/t <<'PY'
import sys
d = sys.argv[1]
def hdr(name, size, typ):
    h = bytearray(512); h[:len(name)] = name; h[124:136] = size; h[156:157] = typ
    return bytes(h)
octal = lambda n: b'%011o\0' % n
open(d + '/a-neg.tar', 'wb').write(hdr(b'x', b'\xff' * 12, b'x') + bytes(1024))                    # en-tête pax de taille -1
open(d + '/b-neg.tar', 'wb').write(hdr(b'f', b'\xff' * 10 + b'\xfc\x00', b'0') + bytes(1024))      # fichier de taille -1024
pax = b'999 path=abc\n'
open(d + '/c-pax.tar', 'wb').write(hdr(b'p', octal(len(pax)), b'x') + pax.ljust(512, b'\0') + hdr(b'f', octal(0), b'0') + bytes(1024))
PY
start $D/t $D/t2
k Down; k Return; k Down; k Down; k Return; k Down; k Down; k Down; k Return; k BackSpace; k End; k F5; sleep 2
[ -e $D/t2/z.txt ] || { echo "ECHEC tar forgé (plantage ou boucle)"; exit 1; }
stop
start $D/sc $D/sc/in; k Down; k F5; sleep 2
[ ! -e $D/sc/in/in ] || { echo "ECHEC dossier copié dans lui-même"; exit 1; }
stop; echo "OK tar forgés et copie dans soi-même"

# 6. mise à jour : une version plus récente publiée apparaît dans le titre, pas une version égale ni un texte invalide
V=$(./s3bopus --version | sed 's/S3bopus \([0-9.]*\).*/\1/')
upd() { printf '%s\n' "$1" > $D/version.txt; S3BOPUS_UPDATE_URL=file://$D/version.txt start $D/s1 $D/s2; sleep 1; TITLE=$(xdotool getwindowname $W); stop; }
upd "linux=99.0.0"; echo "$TITLE" | grep -q "99.0.0" || { echo "ECHEC mise à jour non signalée : $TITLE"; exit 1; }
upd "linux=$V"; [ "$TITLE" = "S3bopus $V" ] || { echo "ECHEC même version signalée : $TITLE"; exit 1; }
upd 'linux=99.0.0; rm -rf x'; [ "$TITLE" = "S3bopus $V" ] || { echo "ECHEC texte invalide accepté : $TITLE"; exit 1; }
echo "OK vérification de mise à jour"
