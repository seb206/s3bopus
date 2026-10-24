// S3bopus pour Linux en X11 natif (Xlib + Xft), sans .NET ni toolkit : deux panneaux, onglets,
// taille des dossiers en arrière-plan, file de transferts (copie / déplacement / suppression).
// Compiler : make   (libx11-dev libxft-dev)   Essai sans écran : make test
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/keysym.h>
#include <X11/Xft/Xft.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <locale.h>
#include <pthread.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/select.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#define VERSION "0.0.2"
#define MAXTABS 8
#define MAXJOBS 512

static int EN;
#define T(fr, en) (EN ? (en) : (fr))

static double now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec + t.tv_nsec / 1e9; }

static void fmt_size(char *b, size_t n, long long v) {
    if (v < 0) { b[0] = 0; return; }
    if (v < 1024) { snprintf(b, n, "%lld %s", v, T("o", "B")); return; }
    const char *u[] = {"Ko", "Mo", "Go", "To"}, *ue[] = {"KB", "MB", "GB", "TB"};
    double d = v / 1024.0; int i = 0;
    while (d >= 1024 && i < 3) { d /= 1024; i++; }
    snprintf(b, n, d < 10 ? "%.2f %s" : d < 100 ? "%.1f %s" : "%.0f %s", d, EN ? ue[i] : u[i]);
}

static void path_join(char *out, const char *dir, const char *name) {
    if (strcmp(dir, "/") == 0) snprintf(out, PATH_MAX, "/%s", name); else snprintf(out, PATH_MAX, "%s/%s", dir, name);
}
static const char *base_of(const char *p) { const char *s = strrchr(p, '/'); return s && s[1] ? s + 1 : p; }
static void dir_of(char *out, const char *p) {
    snprintf(out, PATH_MAX, "%s", p);
    char *s = strrchr(out, '/');
    if (s == out) out[1] = 0; else if (s) *s = 0;
}

// ---------------------------------------------------------------- panneaux
typedef struct { char name[256]; int dir; long long size; time_t mtime; int sel; } Ent;
typedef struct { char path[PATH_MAX]; int cur, top; } Tab;
typedef struct {
    Tab tabs[MAXTABS]; int ntabs, atab;
    Ent *e; int n;
    volatile int gen;  // change à chaque listage : les calculs de taille en cours s'arrêtent
} Pane;

static Pane P[2];
static int active, show_hidden;
static pthread_mutex_t pm = PTHREAD_MUTEX_INITIALIZER;
static volatile int dirty = 1, refresh_req;

static int cmp_ent(const void *a, const void *b) {
    const Ent *x = a, *y = b;
    if (!strcmp(x->name, "..")) return -1;
    if (!strcmp(y->name, "..")) return 1;
    if (x->dir != y->dir) return y->dir - x->dir;
    return strcasecmp(x->name, y->name);
}

static long long tree_size(const char *p, int pane, int gen) {
    struct stat st;
    if (lstat(p, &st)) return 0;
    if (!S_ISDIR(st.st_mode)) return st.st_size;
    long long s = 0; DIR *d = opendir(p); if (!d) return 0;
    struct dirent *e;
    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
        if (pane >= 0 && P[pane].gen != gen) break;
        char c[PATH_MAX]; path_join(c, p, e->d_name);
        s += tree_size(c, pane, gen);
    }
    closedir(d);
    return s;
}

typedef struct { int pane, gen, n; char dir[PATH_MAX]; char (*names)[256]; } SizeJob;
static void *size_thread(void *arg) {
    SizeJob *j = arg;
    for (int i = 0; i < j->n && P[j->pane].gen == j->gen; i++) {
        char c[PATH_MAX]; path_join(c, j->dir, j->names[i]);
        long long s = tree_size(c, j->pane, j->gen);
        pthread_mutex_lock(&pm);
        if (P[j->pane].gen == j->gen)
            for (int k = 0; k < P[j->pane].n; k++)
                if (P[j->pane].e[k].dir && !strcmp(P[j->pane].e[k].name, j->names[i])) { P[j->pane].e[k].size = s; break; }
        pthread_mutex_unlock(&pm);
        dirty = 1;
    }
    free(j->names); free(j);
    return NULL;
}

