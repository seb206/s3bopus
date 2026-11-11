#!/bin/bash
# Essai sans écran : Xvfb + xdotool + xclip.
#  1. copie par la file + comparaison, suppression
#  2. archives .tgz et .zip : entrer, extraire un dossier, un nom « .. » piégé n'est pas extrait
#  3. collage Ctrl+V d'un texte puis d'une grosse image (transfert INCR)
set -e
export LANG=C.UTF-8
D=$(mktemp -d); trap 'kill $XV $AP 2>/dev/null; rm -rf $D' EXIT
mkdir -p $D/h/.config; export HOME=$D/h   # jamais le vrai dossier personnel : le programme y écrit favoris, session et réglages
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
mkdir -p $D/s1 $D/s2 $D/s3 $D/s4; for f in a b c; do echo $f > $D/s1/$f.txt; done
band() { xdotool mousemove 500 300 mousedown 1 mousemove 505 200 mousemove "$1" 50 mouseup 1; sleep 0.3; }
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

# 7. à propos : F1 ouvre, une touche ferme sans agir (F8 ne doit rien supprimer), puis le bouton « ? »
mkdir -p $D/s5; start $D/s1 $D/s5; k F1; import -window root ${ABOUT:-/tmp/s3about.png} 2>/dev/null || true
k F8; k Down; k F5; sleep 1
[ "$(ls $D/s1 | wc -l)" = 3 ] && [ -e $D/s5/a.txt ] || { echo "ECHEC à propos : la touche de fermeture a agi, ou le clavier est resté bloqué"; exit 1; }
xdotool mousemove $((1100 - 431)) 690 click 1; sleep 0.3; xdotool mousemove 300 300 click 1; sleep 0.3; kill -0 $AP
stop; echo "OK à propos"

# 8. favoris : F4 ajoute le dossier courant, F4 puis le favori y retourne, F3 ouvre le plus ouvert, chaque ouverture est comptée
FAVF=$D/h/.config/s3bopus.fav; rm -f $FAVF
start $D/s1 $D/s2
k BackSpace; k F4; k Return                       # dossier parent ($D), ajouté aux favoris
grep -qxP "F\t$D" $FAVF || { echo "ECHEC favori non enregistré"; cat $FAVF; exit 1; }
k Down; k Return; k F4; k Down; k Return          # entre dans un sous-dossier, puis revient par le favori
k F3; import -window root ${FAVSHOT:-/tmp/s3fav.png} 2>/dev/null || true; k Return   # le plus ouvert : $D
grep -qxP "V\t3\t$D" $FAVF || { echo "ECHEC compteur d'ouvertures"; cat $FAVF; exit 1; }
k F4; k Return                                    # on est dans $D : le retirer des favoris
! grep -qP "^F\t" $FAVF || { echo "ECHEC favori non retiré"; cat $FAVF; exit 1; }
stop; echo "OK favoris"

# 9. conflits : la destination contient déjà les noms -> question, puis remplacer / ignorer / garder les deux / déplacer en remplaçant
fill() { rm -rf $D/k2; mkdir -p $D/k2/sub; echo old > $D/k2/f.txt; echo old > $D/k2/sub/g.txt; echo keep > $D/k2/sub/keep.txt; }
mkdir -p $D/k1/sub $D/k3; echo new > $D/k1/f.txt; echo new > $D/k1/sub/g.txt; echo moved > $D/k3/f.txt; fill
start $D/k1 $D/k2
k ctrl+a; k F5; k r; k Return; sleep 2
[ "$(cat $D/k2/f.txt)$(cat $D/k2/sub/g.txt)$(cat $D/k2/sub/keep.txt)" = newnewkeep ] || { echo "ECHEC remplacer (fusion du dossier)"; exit 1; }
[ -z "$(find $D/k2 -name '*.s3bopus-part')" ] || { echo "ECHEC remplacer : fichier temporaire resté"; exit 1; }
fill; k ctrl+a; k F5; k i; k Return; sleep 2
[ "$(cat $D/k2/f.txt)$(cat $D/k2/sub/g.txt)" = oldold ] || { echo "ECHEC ignorer"; exit 1; }
fill; k ctrl+a; k F5; k g; k Return; sleep 2
[ "$(cat $D/k2/f.txt)$(cat "$D/k2/f (2).txt")$(cat "$D/k2/sub (2)/g.txt")" = oldnewnew ] || { echo "ECHEC garder les deux"; exit 1; }
[ "$(cat $D/k1/f.txt)" = new ] || { echo "ECHEC la copie a touché la source"; exit 1; }
stop
fill; start $D/k3 $D/k2; k Down; k F6; k r; k Return; sleep 2
[ "$(cat $D/k2/f.txt)" = moved ] && [ ! -e $D/k3/f.txt ] || { echo "ECHEC déplacer en remplaçant"; exit 1; }
stop; echo "OK conflits"

# 10. saisie d'un chemin, tri par colonne, session : Ctrl+L va dans un dossier, clic sur « Taille », F10 enregistre, relance sans argument
SESS=$D/h/.config/s3bopus.session; rm -f $SESS $FAVF
start $D/s1 $D/s2
k ctrl+l; k ctrl+u; xdotool type --window $W "$D/k1"; sleep 0.3; k Return
xdotool mousemove 380 57 click 1; sleep 0.3
import -window root ${SORTSHOT:-/tmp/s3sort.png} 2>/dev/null || true
k ctrl+t; xdotool key --window $W F10 2>/dev/null || true; xdotool keyup F10; sleep 0.5
# ^ F10 quitte : xdotool perd la fenêtre avant de relâcher la touche, qui resterait enfoncée (et se répéterait dans l'instance relancée)
[ "$(stat -c %a $SESS)" = 600 ] || { echo "ECHEC session lisible par les autres utilisateurs"; exit 1; }
grep -qxP "S\t0\t1\t1" $SESS || { echo "ECHEC tri non enregistré"; cat $SESS; exit 1; }
[ "$(grep -cP "^T\t0\t[01]\t$D/k1$" $SESS)" = 2 ] && grep -qxP "T\t1\t1\t$D/s2" $SESS || { echo "ECHEC onglets non enregistrés"; cat $SESS; exit 1; }
start; k F4; k Return; stop                      # relance sans argument : le dossier courant doit être celui de la session
grep -qxP "F\t$D/k1" $FAVF || { echo "ECHEC session non reprise"; cat $FAVF; exit 1; }
echo "OK chemin, tri et session"
