"""Construit s3bopus_<version>_amd64.deb à partir de publish/s3bopus, sans dpkg (marche aussi sous Windows).

Un .deb est une archive « ar » contenant debian-binary, control.tar.gz et data.tar.gz.
Usage : python build-deb.py <version>
"""
import io, os, sys, tarfile, time

here = os.path.dirname(os.path.abspath(__file__))
version = sys.argv[1]
now = int(time.time())

control = f"""Package: s3bopus
Version: {version}
Architecture: amd64
Maintainer: S3bopus
Depends: libc6, libx11-6, libice6, libsm6, libfontconfig1
Section: utils
Priority: optional
Description: Explorateur de fichiers léger à deux panneaux
 Deux panneaux, file de transferts avec pause et conflits, taille des dossiers.
"""

desktop = """[Desktop Entry]
Type=Application
Name=S3bopus
Comment=Explorateur de fichiers à deux panneaux
Exec=s3bopus %F
Icon=s3bopus
Terminal=false
Categories=Utility;FileManager;
MimeType=inode/directory;
"""


def add(tar, name, data=None, mode=0o644, kind=tarfile.REGTYPE, link=None):
    ti = tarfile.TarInfo(name)
    ti.mode, ti.mtime, ti.uid, ti.gid, ti.uname, ti.gname, ti.type = mode, now, 0, 0, "root", "root", kind
    if link: ti.linkname = link
    if data is not None: ti.size = len(data)
    tar.addfile(ti, io.BytesIO(data) if data is not None else None)


def targz(entries):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz", format=tarfile.GNU_FORMAT) as tar:
        for e in entries: add(tar, *e[:1], **e[1])
    return buf.getvalue()


def d(name): return (name, dict(mode=0o755, kind=tarfile.DIRTYPE))


exe = open(os.path.join(here, "publish", "s3bopus"), "rb").read()
icon = open(os.path.join(here, "s3bopus.png"), "rb").read()
data = targz([
    d("./opt"), d("./opt/s3bopus"),
    ("./opt/s3bopus/s3bopus", dict(data=exe, mode=0o755)),
    d("./usr"), d("./usr/bin"),
    ("./usr/bin/s3bopus", dict(kind=tarfile.SYMTYPE, mode=0o777, link="/opt/s3bopus/s3bopus")),
    d("./usr/share"), d("./usr/share/applications"),
    ("./usr/share/applications/s3bopus.desktop", dict(data=desktop.encode())),
    d("./usr/share/icons"), d("./usr/share/icons/hicolor"), d("./usr/share/icons/hicolor/128x128"), d("./usr/share/icons/hicolor/128x128/apps"),
    ("./usr/share/icons/hicolor/128x128/apps/s3bopus.png", dict(data=icon)),
])
ctrl = targz([("./control", dict(data=control.encode()))])


def ar_member(name, payload):
    header = f"{name + '/':<16}{now:<12}{0:<6}{0:<6}{'100644':<8}{len(payload):<10}`\n".encode()
    assert len(header) == 60
    return header + payload + (b"\n" if len(payload) % 2 else b"")


out = os.path.join(here, f"s3bopus_{version}_amd64.deb")
with open(out, "wb") as f:
    f.write(b"!<arch>\n")
    f.write(ar_member("debian-binary", b"2.0\n"))
    f.write(ar_member("control.tar.gz", ctrl))
    f.write(ar_member("data.tar.gz", data))
print(out)