// Relit le dossier de l'onglet actif ; keep = nom sous le curseur à retrouver (ou NULL)
static void load_pane(int p, const char *keep) {
    Pane *pp = &P[p]; Tab *t = &pp->tabs[pp->atab];
    DIR *d = opendir(t->path);
    if (!d && strcmp(t->path, "/")) { dir_of(t->path, t->path); d = opendir(t->path); }
    Ent *e = NULL; int n = 0, cap = 0;
    if (strcmp(t->path, "/")) { cap = 64; e = calloc(cap, sizeof *e); snprintf(e[n].name, 256, ".."); e[n].dir = 1; e[n].size = -2; n++; }
    struct dirent *de;
    while (d && (de = readdir(d))) {
        if (!strcmp(de->d_name, ".") || !strcmp(de->d_name, "..")) continue;
        if (!show_hidden && de->d_name[0] == '.') continue;
        if (n >= cap) { cap = cap ? cap * 2 : 64; e = realloc(e, cap * sizeof *e); }
        memset(&e[n], 0, sizeof *e);
        snprintf(e[n].name, 256, "%s", de->d_name);
        char c[PATH_MAX]; path_join(c, t->path, de->d_name);
        struct stat st;
        if (stat(c, &st) && lstat(c, &st)) { e[n].size = 0; n++; continue; }
        e[n].dir = S_ISDIR(st.st_mode); e[n].mtime = st.st_mtime;
        e[n].size = e[n].dir ? -1 : st.st_size;
        n++;
    }
    if (d) closedir(d);
    if (n) qsort(e, n, sizeof *e, cmp_ent);
    int cur = 0;
    if (keep) for (int i = 0; i < n; i++) if (!strcmp(e[i].name, keep)) { cur = i; break; }
    pthread_mutex_lock(&pm);
    free(pp->e); pp->e = e; pp->n = n; int gen = ++pp->gen;
    t->cur = keep ? cur : (t->cur < n ? t->cur : (n ? n - 1 : 0));
    pthread_mutex_unlock(&pm);
    SizeJob *j = calloc(1, sizeof *j); j->pane = p; j->gen = gen; snprintf(j->dir, PATH_MAX, "%s", t->path);
    j->names = calloc(n ? n : 1, 256);
    for (int i = 0; i < n; i++) if (e[i].dir && e[i].size == -1) snprintf(j->names[j->n++], 256, "%s", e[i].name);
    pthread_t th; pthread_create(&th, NULL, size_thread, j); pthread_detach(th);
    dirty = 1;
}

static void go(int p, const char *path, const char *keep) {
    char rp[PATH_MAX];
    if (!realpath(path, rp)) return;
    Tab *t = &P[p].tabs[P[p].atab];
    snprintf(t->path, PATH_MAX, "%s", rp); t->cur = t->top = 0;
    load_pane(p, keep);
}

// ---------------------------------------------------------------- file de transferts
enum { J_COPY, J_MOVE, J_DEL };
enum { Q_WAIT, Q_RUN, Q_DONE, Q_ERR, Q_CANCEL };
typedef struct { int kind, state; char src[PATH_MAX], dst[PATH_MAX]; long long total, done; double t0, t1; volatile int cancel; char err[96]; } Job;
static Job J[MAXJOBS]; static int nj;
static pthread_mutex_t qm = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t qc = PTHREAD_COND_INITIALIZER;
static volatile int paused;

static void enqueue(int kind, const char *src, const char *dstdir) {
    pthread_mutex_lock(&qm);
    if (nj == MAXJOBS) {  // plein : on retire ce qui est terminé
        int k = 0;
        for (int i = 0; i < nj; i++) if (J[i].state == Q_WAIT || J[i].state == Q_RUN) J[k++] = J[i];
        nj = k;
        if (nj == MAXJOBS) { pthread_mutex_unlock(&qm); return; }
    }
    Job *j = &J[nj++]; memset(j, 0, sizeof *j);
    j->kind = kind; snprintf(j->src, PATH_MAX, "%s", src); if (dstdir) snprintf(j->dst, PATH_MAX, "%s", dstdir);
    pthread_cond_signal(&qc); pthread_mutex_unlock(&qm); dirty = 1;
}

static int copy_file(Job *j, const char *s, const char *d, mode_t mode) {
    int in = open(s, O_RDONLY); if (in < 0) return -1;
    int out = open(d, O_WRONLY | O_CREAT | O_EXCL, mode & 0777);
    if (out < 0) { close(in); return -1; }
    char *buf = malloc(1 << 20); int rc = 0; ssize_t n;
    while ((n = read(in, buf, 1 << 20)) > 0) {
        while (paused && !j->cancel) usleep(50000);
        if (j->cancel) { rc = -2; break; }
        for (ssize_t o = 0; o < n;) { ssize_t w = write(out, buf + o, n - o); if (w < 0) { rc = -1; break; } o += w; }
        if (rc) break;
        j->done += n;
    }
    if (n < 0) rc = -1;
    free(buf); close(in); close(out);
    if (rc) unlink(d);
    return rc;
}

static int copy_tree(Job *j, const char *s, const char *d) {
    struct stat st;
    if (lstat(s, &st)) return -1;
    if (S_ISLNK(st.st_mode)) { char l[PATH_MAX]; ssize_t n = readlink(s, l, sizeof l - 1); if (n < 0) return -1; l[n] = 0; return symlink(l, d); }
    if (S_ISDIR(st.st_mode)) {
        if (mkdir(d, st.st_mode | 0700)) return -1;
        DIR *dir = opendir(s); if (!dir) return -1;
        struct dirent *e; int rc = 0;
        while (!rc && (e = readdir(dir))) {
            if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
            char a[PATH_MAX], b[PATH_MAX]; path_join(a, s, e->d_name); path_join(b, d, e->d_name);
            rc = copy_tree(j, a, b);
        }
        closedir(dir);
        return rc;
    }
    if (!S_ISREG(st.st_mode)) return 0;
    return copy_file(j, s, d, st.st_mode);
}

