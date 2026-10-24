#!/usr/bin/env python3
"""Compteur de visites et de téléchargements du site S3bopus, façon années 2000.

Python 3 seul (aucune dépendance). Écoute en local (127.0.0.1:8090) derrière Caddy :
  POST /count/hit        -> +1 visite,                      renvoie {"visits": n, "downloads": {...}}
  POST /count/dl/<nom>   -> +1 téléchargement (win | winlight | deb | rpm), renvoie la même chose
  GET  /count            -> lecture seule
  GET  /count/captcha    -> {"id": ..., "image": "<svg…>"} : 5 chiffres dessinés (traits, pas du texte)
  POST /count/contact    -> {"id": ..., "answer": "12345"} : adresse e-mail de contact si la réponse est bonne

Contact : l'adresse n'est jamais écrite dans les pages du site (les robots qui ramassent les adresses
ne la trouvent pas) ; elle n'est donnée qu'après le captcha. Captcha sans état : la réponse attendue
est signée (HMAC, clé tirée au lancement), valable 10 minutes ; une mauvaise réponse ralentit d'une
seconde (la requête seulement, sans file d'attente commune : aucune adresse IP n'est retenue).

Protection des données : on ne garde que des totaux. Aucune adresse IP, aucun cookie, aucun
identifiant, aucun journal des requêtes : rien qui permette de reconnaître un visiteur (RGPD).

Lancement :  python3 counter.py [--port 8090] [--data /var/lib/s3bopus-counter/counts.json]
                                [--contact sysop@s3btify.com]
"""

import argparse
import base64
import hashlib
import hmac
import json
import math
import os
import secrets
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

DOWNLOADS = ("win", "winlight", "deb", "rpm", "mac")
CONTACT = "sysop@s3btify.com"
CAPTCHA_SECONDS = 600
MAX_BODY = 1024

# Chiffres en traits (grille 0..1 x 0..2) : dessinés en tracés SVG, pas en texte lisible par un robot
SEGMENTS = {"a": (0, 0, 1, 0), "b": (1, 0, 1, 1), "c": (1, 1, 1, 2), "d": (0, 2, 1, 2),
            "e": (0, 1, 0, 2), "f": (0, 0, 0, 1), "g": (0, 1, 1, 1)}
DIGITS = {"0": "abcdef", "1": "bc", "2": "abged", "3": "abgcd", "4": "fgbc", "5": "afgcd", "6": "afgedc",
          "7": "abc", "8": "abcdefg", "9": "abcdfg"}


class Captcha:
    def __init__(self):
        self.key = secrets.token_bytes(32)  # change à chaque lancement : les anciens captchas expirent

    def _sign(self, answer: str, expires: int, nonce: str) -> str:
        return hmac.new(self.key, f"{answer}|{expires}|{nonce}".encode(), hashlib.sha256).hexdigest()

    def new(self) -> dict:
        answer = "".join(secrets.choice("0123456789") for _ in range(5))
        expires = int(time.time()) + CAPTCHA_SECONDS
        nonce = secrets.token_hex(8)
        token = f"{expires}.{nonce}.{self._sign(answer, expires, nonce)}"
        return {"id": token, "image": self.draw(answer)}

    def check(self, token: str, answer: str) -> bool:
        try:
            expires, nonce, sig = str(token).split(".")
            ok = (int(expires) >= time.time() and len(str(answer)) <= 10
                  and hmac.compare_digest(sig, self._sign(str(answer).strip(), int(expires), nonce)))
        except ValueError:
            ok = False
        if not ok:
            # Chaque mauvaise réponse attend 1 s de son côté : avec une file commune, un robot qui se
            # trompe en rafale ferait attendre tous les visiteurs (et garderait des fils ouverts sans fin)
            time.sleep(1)
        return ok

    @staticmethod
    def draw(answer: str) -> str:
        rnd = secrets.SystemRandom()
        paths = []
        for i, digit in enumerate(answer):
            x0, y0, w, h = 18 + i * 34 + rnd.uniform(-3, 3), 14 + rnd.uniform(-4, 4), 16, 18
            angle = math.radians(rnd.uniform(-14, 14))

            def pt(x, y):
                px, py = x * w + rnd.uniform(-1.6, 1.6), y * h + rnd.uniform(-1.6, 1.6)
                return (x0 + px * math.cos(angle) - py * math.sin(angle), y0 + px * math.sin(angle) + py * math.cos(angle))
            d = ""
            for seg in DIGITS[digit]:
                ax, ay, bx, by = SEGMENTS[seg]
                (x1, y1), (x2, y2) = pt(ax, ay), pt(bx, by)
                d += f"M{x1:.1f} {y1:.1f}L{x2:.1f} {y2:.1f}"
            paths.append(f'<path d="{d}" stroke="#e8e8f0" stroke-width="{rnd.uniform(3, 4.2):.1f}" stroke-linecap="round" fill="none"/>')
        noise = "".join(
            f'<path d="M{rnd.uniform(0, 190):.0f} {rnd.uniform(0, 60):.0f}Q{rnd.uniform(0, 190):.0f} {rnd.uniform(0, 60):.0f} '
            f'{rnd.uniform(0, 190):.0f} {rnd.uniform(0, 60):.0f}" stroke="#8b5cf6" stroke-width="1.6" fill="none" opacity=".75"/>'
            for _ in range(6))
        return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 190 60" width="190" height="60">'
                f'<rect width="190" height="60" rx="6" fill="#1b1b24"/>{noise}{"".join(paths)}</svg>')


