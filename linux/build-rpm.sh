#!/bin/bash
# Construit le .rpm (Fedora / RHEL / openSUSE) à partir du .deb déjà construit (même contenu).
#   bash linux/build-rpm.sh 0.0.2      (depuis la racine du projet, après build.cmd ; WSL ou Linux avec « rpm » et « dpkg »)
set -euo pipefail
VERSION="${1:?version}"
HERE="$(cd "$(dirname "$0")" && pwd)"
DEB="$HERE/s3bopus_${VERSION}_amd64.deb"
[ -f "$DEB" ] || { echo "manquant : $DEB"; exit 1; }
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/root" "$WORK/rpm"
dpkg-deb -x "$DEB" "$WORK/root"
cat > "$WORK/s3bopus.spec" <<SPEC
Name:           s3bopus
Version:        $VERSION
Release:        1
Summary:        Lightweight dual-pane file manager with a transfer queue
License:        MIT
BuildArch:      x86_64
AutoReqProv:    no
Requires:       glibc, libX11, libICE, libSM, fontconfig

%description
Dual-pane file manager inspired by Directory Opus: folder sizes, a copy queue (one job at a time,
pause, resume, conflicts), tabs, archives opened as folders, history of typed locations.

%install
cp -a $WORK/root/. %{buildroot}/

%files
/opt/s3bopus
/usr/bin/s3bopus
/usr/share/applications/s3bopus.desktop
/usr/share/icons/hicolor/128x128/apps/s3bopus.png
SPEC
# Pas de recompression coûteuse : l'exécutable est déjà compressé
rpmbuild -bb --define "_topdir $WORK/rpm" --define "_binary_payload w9.gzdio" --define "__strip /bin/true" \
    --define "debug_package %{nil}" --define "_build_id_links none" "$WORK/s3bopus.spec" > "$WORK/rpm.log" 2>&1 || { tail -30 "$WORK/rpm.log"; exit 1; }
cp "$WORK"/rpm/RPMS/x86_64/*.rpm "$HERE/"
ls -la "$HERE"/s3bopus-${VERSION}-1.x86_64.rpm
