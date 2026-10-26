(() => {
  "use strict";
  const EN = {
    shot: "Two panes, tabs on each side, folder sizes, the transfer queue and the function-key bar at the bottom.",
    shot_alt: "S3bopus: two panes with tabs, folder sizes, transfer queue and the function-key bar",
    nav_about: "About", back: "← Back to S3btify", nav_download: "Download", tagline: "Two panes, a queue, and nothing else.",
    lead: "A light, portable two-pane file manager for Windows, Mac and Linux, in the spirit of Directory Opus on the Amiga.",
    cta: "Download for free", version: "Version",
    p1: "S3bopus was born out of frustration: on Linux as much as on the PC, the file manager is plain bad. No real queue: Windows finally has pause (whoooooau!), but everything is copied at the same time and it's a disaster.",
    p1b: "And that's not all: in 2026, Windows Explorer still cannot paste an image sitting in the clipboard. A screenshot copied, a Ctrl+V in a folder… and nothing. Microsoft can build a whole operating system, but can't turn a copied image into a file: an aberration. Same for copied text. In S3bopus, Ctrl+V really creates the file: an image becomes a .jpg, text (and anything that looks like it) becomes a .txt.",
    p2: "S3bopus brings back the essentials of Directory Opus on the Amiga: directory sizes and a real copy manager. Transfers go one by one through a queue, with progress, pause, resume, cancel and conflict handling.",
    p3: "And it is extremely portable: one small file, no installation, drop it anywhere on the PC. On Linux, a .deb or .rpm package; on Mac, a real native app.",
    h1: "Folder sizes", c1: "Computed in the background, updated live in the Size column, sortable.",
    h2: "Transfer queue", c2: "One job at a time, pause and resume, speed and time left, replace / skip / keep both.",
    h3: "Tabs and archives", c3: "Tabs in each pane, and archives (ZIP, 7z, RAR, tar.gz) open like folders.",
    h4: "Light or dark, FR or EN", c4: "Two buttons at the top right. Paste text or an image from the clipboard: it creates a .txt or a .jpg.",
    win_req: "Windows 10 / 11. Nothing to install: unzip and run. All archives (ZIP, 7z, RAR, tar.gz).",
    dl_win: "Download for Windows", win_note: "Windows may show a SmartScreen warning: the program is not signed.",
    light: "light", winl_req: "The same program in 156 KB: only ZIP archives open in the pane.", dl_winl: "Download the light version",
    deb_req: "Debian 12 / Ubuntu 22.04 or later, 64-bit. Native X11: 33 KB, it only uses libX11 and libXft.", dl_deb: "Download the .deb package",
    bin_req: "64-bit Linux, any distribution with an X11 server or XWayland. A single 4 MB executable, no installation, no dependencies.", dl_bin: "Download the executable",
    rpm_req: "Fedora / RHEL / openSUSE, 64-bit. Native X11, 39 KB.", dl_rpm: "Download the .rpm package",
    mac_req: "macOS 13 or later, Apple Silicon and Intel. Native app: open the disk image and drag S3bopus into Applications.",
    dl_mac: "Download for Mac", mac_note: "Unsigned program: on first launch, right-click S3bopus then Open. Archives are not supported yet.",
    c_visits: "Visitors", c_downloads: "Downloads",
    contact: "Contact:", contact_show: "Show the e-mail address",
    contact_help: "Type the 5 digits to show the address (protection against robots that harvest addresses).",
    contact_new: "Another code", contact_ok: "Show", contact_code: "Code",
    contact_bad: "Wrong code, try again.", contact_off: "Not available right now, please try again later.",
    footer_privacy: "This site sets no cookie: it only counts visits and downloads, recording nothing about you (no IP address, no identifier).",
  };
  const FR = {};
  document.querySelectorAll("[data-i18n]").forEach((el) => { FR[el.dataset.i18n] = el.textContent; });
  document.querySelectorAll("[data-i18n-alt]").forEach((el) => { FR[el.dataset.i18nAlt] = el.alt; });
  FR.contact_bad = "Code incorrect, réessayez.";
  FR.contact_off = "Indisponible pour l'instant, réessayez plus tard.";
  // Langue : dans l'adresse (?lang=en), sans cookie ni stockage
  const asked = new URLSearchParams(location.search).get("lang");
  let lang = asked === "fr" || asked === "en" ? asked : (navigator.language || "fr").toLowerCase().startsWith("fr") ? "fr" : "en";
  const T = (key) => (lang === "en" ? EN : FR)[key] || FR[key];
  function applyLang() {
    document.documentElement.lang = lang;
    document.querySelectorAll("[data-i18n]").forEach((el) => { el.textContent = T(el.dataset.i18n); });
    document.querySelectorAll("[data-i18n-alt]").forEach((el) => { el.alt = T(el.dataset.i18nAlt); });
    document.getElementById("lang").textContent = lang === "en" ? "FR" : "EN";
  }
  document.getElementById("lang").onclick = () => {
    lang = lang === "en" ? "fr" : "en";
    history.replaceState(null, "", "?lang=" + lang);
    applyLang();
  };
  applyLang();

  // Compteurs (site/counter/counter.py derrière Caddy). Sans compteur : rien ne s'affiche.
  function odometer(id, n) {
    const digits = String(Math.max(0, n | 0)).padStart(6, "0");
    document.getElementById(id).innerHTML = [...digits].map((d) => `<span>${d}</span>`).join("");
  }
  function show(c) {
    if (!c || typeof c.visits !== "number") return;
    odometer("c-visits", c.visits);
    odometer("c-downloads", Object.values(c.downloads || {}).reduce((a, b) => a + b, 0));
    document.getElementById("counters").classList.remove("hidden");
  }
  const count = (path) => fetch(path, { method: "POST", keepalive: true }).then((r) => (r.ok ? r.json() : null));
  count("count/hit").then(show).catch(() => {});
  document.querySelectorAll("a[data-count]").forEach((a) => {
    a.addEventListener("click", () => { count(`count/dl/${a.dataset.count}`).then(show).catch(() => {}); });
  });

  // Contact : l'adresse n'est pas dans la page ; le serveur la donne après le captcha
  const form = document.getElementById("contact-form");
  const err = document.getElementById("contact-err");
  let captchaId = "";
  const loadCaptcha = async () => {
    err.textContent = "";
    document.getElementById("contact-answer").value = "";
    try {
      const r = await fetch("count/captcha", { cache: "no-store" });
      if (!r.ok) throw new Error(String(r.status));
      const c = await r.json();
      captchaId = c.id;
      document.getElementById("contact-img").src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(c.image);
    } catch (e) {
      err.textContent = T("contact_off");
    }
  };
  document.getElementById("contact-open").onclick = () => {
    form.classList.remove("hidden");
    loadCaptcha();
    document.getElementById("contact-answer").focus();
  };
  document.getElementById("contact-new").onclick = loadCaptcha;
  form.onsubmit = async (e) => {
    e.preventDefault();
    err.textContent = "";
    let r;
    try {
      r = await fetch("count/contact", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id: captchaId, answer: document.getElementById("contact-answer").value.trim() }) });
    } catch (ex) {
      err.textContent = T("contact_off");
      return;
    }
    if (!r.ok) {
      if (r.status === 403) { await loadCaptcha(); err.textContent = T("contact_bad"); } else err.textContent = T("contact_off");
      return;
    }
    const { email } = await r.json();
    const link = document.createElement("a");
    link.href = `mailto:${email}`;
    link.textContent = email;
    document.getElementById("contact-out").replaceChildren(link);
    form.classList.add("hidden");
  };
})();
