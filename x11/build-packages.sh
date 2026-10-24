#!/bin/bash
# Construit s3bopus_<v>-<rev>_amd64.deb et s3bopus-<v>-<rev>.x86_64.rpm du X11 (WSL ou Linux : gcc, libx11-dev, libxft-dev, zlib1g-dev, dpkg-deb, rpmbuild).
#   bash x11/build-packages.sh 0.0.2 2
set -euo pipefail
V="${1:?version}"; REV="${2:?révision}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
make -C "$HERE" clean s3bopus CFLAGS="-O2 -s -std=gnu11"
R="$WORK/root"
install -D -m755 "$HERE/s3bopus" "$R/usr/bin/s3bopus"
install -D -m644 "$HERE/../linux/s3bopus.png" "$R/usr/share/icons/hicolor/128x128/apps/s3bopus.png"
mkdir -p "$R/usr/share/applications"
cat > "$R/usr/share/applications/s3bopus.desktop" <<D
[Desktop Entry]
Type=Application
Name=S3bopus
Comment=Explorateur de fichiers à deux panneaux
Exec=s3bopus
Icon=s3bopus
Terminal=false
Categories=Utility;FileManager;
D
# .deb
mkdir -p "$R/DEBIAN"
cat > "$R/DEBIAN/control" <<C
Package: s3bopus
Version: $V-$REV
Architecture: amd64
Maintainer: S3bopus
Depends: libc6, libx11-6, libxft2, zlib1g
Recommends: xdg-utils, fonts-dejavu-core
Section: utils
Priority: optional
Description: Explorateur de fichiers léger à deux panneaux (X11 natif)
 Deux panneaux, onglets, file de transferts, taille des dossiers, archives zip et tar.
C
dpkg-deb --root-owner-group -Zgzip --build "$R" "$HERE/s3bopus_${V}-${REV}_amd64.deb" >/dev/null
rm -rf "$R/DEBIAN"
# .rpm
cat > "$WORK/s3bopus.spec" <<SPEC
Name:           s3bopus
Version:        $V
Release:        $REV
Summary:        Lightweight dual-pane file manager with a transfer queue (native X11)
License:        MIT
BuildArch:      x86_64
AutoReqProv:    no
Requires:       glibc, libX11, libXft, zlib

%description
Dual-pane file manager inspired by Directory Opus: folder sizes, a copy queue (one job at a time,
pause, resume), tabs, zip and tar archives opened as folders, clipboard paste.

%install
cp -a $R/. %{buildroot}/

%files
/usr/bin/s3bopus
/usr/share/applications/s3bopus.desktop
/usr/share/icons/hicolor/128x128/apps/s3bopus.png
SPEC
rpmbuild -bb --define "_topdir $WORK/rpm" --define "__strip /bin/true" --define "debug_package %{nil}" --define "_build_id_links none" "$WORK/s3bopus.spec" > "$WORK/rpm.log" 2>&1 || { tail -30 "$WORK/rpm.log"; exit 1; }
cp "$WORK"/rpm/RPMS/x86_64/*.rpm "$HERE/"
ls -l "$HERE"/s3bopus_${V}-${REV}_amd64.deb "$HERE"/s3bopus-${V}-${REV}.x86_64.rpm
