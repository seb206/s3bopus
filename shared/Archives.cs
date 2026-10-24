using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if LIGHT
using System.IO.Compression;
#else
using SharpCompress.Archives;
using SharpCompress.Readers;
#endif

namespace S3bopus
{
    /// <summary>Un élément d'archive : Key est le chemin interne, séparé par « / ».</summary>
    internal sealed class ArchiveItem
    {
        public string Key;
        public bool IsDir;
        public long Size = -1;
        public DateTime Modified;
        public string Name => Key.Substring(Key.LastIndexOf('/') + 1);
    }

    /// <summary>
    /// Lecture des archives (ZIP, 7z, RAR, tar, tar.gz/bz2/xz) en lecture seule, partagée par les versions Windows et Linux.
    /// Un chemin « virtuel » désigne un dossier dans une archive : C:\x\a.zip\sous\dossier ou /home/x/a.zip/sous/dossier.
    /// </summary>
    internal static class Archives
    {
        // Ordre important : les doubles extensions avant ".gz"
#if LIGHT
        static readonly string[] TarCompressed = { };
        static readonly string[] Plain = { ".zip" };
#else
        static readonly string[] TarCompressed = { ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz" };
        static readonly string[] Plain = { ".zip", ".7z", ".rar", ".tar", ".gz" };
#endif
        static readonly char Sep = Path.DirectorySeparatorChar;

        public static bool IsArchive(string name) =>
            TarCompressed.Concat(Plain).Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        static bool IsTarCompressed(string file) => TarCompressed.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>Découpe un chemin virtuel en (fichier d'archive, chemin interne « a/b »). False si le chemin n'est pas dans une archive.</summary>
        public static bool Split(string path, out string file, out string inner)
        {
            file = inner = null;
            for (string p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                if (Directory.Exists(p)) return false; // un vrai dossier : rien au-dessus ne peut être une archive
                if (File.Exists(p) && IsArchive(p))
                {
                    file = p;
                    inner = path.Substring(p.Length).Trim(Sep).Replace(Sep, '/');
                    return true;
                }
            }
            return false;
        }

        /// <summary>Clé « a/b/c » : séparateurs « / », sans « ./ », « ../ » ni « / » en tête (les fichiers cachés « .x » gardent leur point).</summary>
        internal static string Norm(string key)
        {
            string k = (key ?? "").Replace('\\', '/').TrimEnd('/');
            while (true)
            {
                if (k.StartsWith("/")) k = k.Substring(1);
                else if (k.StartsWith("./")) k = k.Substring(2);
                else if (k.StartsWith("../")) k = k.Substring(3);
                else return k == "." || k == ".." ? "" : k;
            }
        }

        // Cache de la dernière archive lue (on navigue souvent dans la même)
        static readonly object _lock = new object();
        static string _cachedFile;
        static DateTime _cachedTime;
        static List<ArchiveItem> _cached;

        /// <summary>Tous les éléments de l'archive, dossiers implicites compris.</summary>
        public static List<ArchiveItem> List(string file)
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            lock (_lock)
                if (_cachedFile == file && _cachedTime == stamp) return _cached;

            var items = new Dictionary<string, ArchiveItem>(StringComparer.Ordinal);
            void Add(string key, bool dir, long size, DateTime date)
            {
                if (key.Length == 0) return;
                items[key] = new ArchiveItem { Key = key, IsDir = dir, Size = dir ? -1 : size, Modified = date };
                // Certaines archives ne contiennent pas d'entrée pour les dossiers parents
                for (int i = key.LastIndexOf('/'); i > 0; i = key.LastIndexOf('/', i - 1))
                {
                    string parent = key.Substring(0, i);
                    if (items.ContainsKey(parent)) break;
                    items[parent] = new ArchiveItem { Key = parent, IsDir = true, Modified = date };
                }
            }

#if LIGHT
            using (var zip = ZipFile.OpenRead(file))
                foreach (var e in zip.Entries)
                    Add(Norm(e.FullName), e.FullName.EndsWith("/"), e.Length, e.LastWriteTime.DateTime);
#else
            if (IsTarCompressed(file))
                using (var fs = File.OpenRead(file))
                using (var reader = ReaderFactory.OpenReader(fs))
                    while (reader.MoveToNextEntry())
                        Add(Norm(reader.Entry.Key), reader.Entry.IsDirectory, reader.Entry.Size, reader.Entry.LastModifiedTime ?? DateTime.MinValue);
            else
                using (var archive = ArchiveFactory.OpenArchive(file))
                    foreach (var e in archive.Entries)
                        Add(Norm(e.Key), e.IsDirectory, e.Size, e.LastModifiedTime ?? DateTime.MinValue);
#endif

            var list = items.Values.ToList();
            lock (_lock) { _cachedFile = file; _cachedTime = stamp; _cached = list; }
            return list;
        }

        /// <summary>Contenu direct du dossier interne « inner » ("" = racine de l'archive).</summary>
        public static List<ArchiveItem> Children(IEnumerable<ArchiveItem> all, string inner)
        {
            string prefix = inner.Length == 0 ? "" : inner + "/";
            return all.Where(i => i.Key.Length > prefix.Length && i.Key.StartsWith(prefix, StringComparison.Ordinal) && i.Key.IndexOf('/', prefix.Length) < 0).ToList();
        }

        /// <summary>
        /// Extrait les éléments « inners » (fichiers ou dossiers internes) dans destDir, en gardant leur nom.
        /// Renvoie les chemins créés au premier niveau (ce qu'on copiera ou ouvrira ensuite).
        /// </summary>
        public static List<string> Extract(string file, IList<string> inners, string destDir)
        {
            string root = Path.GetFullPath(destDir).TrimEnd(Sep) + Sep;
            var top = new List<string>();

            void Write(string rawKey, bool isDir, DateTime? date, Action<Stream> copy)
            {
                string key = Norm(rawKey);
                string match = inners.FirstOrDefault(p => key == p || key.StartsWith(p + "/", StringComparison.Ordinal));
                if (match == null) return;
                int cut = match.LastIndexOf('/') + 1;
                string target = Path.GetFullPath(Path.Combine(root, key.Substring(cut).Replace('/', Sep)));
                // Protection « zip slip » : rien ne doit sortir du dossier de destination
                if (!target.StartsWith(root, StringComparison.Ordinal)) throw new IOException("Chemin interdit dans l'archive : " + rawKey);
                string first = Path.Combine(root, match.Substring(cut));
                if (!top.Contains(first)) top.Add(first);
                if (isDir) { Directory.CreateDirectory(target); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var output = File.Create(target)) copy(output);
                if (date is DateTime t) try { File.SetLastWriteTime(target, t); } catch { }
            }

#if LIGHT
            using (var zip = ZipFile.OpenRead(file))
                foreach (var e in zip.Entries)
                    Write(e.FullName, e.FullName.EndsWith("/"), e.LastWriteTime.DateTime, s => { using (var es = e.Open()) es.CopyTo(s); });
#else
            void ReadAll(SharpCompress.Readers.IReader reader)
            {
                while (reader.MoveToNextEntry())
                    Write(reader.Entry.Key, reader.Entry.IsDirectory, reader.Entry.LastModifiedTime, reader.WriteEntryTo);
            }

            if (IsTarCompressed(file))
                using (var fs = File.OpenRead(file))
                using (var reader = ReaderFactory.OpenReader(fs))
                    ReadAll(reader);
            else
                using (var archive = ArchiveFactory.OpenArchive(file))
                {
                    // Archives « solides » (7z, certains RAR) : lecture séquentielle, sinon chaque fichier redécompresserait tout depuis le début
                    if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
                        using (var reader = archive.ExtractAllEntries()) ReadAll(reader);
                    else
                        foreach (var e in archive.Entries)
                            Write(e.Key, e.IsDirectory, e.LastModifiedTime, s => { using (var es = e.OpenEntryStream()) es.CopyTo(s); });
                }
#endif

            // Dossiers vides dans l'archive (ou sans entrée propre) : on les crée quand même
            foreach (var t in top) if (!File.Exists(t)) Directory.CreateDirectory(t);
            return top;
        }

        /// <summary>Dossier temporaire neuf pour une extraction (ouvrir un fichier, copier hors de l'archive).</summary>
        public static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "S3bopus", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Au démarrage : supprime les extractions des sessions précédentes.</summary>
        public static void CleanTemp()
        {
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "S3bopus"), true); } catch { }
        }
    }

    /// <summary>Historique des emplacements saisis dans la barre d'adresse (le plus récent en premier).</summary>
    internal static class PathHistory
    {
        const char Sep = '\u001f'; // séparateur invisible : ne peut pas apparaître dans un chemin saisi
        const int Max = 25;
        static StringComparison Cmp => Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static List<string> Items => (Settings.Get("History") ?? "").Split(Sep).Where(s => s.Length > 0).ToList();

        public static void Add(string path)
        {
            var list = Items;
            list.RemoveAll(p => string.Equals(p, path, Cmp));
            list.Insert(0, path);
            if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
            Settings.Set("History", string.Join(Sep.ToString(), list));
        }

        public static void Clear() => Settings.Set("History", "");
    }
}
