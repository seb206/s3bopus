// S3bopus pour Linux en X11 natif (Xlib + Xft), sans .NET ni toolkit : deux panneaux, onglets,
// taille des dossiers en arrière-plan, file de transferts (copie / déplacement / suppression).
// Compiler : make   (libx11-dev libxft-dev)   Essai sans écran : make test
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <X11/Xatom.h>
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
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/select.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>
#include <zlib.h>

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
// arch non vide : l'onglet est dans une archive (lecture seule), inner = dossier dans l'archive, path = arch/inner pour l'affichage
typedef struct { char path[PATH_MAX]; char arch[PATH_MAX], inner[PATH_MAX]; int cur, top; } Tab;
typedef struct {
    Tab tabs[MAXTABS]; int ntabs, atab;
    Ent *e; int n;
    volatile int gen;  // change à chaque listage : les calculs de taille en cours s'arrêtent
} Pane;

static void say(const char *fmt, ...) __attribute__((format(printf, 1, 2)));
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


enum { J_COPY, J_MOVE, J_DEL, J_EXTRACT };  // J_EXTRACT : src = « archive\1chemin dans l'archive »
enum { Q_WAIT, Q_RUN, Q_DONE, Q_ERR, Q_CANCEL };
// sync : exécuté dans le fil principal (ouvrir un fichier d'archive), donc insensible à la pause
typedef struct { int kind, state, sync; char src[PATH_MAX], dst[PATH_MAX]; long long total, done; double t0, t1; volatile int cancel; char err[96]; } Job;
static volatile int paused;
static int gate(Job *j) { while (paused && !j->cancel && !j->sync) usleep(50000); return j->cancel; }

// ---------------------------------------------------------------- archives : zip, tar, tar.gz / tgz (lecture seule)
#define ANAME 1024
typedef struct { char name[ANAME]; int dir, method; long long size, csize, off; time_t mtime; } AEnt;

static unsigned rd16(const unsigned char *p) { return p[0] | p[1] << 8; }
static unsigned rd32(const unsigned char *p) { return rd16(p) | (unsigned)rd16(p + 2) << 16; }
static int has_ext(const char *n, const char *e) { size_t a = strlen(n), b = strlen(e); return a > b && !strcasecmp(n + a - b, e); }
static int is_archive_name(const char *n) { return has_ext(n, ".zip") || has_ext(n, ".tar") || has_ext(n, ".tar.gz") || has_ext(n, ".tgz"); }

// Retire les « ./ » et « / » du début et les « / » de la fin
static void a_clean(char *s) {
    char *q = s;
    while (q[0] == '.' && q[1] == '/') q += 2;
    while (*q == '/') q++;
    memmove(s, q, strlen(q) + 1);
    size_t n = strlen(s);
    while (n && s[n - 1] == '/') s[--n] = 0;
}
// Un composant « .. » dans un nom d'archive : on ne l'extrait jamais (zip-slip)
static int unsafe_name(const char *s) {
    for (const char *p = s; ;) {
        const char *q = strchr(p, '/'); size_t l = q ? (size_t)(q - p) : strlen(p);
        if (l == 2 && p[0] == '.' && p[1] == '.') return 1;
        if (!q) return 0;
        p = q + 1;
    }
}
static int arch_is_zip(const char *file) { unsigned char m[2] = {0, 0}; FILE *f = fopen(file, "rb"); if (!f) return 0; size_t n = fread(m, 1, 2, f); fclose(f); return n == 2 && m[0] == 'P' && m[1] == 'K'; }