class Counts:
    def __init__(self, path: str):
        self.path = path
        self.lock = threading.Lock()
        try:
            with open(path, encoding="utf-8") as fh:
                data = json.load(fh)
        except (OSError, ValueError):
            data = {}
        self.visits = int(data.get("visits", 0))
        self.downloads = {k: int(data.get("downloads", {}).get(k, 0)) for k in DOWNLOADS}

    def snapshot(self) -> dict:
        return {"visits": self.visits, "downloads": dict(self.downloads)}

    def add(self, what: str) -> dict:
        with self.lock:
            if what == "hit":
                self.visits += 1
            else:
                self.downloads[what] += 1
            self._save()
            return self.snapshot()

    def _save(self):
        folder = os.path.dirname(os.path.abspath(self.path))
        os.makedirs(folder, exist_ok=True)
        fd, tmp = tempfile.mkstemp(dir=folder, suffix=".tmp")
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            json.dump(self.snapshot(), fh)
        os.replace(tmp, self.path)  # écriture atomique : pas de fichier à moitié écrit


def make_handler(counts: Counts, captcha: Captcha | None = None, contact: str = CONTACT):
    captcha = captcha or Captcha()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass  # aucun journal : ni IP, ni horodatage

        def _reply(self, code: int, data: dict | None = None):
            body = json.dumps(data or {}).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if self.path.rstrip("/") == "/count":
                return self._reply(200, counts.snapshot())
            if self.path.split("?")[0] == "/count/captcha":
                return self._reply(200, captcha.new())
            self._reply(404)

        def do_POST(self):
            parts = [p for p in self.path.split("?")[0].split("/") if p]
            if parts == ["count", "hit"]:
                return self._reply(200, counts.add("hit"))
            if len(parts) == 3 and parts[:2] == ["count", "dl"] and parts[2] in DOWNLOADS:
                return self._reply(200, counts.add(parts[2]))
            if parts == ["count", "contact"]:
                try:
                    size = int(self.headers.get("Content-Length") or 0)
                    data = json.loads(self.rfile.read(size)) if 0 < size <= MAX_BODY else {}
                except ValueError:
                    data = {}
                if isinstance(data, dict) and captcha.check(data.get("id", ""), data.get("answer", "")):
                    return self._reply(200, {"email": contact})
                return self._reply(403, {"error": "captcha"})
            self._reply(404)

    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8090)
    parser.add_argument("--data", default="/var/lib/s3bopus-counter/counts.json")
    parser.add_argument("--contact", default=CONTACT)
    args = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(Counts(args.data), contact=args.contact))
    server.daemon_threads = True
    print(f"compteur S3bopus sur 127.0.0.1:{args.port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