static int rm_tree(Job *j, const char *p) {
    struct stat st;
    if (lstat(p, &st)) return -1;
    if (S_ISDIR(st.st_mode)) {
        DIR *dir = opendir(p); if (!dir) return -1;
        struct dirent *e; int rc = 0;
        while (!rc && (e = readdir(dir))) {
            if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
            if (j->cancel) { rc = -2; break; }
            char c[PATH_MAX]; path_join(c, p, e->d_name); rc = rm_tree(j, c);
        }
        closedir(dir);
        return rc ? rc : rmdir(p);
    }
    j->done++;
    return unlink(p);
}

// Le dossier cible contient déjà le nom : « fichier (2).ext » (garder les deux, sans dialogue)
static void unique_name(char *dst) {
    struct stat st;
    if (lstat(dst, &st)) return;
    char stem[PATH_MAX], ext[64] = "", t[PATH_MAX];
    snprintf(stem, sizeof stem, "%s", dst);
    char *dot = strrchr(stem, '.'), *sl = strrchr(stem, '/');
    if (dot && sl && dot > sl + 1) { snprintf(ext, sizeof ext, "%s", dot); *dot = 0; }
    for (int i = 2; i < 10000; i++) { snprintf(t, sizeof t, "%s (%d)%s", stem, i, ext); if (lstat(t, &st)) break; }
    snprintf(dst, PATH_MAX, "%s", t);
}

static void run_job(Job *j) {
    int rc = 0;
    if (j->kind == J_DEL) { rc = rm_tree(j, j->src); }
    else {
        char dd[PATH_MAX], dst[PATH_MAX]; snprintf(dd, PATH_MAX, "%s", j->dst);
        char sd[PATH_MAX]; dir_of(sd, j->src);
        if (j->kind == J_MOVE && !strcmp(sd, dd)) { j->state = Q_DONE; return; }
        path_join(dst, dd, base_of(j->src));
        unique_name(dst);
        j->total = tree_size(j->src, -1, 0);
        if (j->kind == J_MOVE && !rename(j->src, dst)) j->done = j->total;
        else if (j->kind == J_MOVE && errno != EXDEV) rc = -1;
        else {
            rc = copy_tree(j, j->src, dst);
            if (rc == -2) { Job tmp = {0}; tmp.cancel = 0; rm_tree(&tmp, dst); }  // copie partielle retirée
            if (!rc && j->kind == J_MOVE) { Job tmp = {0}; rc = rm_tree(&tmp, j->src); }
        }
    }
    if (rc == -2) j->state = Q_CANCEL;
    else if (rc) { j->state = Q_ERR; snprintf(j->err, sizeof j->err, "%s", strerror(errno)); }
    else j->state = Q_DONE;
}

static void *worker(void *arg) {
    (void)arg;
    for (;;) {
        pthread_mutex_lock(&qm);
        Job *j = NULL;
        while (!j) {
            for (int i = 0; i < nj; i++) if (J[i].state == Q_WAIT) { j = &J[i]; break; }
            if (!j) pthread_cond_wait(&qc, &qm);
        }
        j->state = Q_RUN; j->t0 = now();
        pthread_mutex_unlock(&qm);
        while (paused && !j->cancel) usleep(50000);
        if (j->cancel) j->state = Q_CANCEL; else run_job(j);
        j->t1 = now();
        refresh_req = 1; dirty = 1;
    }
    return NULL;
}

// ---------------------------------------------------------------- affichage
static Display *dpy; static int scr; static Window win; static Pixmap pix; static GC gc;
static XftDraw *xd; static XftFont *font; static Visual *vis; static Colormap cmap;
static int W = 1100, H = 700, rh, asc;
static XIM xim; static XIC xic;

enum { C_BG, C_PANEL, C_TEXT, C_DIM, C_DIR, C_SELROW, C_CURROW, C_MARK, C_BAR, C_LINE, C_OK, C_ERR, NCOL };
static const unsigned RGB[NCOL] = {0x1e1e1e, 0x252526, 0xd4d4d4, 0x8a8a8a, 0x7fb4ff, 0x2a2d2e, 0x094771, 0xe5c07b, 0x3a96dd, 0x3c3c3c, 0x4ec9b0, 0xf14c4c};
static XftColor XC[NCOL]; static unsigned long PX[NCOL];