// shortcut: pas de zip64 ni d'entrées chiffrées ; à ajouter si une archive > 4 Go se présente
static AEnt *zip_list(const char *file, int *n) {
    FILE *f = fopen(file, "rb"); if (!f) return NULL;
    fseek(f, 0, SEEK_END); long sz = ftell(f), len = sz < 65557 ? sz : 65557;
    unsigned char *b = malloc(len ? len : 1);
    fseek(f, sz - len, SEEK_SET);
    long e = -1;
    if (fread(b, 1, len, f) == (size_t)len) for (long i = len - 22; i >= 0; i--) if (rd32(b + i) == 0x06054b50) { e = i; break; }
    if (e < 0) { free(b); fclose(f); return NULL; }
    unsigned cnt = rd16(b + e + 10), csz = rd32(b + e + 12), coff = rd32(b + e + 16);
    free(b);
    unsigned char *cd = NULL; AEnt *a = NULL;
    if (cnt == 0xFFFF || csz == 0xFFFFFFFFu || coff == 0xFFFFFFFFu || !(cd = malloc(csz + 1))) { fclose(f); return NULL; }
    fseek(f, coff, SEEK_SET);
    if (fread(cd, 1, csz, f) != csz) { free(cd); fclose(f); return NULL; }
    fclose(f);
    a = calloc(cnt ? cnt : 1, sizeof *a);
    int k = 0; unsigned o = 0;
    for (unsigned i = 0; i < cnt && o + 46 <= csz && rd32(cd + o) == 0x02014b50; i++) {
        unsigned nl = rd16(cd + o + 28), el = rd16(cd + o + 30), cl = rd16(cd + o + 32);
        if (o + 46 + nl > csz) break;
        AEnt *x = &a[k]; unsigned nn = nl < ANAME - 1 ? nl : ANAME - 1;
        memcpy(x->name, cd + o + 46, nn); x->name[nn] = 0;
        x->dir = nn && x->name[nn - 1] == '/'; a_clean(x->name);
        x->method = rd16(cd + o + 10); x->csize = rd32(cd + o + 20); x->size = rd32(cd + o + 24); x->off = rd32(cd + o + 42);
        unsigned dt = rd16(cd + o + 12), dd = rd16(cd + o + 14);
        struct tm tm = {.tm_year = (dd >> 9) + 80, .tm_mon = ((dd >> 5) & 15) - 1, .tm_mday = dd & 31, .tm_hour = dt >> 11, .tm_min = (dt >> 5) & 63, .tm_sec = (dt & 31) * 2, .tm_isdst = -1};
        x->mtime = mktime(&tm);
        if (x->name[0]) k++;
        o += 46 + nl + el + cl;
    }
    free(cd); *n = k;
    return a;
}

static long long octal(const char *p, int n) {
    long long v = 0; int i = 0;
    if ((unsigned char)p[0] & 0x80) { for (i = 1; i < n; i++) v = v << 8 | (unsigned char)p[i]; return v; }  // base 256 (GNU)
    while (i < n && p[i] == ' ') i++;
    for (; i < n && p[i] >= '0' && p[i] <= '7'; i++) v = v * 8 + (p[i] - '0');
    return v;
}
// Lit l'en-tête suivant (en absorbant les noms longs GNU « L » et pax « x »). 1 : entrée lue, *left octets de données à passer ; 0 : fin.
// a->method = 1 si l'entrée est un fichier ou un dossier (liens et fichiers spéciaux : 0, sans contenu).
static int tar_header(gzFile f, AEnt *a, long long *left) {
    char longname[ANAME] = ""; unsigned char h[512];
    for (;;) {
        if (gzread(f, h, 512) != 512 || !h[0]) return 0;
        long long size = octal((char *)h + 124, 12); char type = h[156];
        if (type == 'L' || type == 'x' || type == 'g') {
            long long pad = (size + 511) / 512 * 512;
            if (size > (1 << 20)) return 0;
            char *d = malloc(pad + 1);
            if (gzread(f, d, pad) != pad) { free(d); return 0; }
            d[size] = 0;
            if (type == 'L') snprintf(longname, sizeof longname, "%s", d);
            else if (type == 'x')
                for (char *q = d; q < d + size;) {  // lignes « longueur clé=valeur\n »
                    char *sp = strchr(q, ' '); long l = 0;
                    if (!sp) break;
                    for (const char *dg = q; dg < sp && *dg >= '0' && *dg <= '9'; dg++) l = l * 10 + (*dg - '0');  // pas atol : il tire glibc 2.38
                    if (l <= 0) break;
                    if (!strncmp(sp + 1, "path=", 5)) { long m = l - (sp - q) - 7; if (m < 0) m = 0; if (m > ANAME - 1) m = ANAME - 1; memcpy(longname, sp + 6, m); longname[m] = 0; }
                    q += l;
                }
            free(d); continue;
        }
        memset(a, 0, sizeof *a);
        if (longname[0]) snprintf(a->name, ANAME, "%s", longname);
        else {
            char nm[101], pf[156]; memcpy(nm, h, 100); nm[100] = 0; memcpy(pf, h + 345, 155); pf[155] = 0;
            if (!memcmp(h + 257, "ustar", 5) && pf[0]) snprintf(a->name, ANAME, "%s/%s", pf, nm); else snprintf(a->name, ANAME, "%s", nm);
        }
        int reg = type == '0' || type == 0 || type == '7';
        a->dir = type == '5'; a->method = reg || a->dir; a->size = reg ? size : 0;
        a->mtime = octal((char *)h + 136, 12);
        *left = size; a_clean(a->name);
        return 1;
    }
}

static AEnt *tar_list(const char *file, int *n) {
    gzFile f = gzopen(file, "rb"); if (!f) return NULL;
    int cap = 256, k = 0; AEnt *a = malloc(cap * sizeof *a), x; long long left;
    while (tar_header(f, &x, &left)) {
        if (x.method && x.name[0]) { if (k == cap) { cap *= 2; a = realloc(a, cap * sizeof *a); } a[k++] = x; }
        if (gzseek(f, (left + 511) / 512 * 512, SEEK_CUR) < 0) break;
    }
    gzclose(f); *n = k;
    if (!k) { free(a); return NULL; }
    return a;
}
static AEnt *arch_list(const char *file, int *n) { return arch_is_zip(file) ? zip_list(file, n) : tar_list(file, n); }

// Enfants directs du dossier inner de l'archive, dans e[1..] (e[0] = « .. ») ; -1 si l'archive est illisible
// shortcut: recherche linéaire du nom parmi les enfants, lent au-delà de ~50 000 éléments dans un même dossier
static int arch_entries(const char *arch, const char *inner, int hidden, Ent **out) {
    int n; AEnt *a = arch_list(arch, &n); if (!a) return -1;
    size_t il = strlen(inner); int cnt = 1, cap = 64; Ent *e = calloc(cap, sizeof *e);
    snprintf(e[0].name, 256, ".."); e[0].dir = 1; e[0].size = -2;
    for (int i = 0; i < n; i++) {
        const char *rest = a[i].name;
        if (il) { if (strncmp(rest, inner, il) || rest[il] != '/') continue; rest += il + 1; }
        if (!*rest) continue;
        const char *sl = strchr(rest, '/'); size_t cl = sl ? (size_t)(sl - rest) : strlen(rest); if (cl > 255) cl = 255;
        char nm[256]; memcpy(nm, rest, cl); nm[cl] = 0;
        if (!hidden && nm[0] == '.') continue;
        int j = 1; while (j < cnt && strcmp(e[j].name, nm)) j++;
        if (j == cnt) {
            if (cnt == cap) { cap *= 2; e = realloc(e, cap * sizeof *e); }
            memset(&e[cnt], 0, sizeof *e); snprintf(e[cnt].name, 256, "%s", nm); cnt++;
        }
        if (sl || a[i].dir) e[j].dir = 1;
        if (!a[i].dir) { if (sl) e[j].size += a[i].size; else e[j].size = a[i].size; }
        if (!sl && a[i].mtime) e[j].mtime = a[i].mtime;
    }
    free(a); *out = e;
    return cnt;
}

static int gate(Job *j);
static void mkdirs(const char *path) {
    char t[PATH_MAX]; snprintf(t, sizeof t, "%s", path);
    for (char *q = t + 1; *q; q++) if (*q == '/') { *q = 0; mkdir(t, 0777); *q = '/'; }
    mkdir(t, 0777);
}
static int write_all(int fd, const void *b, size_t n) {
    for (size_t o = 0; o < n;) { ssize_t w = write(fd, (const char *)b + o, n - o); if (w < 0) return -1; o += w; }
    return 0;
}
static int is_match(const char *name, const char *inner, size_t il) { return !il || !strcmp(name, inner) || (!strncmp(name, inner, il) && name[il] == '/'); }

static long long arch_total(const char *arch, const char *inner) {
    int n; AEnt *a = arch_list(arch, &n); if (!a) return 0;
    long long t = 0; size_t il = strlen(inner);
    for (int i = 0; i < n; i++) if (!a[i].dir && is_match(a[i].name, inner, il)) t += a[i].size;
    free(a);
    return t;
}

static int zip_extract_file(Job *j, FILE *f, const AEnt *a, const char *dst) {
    unsigned char h[30];
    if (fseek(f, a->off, SEEK_SET) || fread(h, 1, 30, f) != 30 || rd32(h) != 0x04034b50) return -1;
    fseek(f, rd16(h + 26) + rd16(h + 28), SEEK_CUR);
    int out = open(dst, O_WRONLY | O_CREAT | O_EXCL, 0666); if (out < 0) return -1;
    unsigned char *ib = malloc(65536), *ob = malloc(65536); long long left = a->csize; int rc = 0;
    z_stream z; memset(&z, 0, sizeof z);
    if (a->method == 0) {
        while (!rc && left > 0) {
            size_t n = fread(ib, 1, left < 65536 ? left : 65536, f);
            if (!n || write_all(out, ib, n)) { rc = -1; break; }
            left -= n; j->done += n;
            if (gate(j)) rc = -2;
        }
    } else if (a->method == 8 && inflateInit2(&z, -15) == Z_OK) {
        int zr = Z_OK;
        while (zr != Z_STREAM_END && !rc) {
            if (!z.avail_in) {
                size_t want = left < 65536 ? left : 65536, n = want ? fread(ib, 1, want, f) : 0;
                if (!n) { rc = -1; break; }
                left -= n; z.next_in = ib; z.avail_in = n;
            }
            z.next_out = ob; z.avail_out = 65536;
            zr = inflate(&z, Z_NO_FLUSH);
            if (zr != Z_OK && zr != Z_STREAM_END) { rc = -1; break; }
            size_t o = 65536 - z.avail_out;
            if (write_all(out, ob, o)) { rc = -1; break; }
            j->done += o;
            if (gate(j)) rc = -2;
        }
        inflateEnd(&z);
    } else rc = -1;  // méthode de compression non gérée
    free(ib); free(ob); close(out);
    if (rc) unlink(dst);
    return rc;
}