static void rect(int x, int y, int w, int h, int c) { if (w > 0 && h > 0) { XSetForeground(dpy, gc, PX[c]); XFillRectangle(dpy, pix, gc, x, y, w, h); } }
static int text_w(const char *s) { XGlyphInfo g; XftTextExtentsUtf8(dpy, font, (const FcChar8 *)s, strlen(s), &g); return g.xOff; }
static void text(int x, int y, int c, const char *s) { XftDrawStringUtf8(xd, &XC[c], font, x, y + asc + 3, (const FcChar8 *)s, strlen(s)); }
static void text_r(int xr, int y, int c, const char *s) { text(xr - text_w(s), y, c, s); }
// Coupe le texte pour qu'il tienne dans w pixels
static void text_fit(int x, int y, int w, int c, const char *s) {
    char b[PATH_MAX + 4]; snprintf(b, sizeof b, "%s", s);
    int len = strlen(b);
    if (text_w(b) > w) {
        while (len > 0 && text_w(b) + text_w("…") > w) { len--; while (len > 0 && (b[len] & 0xC0) == 0x80) len--; b[len] = 0; }
        strcat(b, "…");
    }
    text(x, y, c, b);
}

#define QROWS 5
static void geom(int p, int *x, int *w, int *ly, int *lh) {
    *x = p ? W / 2 : 0; *w = p ? W - W / 2 : W / 2;
    *ly = rh * 3;
    *lh = H - rh * 3 - (rh + 2) - rh - rh * QROWS;
}
static int vis_rows(int p) { int x, w, y, h; geom(p, &x, &w, &y, &h); return h / rh; }

static char msg[256]; static double msg_until;
static void say(const char *fmt, ...) __attribute__((format(printf, 1, 2)));
#include <stdarg.h>
static void say(const char *fmt, ...) { va_list a; va_start(a, fmt); vsnprintf(msg, sizeof msg, fmt, a); va_end(a); msg_until = now() + 6; dirty = 1; }

static struct { int kind; char label[128]; char buf[512]; int len; } pr;  // saisie : 1 dossier, 2 renommer, 3 confirmer suppression
static int tab_w(int p, int i) { char b[64]; const char *n = base_of(P[p].tabs[i].path); snprintf(b, sizeof b, "%s", n); int w = text_w(b) + 24; return w > 170 ? 170 : w; }

static void draw_pane(int p) {
    int x, w, ly, lh; geom(p, &x, &w, &ly, &lh);
    Pane *pp = &P[p]; Tab *t = &pp->tabs[pp->atab];
    rect(x, 0, w, ly + lh, C_PANEL);
    int tx = x + 2;
    for (int i = 0; i < pp->ntabs; i++) {
        int tw = tab_w(p, i);
        rect(tx, 2, tw - 2, rh - 2, i == pp->atab ? (p == active ? C_CURROW : C_SELROW) : C_BG);
        const char *nm = base_of(pp->tabs[i].path);
        text_fit(tx + 8, 2, tw - 14, i == pp->atab ? C_TEXT : C_DIM, strcmp(pp->tabs[i].path, "/") ? nm : "/");
        tx += tw;
    }
    rect(x, rh, w, rh, C_BG);
    text_fit(x + 8, rh, w - 16, p == active ? C_TEXT : C_DIM, t->path);
    rect(x, rh * 2, w, rh, C_LINE);
    text(x + 8, rh * 2, C_DIM, T("Nom", "Name"));
    text_r(x + w - 140, rh * 2, C_DIM, T("Taille", "Size"));
    text(x + w - 130, rh * 2, C_DIM, T("Modifié", "Modified"));
    int rows = lh / rh;
    for (int r = 0; r < rows; r++) {
        int i = t->top + r; if (i >= pp->n) break;
        Ent *e = &pp->e[i]; int y = ly + r * rh;
        if (i == t->cur) rect(x, y, w, rh, p == active ? C_CURROW : C_SELROW);
        int col = e->sel ? C_MARK : e->dir ? C_DIR : C_TEXT;
        char sz[40] = "", dt[40] = "";
        if (e->size == -1) snprintf(sz, sizeof sz, "…"); else if (e->size >= 0) fmt_size(sz, sizeof sz, e->size);
        if (e->mtime) { struct tm tm; localtime_r(&e->mtime, &tm); strftime(dt, sizeof dt, EN ? "%Y-%m-%d %H:%M" : "%d/%m/%Y %H:%M", &tm); }
        text_fit(x + 8, y, w - 160, col, e->dir && strcmp(e->name, "..") ? e->name : e->name);
        text_r(x + w - 140, y, e->sel ? C_MARK : C_DIM, sz);
        text(x + w - 130, y, C_DIM, dt);
    }
    rect(x + w - 1, 0, 1, ly + lh, C_LINE);
}