// Extrait l'entrée « inner » (fichier ou dossier) de l'archive vers dst, qui est le chemin final de cette entrée
static int arch_extract(Job *j, const char *arch, const char *inner, const char *dst) {
    int n = 0, rc = 0; size_t il = strlen(inner);
    char d[PATH_MAX], par[PATH_MAX];
    if (arch_is_zip(arch)) {
        AEnt *a = zip_list(arch, &n); FILE *f = fopen(arch, "rb");
        if (!a || !f) { free(a); if (f) fclose(f); return -1; }
        for (int i = 0; i < n && !rc; i++) {
            if (!is_match(a[i].name, inner, il) || unsafe_name(a[i].name)) continue;
            snprintf(d, sizeof d, "%s%s", dst, a[i].name + il);
            if (a[i].dir) { mkdirs(d); continue; }
            dir_of(par, d); mkdirs(par);
            rc = zip_extract_file(j, f, &a[i], d);
        }
        free(a); fclose(f);
        return rc;
    }
    gzFile f = gzopen(arch, "rb"); if (!f) return -1;
    AEnt x; long long left; char *buf = malloc(65536);
    while (!rc && tar_header(f, &x, &left)) {
        long long consumed = 0;
        if (x.method && is_match(x.name, inner, il) && !unsafe_name(x.name)) {
            snprintf(d, sizeof d, "%s%s", dst, x.name + il);
            if (x.dir) mkdirs(d);
            else {
                dir_of(par, d); mkdirs(par);
                int out = open(d, O_WRONLY | O_CREAT | O_EXCL, 0666);
                if (out < 0) rc = -1;
                while (!rc && consumed < x.size) {
                    int want = x.size - consumed < 65536 ? (int)(x.size - consumed) : 65536, g = gzread(f, buf, want);
                    if (g <= 0 || write_all(out, buf, g)) { rc = -1; break; }
                    consumed += g; j->done += g;
                    if (gate(j)) rc = -2;
                }
                if (out >= 0) { close(out); if (rc) unlink(d); }
            }
        }
        if (!rc && gzseek(f, (left + 511) / 512 * 512 - consumed, SEEK_CUR) < 0) rc = -1;
    }
    free(buf); gzclose(f);
    return rc;
}

// Relit le dossier de l'onglet actif ; keep = nom sous le curseur à retrouver (ou NULL)
static void load_pane(int p, const char *keep) {
    Pane *pp = &P[p]; Tab *t = &pp->tabs[pp->atab];
    Ent *e = NULL; int n = 0, cap = 0; DIR *d = NULL;
    if (t->arch[0]) {
        n = arch_entries(t->arch, t->inner, show_hidden, &e);
        if (n < 0) {  // archive disparue ou illisible : retour dans son dossier
            say(T("Archive illisible : %s", "Cannot read archive: %s"), base_of(t->arch));
            dir_of(t->path, t->arch); t->arch[0] = t->inner[0] = 0; e = NULL; n = 0;
        }
    }
    if (!t->arch[0]) {
        d = opendir(t->path);
        if (!d && strcmp(t->path, "/")) { dir_of(t->path, t->path); d = opendir(t->path); }
    }
    if (!t->arch[0] && strcmp(t->path, "/")) { cap = 64; e = calloc(cap, sizeof *e); snprintf(e[n].name, 256, ".."); e[n].dir = 1; e[n].size = -2; n++; }
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
    if (!t->arch[0]) {  // les tailles des dossiers d'archive sont déjà connues
        SizeJob *j = calloc(1, sizeof *j); j->pane = p; j->gen = gen; snprintf(j->dir, PATH_MAX, "%s", t->path);
        j->names = calloc(n ? n : 1, 256);
        for (int i = 0; i < n; i++) if (e[i].dir && e[i].size == -1) snprintf(j->names[j->n++], 256, "%s", e[i].name);
        pthread_t th; pthread_create(&th, NULL, size_thread, j); pthread_detach(th);
    }
    dirty = 1;
}