static void draw_queue(void) {
    int y0 = H - (rh + 2) - rh - rh * QROWS;
    rect(0, y0, W, rh * QROWS, C_BG); rect(0, y0, W, 1, C_LINE);
    char hd[128]; snprintf(hd, sizeof hd, "%s%s", T("File de transferts", "Transfer queue"), paused ? T("  (en pause, Ctrl+P)", "  (paused, Ctrl+P)") : "");
    text(8, y0, C_DIM, hd);
    pthread_mutex_lock(&qm);
    int first = nj > QROWS - 1 ? nj - (QROWS - 1) : 0;
    for (int i = first, r = 1; i < nj; i++, r++) {
        Job *j = &J[i]; int y = y0 + r * rh;
        char lab[PATH_MAX + 32]; snprintf(lab, sizeof lab, "%s %s", j->kind == J_COPY ? T("Copie", "Copy") : j->kind == J_MOVE ? T("Déplacement", "Move") : T("Suppression", "Delete"), base_of(j->src));
        text_fit(8, y, W / 2 - 20, C_TEXT, lab);
        int bx = W / 2, bw = W / 4;
        char st[96] = "";
        if (j->state == Q_RUN) {
            double pc = j->total > 0 ? (double)j->done / j->total : 0; if (pc > 1) pc = 1;
            rect(bx, y + 4, bw, rh - 8, C_LINE); rect(bx, y + 4, (int)(bw * pc), rh - 8, C_BAR);
            double el = now() - j->t0; char sp[40];
            fmt_size(sp, sizeof sp, el > 0.2 ? (long long)(j->done / el) : 0);
            if (j->total > 0) snprintf(st, sizeof st, "%d %%  %s/s", (int)(pc * 100), sp);
            else snprintf(st, sizeof st, "%lld", j->done);
        } else if (j->state == Q_WAIT) snprintf(st, sizeof st, "%s", T("en attente", "queued"));
        else if (j->state == Q_DONE) snprintf(st, sizeof st, "%s", T("terminé", "done"));
        else if (j->state == Q_CANCEL) snprintf(st, sizeof st, "%s", T("annulé", "cancelled"));
        else snprintf(st, sizeof st, "%s: %s", T("erreur", "error"), j->err);
        text(bx + bw + 10, y, j->state == Q_ERR ? C_ERR : j->state == Q_DONE ? C_OK : C_DIM, st);
    }
    if (!nj) text(8, y0 + rh, C_DIM, T("Aucun transfert. F5 copie, F6 déplace vers l'autre panneau.", "No transfer. F5 copies, F6 moves to the other pane."));
    pthread_mutex_unlock(&qm);
}

static void draw(void) {
    rect(0, 0, W, H, C_BG);
    pthread_mutex_lock(&pm);
    draw_pane(0); draw_pane(1);
    Pane *pp = &P[active]; int sel = 0; long long ss = 0;
    for (int i = 0; i < pp->n; i++) if (pp->e[i].sel) { sel++; if (pp->e[i].size > 0) ss += pp->e[i].size; }
    int total = pp->n; char st[256], sz[40];
    pthread_mutex_unlock(&pm);
    draw_queue();
    int sy = H - (rh + 2) - rh;
    rect(0, sy, W, rh, C_LINE);
    if (pr.kind) {
        char l[700]; snprintf(l, sizeof l, "%s %s_", pr.label, pr.buf);
        text(8, sy, C_TEXT, l);
    } else if (msg_until > now()) text(8, sy, C_MARK, msg);
    else {
        fmt_size(sz, sizeof sz, ss);
        snprintf(st, sizeof st, "%d %s, %d %s %s", total, T("éléments", "items"), sel, T("sélectionnés", "selected"), sel ? sz : "");
        text(8, sy, C_DIM, st);
    }
    const char *keys[] = {"F2 ", "F5 ", "F6 ", "F7 ", "F8 ", "F10 "};
    const char *lab[] = {T("Renommer", "Rename"), T("Copier", "Copy"), T("Déplacer", "Move"), T("Dossier", "New folder"), T("Supprimer", "Delete"), T("Quitter", "Quit")};
    int fy = H - rh - 2, fw = W / 6;
    rect(0, fy, W, rh + 2, C_PANEL);
    for (int i = 0; i < 6; i++) {
        text(i * fw + 8, fy + 1, C_MARK, keys[i]);
        text(i * fw + 8 + text_w(keys[i]), fy + 1, C_TEXT, lab[i]);
    }
    XCopyArea(dpy, pix, win, gc, 0, 0, W, H, 0, 0);
    dirty = 0;
}

static void make_back(void) {
    if (pix) { XftDrawDestroy(xd); XFreePixmap(dpy, pix); }
    pix = XCreatePixmap(dpy, win, W, H, DefaultDepth(dpy, scr));
    xd = XftDrawCreate(dpy, pix, vis, cmap);
}

// ---------------------------------------------------------------- actions
static void fix_scroll(int p) {
    Tab *t = &P[p].tabs[P[p].atab]; int rows = vis_rows(p);
    if (t->cur < t->top) t->top = t->cur;
    if (t->cur >= t->top + rows) t->top = t->cur - rows + 1;
    if (t->top < 0) t->top = 0;
}
static void move_cur(int p, int d) {
    Pane *pp = &P[p]; Tab *t = &pp->tabs[pp->atab];
    pthread_mutex_lock(&pm);
    t->cur += d; if (t->cur >= pp->n) t->cur = pp->n - 1; if (t->cur < 0) t->cur = 0;
    pthread_mutex_unlock(&pm);
    fix_scroll(p); dirty = 1;
}
static void spawn_open(const char *path) {
    if (fork() == 0) { setsid(); int nul = open("/dev/null", O_RDWR); dup2(nul, 0); dup2(nul, 1); dup2(nul, 2); execlp("xdg-open", "xdg-open", path, (char *)NULL); _exit(1); }
}
static void enter(int p) {
    Pane *pp = &P[p]; Tab *t = &pp->tabs[pp->atab];
    if (t->cur >= pp->n) return;
    char name[256], c[PATH_MAX]; int dir;
    pthread_mutex_lock(&pm); snprintf(name, 256, "%s", pp->e[t->cur].name); dir = pp->e[t->cur].dir; pthread_mutex_unlock(&pm);
    if (!strcmp(name, "..")) { char prev[256]; snprintf(prev, 256, "%s", base_of(t->path)); dir_of(c, t->path); go(p, c, prev); return; }
    path_join(c, t->path, name);
    if (dir) go(p, c, NULL); else spawn_open(c);
}
static void up(int p) { Tab *t = &P[p].tabs[P[p].atab]; if (strcmp(t->path, "/")) { char c[PATH_MAX], prev[256]; snprintf(prev, 256, "%s", base_of(t->path)); dir_of(c, t->path); go(p, c, prev); } }

// Éléments visés : les cochés, sinon celui sous le curseur (jamais « .. »). Renvoie le nombre de chemins écrits dans out.
static int targets(int p, char out[][PATH_MAX], int max) {
    Pane *pp = &P[p]; Tab *t = &pp->tabs[pp->atab]; int n = 0;
    pthread_mutex_lock(&pm);
    for (int i = 0; i < pp->n && n < max; i++) if (pp->e[i].sel && strcmp(pp->e[i].name, "..")) path_join(out[n++], t->path, pp->e[i].name);
    if (!n && t->cur < pp->n && strcmp(pp->e[t->cur].name, "..")) path_join(out[n++], t->path, pp->e[t->cur].name);
    pthread_mutex_unlock(&pm);
    return n;
}
static char tg[256][PATH_MAX];
static void transfer(int kind) {
    int n = targets(active, tg, 256);
    if (!n) return;
    for (int i = 0; i < n; i++) enqueue(kind, tg[i], P[!active].tabs[P[!active].atab].path);
    say(T("%d élément(s) ajouté(s) à la file", "%d item(s) queued"), n);
}
static void start_prompt(int kind, const char *label, const char *init) {
    pr.kind = kind; snprintf(pr.label, sizeof pr.label, "%s", label);
    snprintf(pr.buf, sizeof pr.buf, "%s", init); pr.len = strlen(pr.buf); dirty = 1;
}
static void prompt_done(int ok) {
    int k = pr.kind; pr.kind = 0; dirty = 1;
    if (!ok) return;
    Tab *t = &P[active].tabs[P[active].atab]; char c[PATH_MAX];
    if (k == 1 && pr.len) {
        path_join(c, t->path, pr.buf);
        if (mkdir(c, 0777)) say("%s: %s", T("Erreur", "Error"), strerror(errno)); else load_pane(active, pr.buf);
    } else if (k == 2 && pr.len) {
        int n = targets(active, tg, 1);
        if (n) { path_join(c, t->path, pr.buf); if (rename(tg[0], c)) say("%s: %s", T("Erreur", "Error"), strerror(errno)); else load_pane(active, pr.buf); }
    } else if (k == 3 && pr.len && strchr("oOyY", pr.buf[0])) {
        int n = targets(active, tg, 256);
        for (int i = 0; i < n; i++) enqueue(J_DEL, tg[i], NULL);
    }
}
static void tab_new(int p) {
    Pane *pp = &P[p]; if (pp->ntabs >= MAXTABS) return;
    pp->tabs[pp->ntabs] = pp->tabs[pp->atab]; pp->atab = pp->ntabs++; load_pane(p, NULL);
}
static void tab_close(int p) {
    Pane *pp = &P[p]; if (pp->ntabs < 2) return;
    for (int i = pp->atab; i < pp->ntabs - 1; i++) pp->tabs[i] = pp->tabs[i + 1];
    pp->ntabs--; if (pp->atab >= pp->ntabs) pp->atab = pp->ntabs - 1; load_pane(p, NULL);
}
static void tab_switch(int p, int i) { P[p].atab = (i + P[p].ntabs) % P[p].ntabs; load_pane(p, NULL); }
static void toggle_sel(int p, int advance) {
    Pane *pp = &P[p]; Tab *t = &pp->tabs[pp->atab];
    pthread_mutex_lock(&pm);
    if (t->cur < pp->n && strcmp(pp->e[t->cur].name, "..")) pp->e[t->cur].sel ^= 1;
    pthread_mutex_unlock(&pm);
    if (advance) move_cur(p, 1);
    dirty = 1;
}