static void go(int p, const char *path, const char *keep) {
    char rp[PATH_MAX];
    if (!realpath(path, rp)) return;
    Tab *t = &P[p].tabs[P[p].atab];
    snprintf(t->path, PATH_MAX, "%s", rp); t->arch[0] = t->inner[0] = 0; t->cur = t->top = 0;
    load_pane(p, keep);
}

// ---------------------------------------------------------------- file de transferts
static Job J[MAXJOBS]; static int nj;
static pthread_mutex_t qm = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t qc = PTHREAD_COND_INITIALIZER;

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
    else if (j->kind == J_EXTRACT) {
        char arch[PATH_MAX], dst[PATH_MAX], *inner = strchr(j->src, '\1');
        snprintf(arch, sizeof arch, "%.*s", (int)(inner - j->src), j->src); inner++;
        path_join(dst, j->dst, base_of(inner)); unique_name(dst);
        j->total = arch_total(arch, inner);
        rc = arch_extract(j, arch, inner, dst);
        if (rc) { Job tmp = {0}; rm_tree(&tmp, dst); }  // extraction partielle retirée
    } else {
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
static const unsigned RGB[2][NCOL] = {
    {0x1e1e1e, 0x252526, 0xd4d4d4, 0x8a8a8a, 0x7fb4ff, 0x2a2d2e, 0x094771, 0xe5c07b, 0x3a96dd, 0x3c3c3c, 0x4ec9b0, 0xf14c4c},
    {0xffffff, 0xf0f0f0, 0x1e1e1e, 0x6a6a6a, 0x0b5cad, 0xebebeb, 0xcce4ff, 0xb35c00, 0x3a96dd, 0xd0d0d0, 0x1a7f5a, 0xc62828}};
static int LIGHT;
static XftColor XC[NCOL]; static unsigned long PX[NCOL];
static void set_theme(void) {
    for (int i = 0; i < NCOL; i++) {
        unsigned c = RGB[LIGHT][i];
        XRenderColor rc = {((c >> 16) & 255) * 257, ((c >> 8) & 255) * 257, (c & 255) * 257, 0xffff};
        XftColorAllocValue(dpy, vis, cmap, &rc, &XC[i]); PX[i] = XC[i].pixel;
    }
}
// Préférences (langue, thème) dans ~/.config/s3bopus.conf
static void conf_path(char *b, size_t n) { const char *h = getenv("HOME"); snprintf(b, n, "%s/.config/s3bopus.conf", h ? h : "/tmp"); }
static void conf_save(void) { char b[PATH_MAX]; conf_path(b, sizeof b); FILE *f = fopen(b, "w"); if (f) { fprintf(f, "lang=%s\ntheme=%s\n", EN ? "en" : "fr", LIGHT ? "light" : "dark"); fclose(f); } }
static void conf_load(void) {
    char b[PATH_MAX], l[64]; conf_path(b, sizeof b); FILE *f = fopen(b, "r"); if (!f) return;
    while (fgets(l, sizeof l, f)) { if (!strncmp(l, "lang=en", 7)) EN = 1; else if (!strncmp(l, "lang=fr", 7)) EN = 0; else if (!strncmp(l, "theme=light", 11)) LIGHT = 1; else if (!strncmp(l, "theme=dark", 10)) LIGHT = 0; }
    fclose(f);
}

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
        char lab[PATH_MAX + 32]; snprintf(lab, sizeof lab, "%s %s", j->kind == J_COPY ? T("Copie", "Copy") : j->kind == J_MOVE ? T("Déplacement", "Move") : j->kind == J_EXTRACT ? T("Extraction", "Extract") : T("Suppression", "Delete"), base_of(j->kind == J_EXTRACT ? strchr(j->src, '\1') + 1 : j->src));
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
    int fy = H - rh - 2, fw = (W - 190) / 6;
    rect(0, fy, W, rh + 2, C_PANEL);
    for (int i = 0; i < 6; i++) {
        text(i * fw + 8, fy + 1, C_MARK, keys[i]);
        text(i * fw + 8 + text_w(keys[i]), fy + 1, C_TEXT, lab[i]);
    }
    // boutons langue et thème, à droite
    rect(W - 186, fy + 2, 40, rh - 2, !EN ? C_CURROW : C_SELROW); text(W - 186 + (40 - text_w("FR")) / 2, fy + 1, C_TEXT, "FR");
    rect(W - 142, fy + 2, 40, rh - 2, EN ? C_CURROW : C_SELROW); text(W - 142 + (40 - text_w("EN")) / 2, fy + 1, C_TEXT, "EN");
    const char *tl = LIGHT ? T("Clair", "Light") : T("Sombre", "Dark");
    rect(W - 94, fy + 2, 90, rh - 2, C_SELROW); text(W - 94 + (90 - text_w(tl)) / 2, fy + 1, C_TEXT, tl);
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
static int in_arch(int p) { return P[p].tabs[P[p].atab].arch[0]; }
static void go_arch(int p, const char *arch, const char *inner, const char *keep) {
    Tab *t = &P[p].tabs[P[p].atab]; char a[PATH_MAX], i[PATH_MAX];
    snprintf(a, PATH_MAX, "%s", arch); snprintf(i, PATH_MAX, "%s", inner);  // arch / inner peuvent pointer dans t
    snprintf(t->arch, PATH_MAX, "%s", a); snprintf(t->inner, PATH_MAX, "%s", i);
    snprintf(t->path, PATH_MAX, "%s%s%s", a, i[0] ? "/" : "", i);
    t->cur = t->top = 0; load_pane(p, keep);
}
// Dossier parent : dans une archive on remonte d'un niveau, puis on en sort
static void parent(int p) {
    Tab *t = &P[p].tabs[P[p].atab]; char prev[256], c[PATH_MAX];
    snprintf(prev, 256, "%s", base_of(t->path));
    if (t->arch[0] && t->inner[0]) {
        char in[PATH_MAX]; snprintf(in, PATH_MAX, "%s", t->inner);
        char *sl = strrchr(in, '/'); if (sl) *sl = 0; else in[0] = 0;
        go_arch(p, t->arch, in, prev);
    } else if (t->arch[0]) { dir_of(c, t->arch); go(p, c, prev); }
    else if (strcmp(t->path, "/")) { dir_of(c, t->path); go(p, c, prev); }
}
// Ouvrir un fichier d'archive : extrait dans /tmp/S3bopus-<uid> puis xdg-open
// shortcut: extraction synchrone (fige la fenêtre sur un gros fichier) et dossier temporaire nettoyé seulement au démarrage
static void open_from_arch(Tab *t, const char *inner) {
    char dir[PATH_MAX], dst[PATH_MAX]; snprintf(dir, sizeof dir, "/tmp/S3bopus-%d", (int)getuid());
    mkdir(dir, 0700); path_join(dst, dir, base_of(inner)); unique_name(dst);
    Job j; memset(&j, 0, sizeof j); j.sync = 1;
    if (arch_extract(&j, t->arch, inner, dst) == 0) spawn_open(dst); else say(T("Extraction impossible", "Cannot extract"));
}
static void enter(int p) {
    Pane *pp = &P[p]; Tab *t = &pp->tabs[pp->atab];
    if (t->cur >= pp->n) return;
    char name[256], c[PATH_MAX]; int dir;
    pthread_mutex_lock(&pm); snprintf(name, 256, "%s", pp->e[t->cur].name); dir = pp->e[t->cur].dir; pthread_mutex_unlock(&pm);
    if (!strcmp(name, "..")) { parent(p); return; }
    if (t->arch[0]) {
        snprintf(c, PATH_MAX, "%s%s%s", t->inner, t->inner[0] ? "/" : "", name);
        if (dir) go_arch(p, t->arch, c, NULL); else open_from_arch(t, c);
        return;
    }
    path_join(c, t->path, name);
    if (dir) go(p, c, NULL); else if (is_archive_name(name)) go_arch(p, c, "", NULL); else spawn_open(c);
}

// Chemin d'un élément du panneau ; dans une archive « archive\1chemin interne »
static void target_path(const Tab *t, char *out, const char *name) {
    if (t->arch[0]) snprintf(out, PATH_MAX, "%s\1%s%s%s", t->arch, t->inner, t->inner[0] ? "/" : "", name);
    else path_join(out, t->path, name);
}
// Éléments visés : les cochés, sinon celui sous le curseur (jamais « .. »). Renvoie le nombre de chemins écrits dans out.
static int targets(int p, char out[][PATH_MAX], int max) {
    Pane *pp = &P[p]; Tab *t = &pp->tabs[pp->atab]; int n = 0;
    pthread_mutex_lock(&pm);
    for (int i = 0; i < pp->n && n < max; i++) if (pp->e[i].sel && strcmp(pp->e[i].name, "..")) target_path(t, out[n++], pp->e[i].name);
    if (!n && t->cur < pp->n && strcmp(pp->e[t->cur].name, "..")) target_path(t, out[n++], pp->e[t->cur].name);
    pthread_mutex_unlock(&pm);
    return n;
}
static char tg[256][PATH_MAX];
static void read_only(void) { say(T("Archive en lecture seule : seule la copie (extraction) est possible", "Archive is read-only: only copy (extract) works")); }
static void transfer(int kind) {
    if (in_arch(!active)) { read_only(); return; }
    if (in_arch(active)) { if (kind == J_MOVE) { read_only(); return; } kind = J_EXTRACT; }
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

// ---------------------------------------------------------------- presse-papiers (Ctrl+V)
static Atom A_CLIP, A_TARGETS, A_PROP, A_INCR, A_UTF8, A_PNG, A_JPEG;

// Attend un SelectionNotify, ou un PropertyNotify « nouvelle valeur » de atom, pendant 3 s au plus
static int wait_ev(int type, Atom atom, XEvent *ev) {
    double end = now() + 3;
    while (now() < end) {
        while (XPending(dpy)) {
            XNextEvent(dpy, ev);
            if (type == SelectionNotify && ev->type == SelectionNotify) return 1;
            if (type == PropertyNotify && ev->type == PropertyNotify && ev->xproperty.atom == atom && ev->xproperty.state == PropertyNewValue) return 1;
            if (ev->type == Expose) dirty = 1;
        }
        fd_set f; FD_ZERO(&f); FD_SET(ConnectionNumber(dpy), &f);
        struct timeval tv = {0, 50000}; select(ConnectionNumber(dpy) + 1, &f, NULL, NULL, &tv);
    }
    return 0;
}
// Demande la sélection CLIPBOARD au format target ; gère le transfert par morceaux (INCR) des grosses images
static unsigned char *clip_get(Atom target, size_t *len, int *fmt) {
    XEvent ev; Atom ty; int f; unsigned long n, after; unsigned char *d;
    XDeleteProperty(dpy, win, A_PROP); XConvertSelection(dpy, A_CLIP, target, A_PROP, win, CurrentTime); XFlush(dpy);
    if (!wait_ev(SelectionNotify, 0, &ev) || ev.xselection.property == None) return NULL;
    if (XGetWindowProperty(dpy, win, A_PROP, 0, 1 << 24, True, AnyPropertyType, &ty, &f, &n, &after, &d) != Success) return NULL;
    *fmt = f;
    if (ty != A_INCR) {
        size_t b = n * (f == 32 ? sizeof(long) : f / 8); unsigned char *r = malloc(b + 1);
        memcpy(r, d, b); r[b] = 0; XFree(d); *len = b;
        return r;
    }
    XFree(d);
    size_t cap = 1 << 20, l = 0; unsigned char *r = malloc(cap);
    for (;;) {
        if (!wait_ev(PropertyNotify, A_PROP, &ev)) { free(r); return NULL; }
        if (XGetWindowProperty(dpy, win, A_PROP, 0, 1 << 24, True, AnyPropertyType, &ty, &f, &n, &after, &d) != Success) { free(r); return NULL; }
        size_t b = n * (f / 8);
        if (!b) { XFree(d); break; }
        if (l + b + 1 > cap) { cap = (l + b + 1) * 2; r = realloc(r, cap); }
        memcpy(r + l, d, b); l += b; XFree(d);
    }
    r[l] = 0; *len = l; *fmt = 8;
    return r;
}
// Crée un fichier avec le contenu du presse-papiers : image PNG/JPEG telle quelle, sinon texte en .txt
// shortcut: pas d'encodeur JPEG, l'image garde son format (PNG en général) ; les listes de fichiers copiés ne sont pas gérées
static void paste(void) {
    int p = active; Tab *t = &P[p].tabs[P[p].atab];
    if (in_arch(p)) { read_only(); return; }
    size_t len = 0; int fmt = 0, png = 0, jpg = 0, utf = 0, str = 0;
    unsigned char *tg = clip_get(A_TARGETS, &len, &fmt);
    Atom *at = (Atom *)tg; size_t na = tg && fmt == 32 ? len / sizeof(long) : 0;
    for (size_t i = 0; i < na; i++) { if (at[i] == A_PNG) png = 1; else if (at[i] == A_JPEG) jpg = 1; else if (at[i] == A_UTF8) utf = 1; else if (at[i] == XA_STRING) str = 1; }
    free(tg);
    Atom want; const char *name, *ext;
    if (png) { want = A_PNG; ext = ".png"; name = T("Image collée", "Pasted image"); }
    else if (jpg) { want = A_JPEG; ext = ".jpg"; name = T("Image collée", "Pasted image"); }
    else if (utf || !na) { want = A_UTF8; ext = ".txt"; name = T("Texte collé", "Pasted text"); }
    else if (str) { want = XA_STRING; ext = ".txt"; name = T("Texte collé", "Pasted text"); }
    else { say(T("Presse-papiers : format non géré", "Clipboard: unsupported format")); return; }
    unsigned char *data = clip_get(want, &len, &fmt);
    if (!data || !len) { free(data); say(T("Presse-papiers vide", "Clipboard is empty")); return; }
    char fn[300], c[PATH_MAX]; snprintf(fn, sizeof fn, "%s%s", name, ext);
    path_join(c, t->path, fn); unique_name(c);
    int fd = open(c, O_WRONLY | O_CREAT | O_EXCL, 0666);
    if (fd < 0 || write_all(fd, data, len)) say("%s: %s", T("Erreur", "Error"), strerror(errno));
    else { load_pane(p, base_of(c)); say(T("Collé : %s", "Pasted: %s"), base_of(c)); }
    if (fd >= 0) close(fd);
    free(data);
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
    case XK_BackSpace: parent(p); break;
    case XK_Tab: active = !active; dirty = 1; break;
    case XK_space: case XK_Insert: toggle_sel(p, 1); break;
    case XK_F2: { if (in_arch(p)) { read_only(); break; } int c = targets(p, tg, 1); if (c) start_prompt(2, T("Nouveau nom :", "New name:"), base_of(tg[0])); break; }
    case XK_F5: transfer(J_COPY); break;
    case XK_F6: transfer(J_MOVE); break;
    case XK_F7: if (in_arch(p)) { read_only(); break; } start_prompt(1, T("Nouveau dossier :", "New folder:"), ""); break;
    case XK_F8: case XK_Delete: { if (in_arch(p)) { read_only(); break; } int c = targets(p, tg, 256); if (c) { char l[160]; snprintf(l, sizeof l, T("Supprimer %d élément(s) ? (o/n)", "Delete %d item(s)? (y/n)"), c); start_prompt(3, l, ""); } break; }
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
        else if (ctrl && (ks == XK_v || ks == XK_V)) paste();
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
    if (be->y >= H - rh - 2) {
        if (be->x >= W - 186 && be->x < W - 146) EN = 0; else if (be->x >= W - 142 && be->x < W - 102) EN = 1;
        else if (be->x >= W - 94) { LIGHT = !LIGHT; set_theme(); }
        else return;
        conf_save(); dirty = 1; return;
    }
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
    const char *lg = getenv("LANG"); EN = !(lg && !strncmp(lg, "fr", 2)); conf_load();
    if (argc > 1 && !strcmp(argv[1], "--version")) { printf("S3bopus %s (X11)\n", VERSION); return 0; }
    if (!(dpy = XOpenDisplay(NULL))) { fprintf(stderr, "S3bopus : pas d'affichage X11 (DISPLAY)\n"); return 1; }
    scr = DefaultScreen(dpy); vis = DefaultVisual(dpy, scr); cmap = DefaultColormap(dpy, scr);
    font = XftFontOpenName(dpy, scr, "DejaVu Sans Mono:size=10");
    if (!font) font = XftFontOpenName(dpy, scr, "monospace:size=10");
    if (!font) { fprintf(stderr, "S3bopus : aucune police\n"); return 1; }
    asc = font->ascent; rh = font->ascent + font->descent + 6;
    set_theme();
    win = XCreateSimpleWindow(dpy, RootWindow(dpy, scr), 0, 0, W, H, 0, 0, PX[C_BG]);
    XStoreName(dpy, win, "S3bopus " VERSION);
    XClassHint ch = {"s3bopus", "S3bopus"}; XSetClassHint(dpy, win, &ch);
    XSelectInput(dpy, win, ExposureMask | KeyPressMask | ButtonPressMask | StructureNotifyMask | PropertyChangeMask);
    Atom del = XInternAtom(dpy, "WM_DELETE_WINDOW", False); XSetWMProtocols(dpy, win, &del, 1);
    gc = XCreateGC(dpy, win, 0, NULL);
    A_CLIP = XInternAtom(dpy, "CLIPBOARD", False); A_TARGETS = XInternAtom(dpy, "TARGETS", False); A_PROP = XInternAtom(dpy, "S3BOPUS_PASTE", False);
    A_INCR = XInternAtom(dpy, "INCR", False); A_UTF8 = XInternAtom(dpy, "UTF8_STRING", False);
    A_PNG = XInternAtom(dpy, "image/png", False); A_JPEG = XInternAtom(dpy, "image/jpeg", False);
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
    { char tmp[PATH_MAX]; Job tj = {0}; snprintf(tmp, sizeof tmp, "/tmp/S3bopus-%d", (int)getuid()); rm_tree(&tj, tmp); }
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