static void on_key(XKeyEvent *ke) {
    char buf[64]; KeySym ks; int n;
    if (xic) { Status s; n = Xutf8LookupString(xic, ke, buf, sizeof buf - 1, &ks, &s); if (s == XLookupChars) ks = NoSymbol; }
    else n = XLookupString(ke, buf, sizeof buf - 1, &ks, NULL);
    if (n < 0) n = 0;
    buf[n] = 0;
    if (ks == NoSymbol) ks = XLookupKeysym(ke, 0);
    int ctrl = ke->state & ControlMask;
    if (pr.kind) {
        if (ks == XK_Escape) prompt_done(0);
        else if (ks == XK_Return || ks == XK_KP_Enter) prompt_done(1);
        else if (ks == XK_BackSpace) { while (pr.len > 0 && (pr.buf[pr.len - 1] & 0xC0) == 0x80) pr.len--; if (pr.len) pr.len--; pr.buf[pr.len] = 0; dirty = 1; }
        else if (n > 0 && (unsigned char)buf[0] >= 32 && pr.len + n < (int)sizeof pr.buf - 1 && !ctrl) { memcpy(pr.buf + pr.len, buf, n); pr.len += n; pr.buf[pr.len] = 0; dirty = 1; }
        return;
    }
    int p = active, page = vis_rows(p) - 1;
    switch (ks) {
    case XK_Up: move_cur(p, -1); break;
    case XK_Down: move_cur(p, 1); break;
    case XK_Page_Up: if (ctrl) tab_switch(p, P[p].atab - 1); else move_cur(p, -page); break;
    case XK_Page_Down: if (ctrl) tab_switch(p, P[p].atab + 1); else move_cur(p, page); break;
    case XK_Home: move_cur(p, -1000000); break;
    case XK_End: move_cur(p, 1000000); break;
    case XK_Return: case XK_KP_Enter: enter(p); break;
    case XK_BackSpace: up(p); break;
    case XK_Tab: active = !active; dirty = 1; break;
    case XK_space: case XK_Insert: toggle_sel(p, 1); break;
    case XK_F2: { int c = targets(p, tg, 1); if (c) start_prompt(2, T("Nouveau nom :", "New name:"), base_of(tg[0])); break; }
    case XK_F5: transfer(J_COPY); break;
    case XK_F6: transfer(J_MOVE); break;
    case XK_F7: start_prompt(1, T("Nouveau dossier :", "New folder:"), ""); break;
    case XK_F8: case XK_Delete: { int c = targets(p, tg, 256); if (c) { char l[160]; snprintf(l, sizeof l, T("Supprimer %d élément(s) ? (o/n)", "Delete %d item(s)? (y/n)"), c); start_prompt(3, l, ""); } break; }
    case XK_F10: exit(0);
    case XK_Escape:
        pthread_mutex_lock(&qm);
        for (int i = 0; i < nj; i++) if (J[i].state == Q_RUN) J[i].cancel = 1;
        pthread_mutex_unlock(&qm);
        break;
    default:
        if (ctrl && (ks == XK_t || ks == XK_T)) tab_new(p);
        else if (ctrl && (ks == XK_w || ks == XK_W)) tab_close(p);
        else if (ctrl && (ks == XK_p || ks == XK_P)) { paused = !paused; dirty = 1; }
        else if (ctrl && (ks == XK_h || ks == XK_H)) { show_hidden = !show_hidden; load_pane(0, NULL); load_pane(1, NULL); }
        else if (ctrl && (ks == XK_a || ks == XK_A)) { pthread_mutex_lock(&pm); for (int i = 0; i < P[p].n; i++) P[p].e[i].sel = strcmp(P[p].e[i].name, ".."); pthread_mutex_unlock(&pm); dirty = 1; }
        else if (ctrl && (ks == XK_q || ks == XK_Q)) exit(0);
    }
}

static void on_click(XButtonEvent *be) {
    int p = be->x >= W / 2, x, w, ly, lh; geom(p, &x, &w, &ly, &lh);
    static double last; static int lastrow = -1, lastp = -1;
    if (be->button == Button4 || be->button == Button5) {
        Tab *t = &P[p].tabs[P[p].atab]; t->top += be->button == Button4 ? -3 : 3;
        int mx = P[p].n - vis_rows(p); if (t->top > mx) t->top = mx; if (t->top < 0) t->top = 0;
        dirty = 1; return;
    }
    if (be->button != Button1) return;
    active = p; dirty = 1;
    if (be->y < rh) {
        int tx = x + 2;
        for (int i = 0; i < P[p].ntabs; i++) { int tw = tab_w(p, i); if (be->x >= tx && be->x < tx + tw) { if (i != P[p].atab) tab_switch(p, i); return; } tx += tw; }
        return;
    }
    if (be->y < ly || be->y >= ly + lh) return;
    Tab *t = &P[p].tabs[P[p].atab]; int row = t->top + (be->y - ly) / rh;
    if (row >= P[p].n) return;
    t->cur = row;
    if (be->state & ControlMask) toggle_sel(p, 0);
    double tn = now();
    if (row == lastrow && p == lastp && tn - last < 0.4) { enter(p); lastrow = -1; } else { lastrow = row; lastp = p; last = tn; }
}

int main(int argc, char **argv) {
    setlocale(LC_ALL, ""); signal(SIGCHLD, SIG_IGN); signal(SIGPIPE, SIG_IGN);
    const char *lg = getenv("LANG"); EN = !(lg && !strncmp(lg, "fr", 2));
    if (argc > 1 && !strcmp(argv[1], "--version")) { printf("S3bopus %s (X11)\n", VERSION); return 0; }
    if (!(dpy = XOpenDisplay(NULL))) { fprintf(stderr, "S3bopus : pas d'affichage X11 (DISPLAY)\n"); return 1; }
    scr = DefaultScreen(dpy); vis = DefaultVisual(dpy, scr); cmap = DefaultColormap(dpy, scr);
    font = XftFontOpenName(dpy, scr, "DejaVu Sans Mono:size=10");
    if (!font) font = XftFontOpenName(dpy, scr, "monospace:size=10");
    if (!font) { fprintf(stderr, "S3bopus : aucune police\n"); return 1; }
    asc = font->ascent; rh = font->ascent + font->descent + 6;
    for (int i = 0; i < NCOL; i++) {
        XRenderColor rc = {((RGB[i] >> 16) & 255) * 257, ((RGB[i] >> 8) & 255) * 257, (RGB[i] & 255) * 257, 0xffff};
        XftColorAllocValue(dpy, vis, cmap, &rc, &XC[i]); PX[i] = XC[i].pixel;
    }
    win = XCreateSimpleWindow(dpy, RootWindow(dpy, scr), 0, 0, W, H, 0, 0, PX[C_BG]);
    XStoreName(dpy, win, "S3bopus " VERSION);
    XClassHint ch = {"s3bopus", "S3bopus"}; XSetClassHint(dpy, win, &ch);
    XSelectInput(dpy, win, ExposureMask | KeyPressMask | ButtonPressMask | StructureNotifyMask);
    Atom del = XInternAtom(dpy, "WM_DELETE_WINDOW", False); XSetWMProtocols(dpy, win, &del, 1);
    gc = XCreateGC(dpy, win, 0, NULL);
    XSetLocaleModifiers("");
    if ((xim = XOpenIM(dpy, NULL, NULL, NULL))) xic = XCreateIC(xim, XNInputStyle, XIMPreeditNothing | XIMStatusNothing, XNClientWindow, win, XNFocusWindow, win, NULL);
    make_back(); XMapWindow(dpy, win);

    const char *home = getenv("HOME"); if (!home) home = "/";
    for (int p = 0; p < 2; p++) {
        P[p].ntabs = 1;
        snprintf(P[p].tabs[0].path, PATH_MAX, "%s", p < argc - 1 ? argv[p + 1] : home);
        char rp[PATH_MAX]; if (!realpath(P[p].tabs[0].path, rp)) snprintf(rp, PATH_MAX, "/");
        snprintf(P[p].tabs[0].path, PATH_MAX, "%s", rp);
        load_pane(p, NULL);
    }
    pthread_t th; pthread_create(&th, NULL, worker, NULL); pthread_detach(th);

    int xfd = ConnectionNumber(dpy);
    for (;;) {
        while (XPending(dpy)) {
            XEvent ev; XNextEvent(dpy, &ev);
            if (XFilterEvent(&ev, None)) continue;
            switch (ev.type) {
            case Expose: dirty = 1; break;
            case ConfigureNotify: if (ev.xconfigure.width != W || ev.xconfigure.height != H) { W = ev.xconfigure.width; H = ev.xconfigure.height; make_back(); dirty = 1; } break;
            case KeyPress: on_key(&ev.xkey); break;
            case ButtonPress: on_click(&ev.xbutton); break;
            case ClientMessage: if ((Atom)ev.xclient.data.l[0] == del) return 0; break;
            }
        }
        if (refresh_req) {
            refresh_req = 0;
            for (int p = 0; p < 2; p++) { Pane *pp = &P[p]; char keep[256] = ""; Tab *t = &pp->tabs[pp->atab];
                pthread_mutex_lock(&pm); if (t->cur < pp->n) snprintf(keep, 256, "%s", pp->e[t->cur].name); pthread_mutex_unlock(&pm);
                load_pane(p, keep[0] ? keep : NULL); }
        }
        if (msg_until && msg_until < now()) { msg_until = 0; dirty = 1; }
        pthread_mutex_lock(&qm); for (int i = 0; i < nj; i++) if (J[i].state == Q_RUN) dirty = 1; pthread_mutex_unlock(&qm);
        if (dirty) { draw(); XFlush(dpy); }
        fd_set fds; FD_ZERO(&fds); FD_SET(xfd, &fds);
        struct timeval tv = {0, 100000};
        select(xfd + 1, &fds, NULL, NULL, &tv);
    }
}
