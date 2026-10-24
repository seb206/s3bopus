using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace S3bopus
{
    /// <summary>Langue de l'interface : Lang.T("français", "english").</summary>
    internal static class Lang
    {
        public static bool En;
        public static string T(string fr, string en) => En ? en : fr;
    }

    internal static class AppInfo
    {
        public const string Version = "0.0.2";
        public const string Date = "2026-10-23";
    }

    internal static class Fmt
    {
        // InvariantGlobalization : pas de cultures système, on fixe les séparateurs nous-mêmes
        static NumberFormatInfo Nf => Lang.En
            ? new NumberFormatInfo { NumberGroupSeparator = ",", NumberDecimalSeparator = "." }
            : new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalSeparator = "," };

        public static string Size(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return bytes.ToString("N0", Nf) + (Lang.En ? (bytes > 1 ? " bytes" : " byte") : (bytes > 1 ? " octets" : " octet"));
            string[] units = Lang.En ? new[] { "KB", "MB", "GB", "TB", "PB" } : new[] { "Ko", "Mo", "Go", "To", "Po" };
            double v = bytes / 1024.0;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            string f = v < 10 ? "0.##" : v < 100 ? "0.#" : "N0";
            return v.ToString(f, Nf) + " " + units[u];
        }

        public static string Count(long n, string singular, string plural) => n.ToString("N0", Nf) + " " + (n > 1 ? plural : singular);

        public static string Duration(TimeSpan t) =>
            t.TotalHours >= 1 ? (int)t.TotalHours + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                              : t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");

        public static string Date(DateTime d) => d == DateTime.MinValue ? "" : d.ToString(Lang.En ? "yyyy-MM-dd HH:mm" : "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        public static string TypeName(string name, bool isDir)
        {
            if (isDir) return Lang.T("Dossier", "Folder");
            string ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            return ext.Length == 0 ? Lang.T("Fichier", "File") : Lang.T("Fichier ", "") + ext + Lang.T("", " file");
        }
    }

    /// <summary>Réglages persistants dans ~/.config/S3bopus/S3bopus.ini.</summary>
    internal static class Settings
    {
        static readonly Dictionary<string, string> _v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "S3bopus", "S3bopus.ini");

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) _v[line.Substring(0, i).Trim()] = line.Substring(i + 1);
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, _v.Select(kv => kv.Key + "=" + kv.Value));
            }
            catch { }
        }

        public static string Get(string key, string def = null) => _v.TryGetValue(key, out var v) ? v : def;
        public static bool GetBool(string key, bool def) => _v.TryGetValue(key, out var v) ? v == "1" : def;
        public static int GetInt(string key, int def) => _v.TryGetValue(key, out var v) && int.TryParse(v, out int i) ? i : def;
        public static void Set(string key, object value) => _v[key] = value is bool b ? (b ? "1" : "0") : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    internal static class Sys
    {
        public static void Open(string path) => Run("xdg-open", path);

        public static void Run(string exe, string arg, string workDir = null)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (arg != null) psi.ArgumentList.Add(arg);
            if (workDir != null) psi.WorkingDirectory = workDir;
            Process.Start(psi);
        }

        /// <summary>
        /// Corbeille freedesktop (~/.local/share/Trash), la même que celle de Nautilus, Dolphin, Thunar...
        /// shortcut: un fichier d'un autre disque est déplacé (copié) vers la corbeille de l'utilisateur au lieu de $topdir/.Trash-uid ; à faire si c'est lent sur gros fichiers.
        /// </summary>
        public static void Trash(string path)
        {
            string home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            string files = Path.Combine(home, "Trash", "files"), info = Path.Combine(home, "Trash", "info");
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(info);
            string name = Path.GetFileName(path.TrimEnd('/'));
            string target = name;
            for (int i = 2; File.Exists(Path.Combine(files, target)) || Directory.Exists(Path.Combine(files, target)) || File.Exists(Path.Combine(info, target + ".trashinfo")); i++)
                target = name + "." + i;
            File.WriteAllText(Path.Combine(info, target + ".trashinfo"),
                "[Trash Info]\nPath=" + Uri.EscapeDataString(path).Replace("%2F", "/") + "\nDeletionDate=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "\n");
            try
            {
                if (Directory.Exists(path)) MoveDir(path, Path.Combine(files, target));
                else File.Move(path, Path.Combine(files, target));
            }
            catch { File.Delete(Path.Combine(info, target + ".trashinfo")); throw; }
        }

        /// <summary>Directory.Move échoue entre deux disques : on copie puis on supprime.</summary>
        static void MoveDir(string src, string dst)
        {
            try { Directory.Move(src, dst); return; }
            catch (IOException) when (!Directory.Exists(dst)) { }
            CopyDir(src, dst);
            Directory.Delete(src, true);
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        public static bool IsLink(FileSystemInfo fi) => fi.LinkTarget != null;
    }

    // ================================================================== tailles de dossiers

    internal sealed class DirSize
    {
        public long Bytes, Files, Folders;
        public bool Complete;
        public int Errors;
    }

    /// <summary>Calcul (en arrière-plan) et cache partagé des tailles de dossiers.</summary>
    internal static class DirSizes
    {
        static readonly ConcurrentDictionary<string, DirSize> _cache = new ConcurrentDictionary<string, DirSize>(StringComparer.Ordinal);
        static string Norm(string p) => p.Length > 1 ? p.TrimEnd('/') : p;

        public static DirSize Get(string path) => _cache.TryGetValue(Norm(path), out var d) ? d : null;

        /// <summary>Oublie la taille de ce chemin, de ses parents et de ses enfants.</summary>
        public static void Invalidate(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string p = Norm(path);
            foreach (var key in _cache.Keys)
                if (p == key || p.StartsWith(key + "/", StringComparison.Ordinal) || key.StartsWith(p + "/", StringComparison.Ordinal))
                    _cache.TryRemove(key, out _);
        }

        public static void Compute(IList<string> folders, bool force, CancellationToken ct, Action<string, DirSize> progress)
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            foreach (var folder in folders)
            {
                if (ct.IsCancellationRequested) return;
                string key = Norm(folder);
                if (!force && _cache.TryGetValue(key, out var existing) && existing.Complete) { progress(folder, existing); continue; }

                var size = new DirSize();
                _cache[key] = size;
                var last = DateTime.UtcNow;
                var stack = new Stack<string>();
                stack.Push(folder);
                while (stack.Count > 0)
                {
                    if (ct.IsCancellationRequested) { _cache.TryRemove(key, out _); return; }
                    string dir = stack.Pop();
                    try
                    {
                        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
                        {
                            if (e is DirectoryInfo)
                            {
                                Interlocked.Increment(ref size.Folders);
                                // Ne pas suivre les liens symboliques (boucles, doubles comptes)
                                if (!Sys.IsLink(e)) stack.Push(e.FullName);
                            }
                            else
                            {
                                if (!Sys.IsLink(e)) Interlocked.Add(ref size.Bytes, ((FileInfo)e).Length);
                                Interlocked.Increment(ref size.Files);
                            }
                        }
                    }
                    catch { size.Errors++; }

                    if ((DateTime.UtcNow - last).TotalMilliseconds > 250)
                    {
                        last = DateTime.UtcNow;
                        progress(folder, size);
                    }
                }
                size.Complete = true;
                progress(folder, size);
            }
        }
    }

    // ================================================================== file de transferts

    internal enum JobKind { Copy, Move }
    internal enum JobState { Waiting, Scanning, Running, Done, DoneWithErrors, Canceled, Failed }
    internal enum ConflictChoice { Ask, Replace, Skip, KeepBoth, ReplaceIfNewer, CancelJob }

    internal sealed class ConflictInfo
    {
        public string Source, Destination;
        public long SourceSize, DestSize;
        public DateTime SourceDate, DestDate;
        public bool ApplyToAll;
    }

    internal sealed class TransferJob
    {
        static int _ids;
        public readonly int Id = Interlocked.Increment(ref _ids);
        public JobKind Kind;
        public string[] Sources;
        public string DestDir;
        public volatile JobState State = JobState.Waiting;
        public long TotalBytes, DoneBytes;
        public int TotalFiles, DoneFiles;
        public string Current = "";
        public DateTime Started, Finished;
        public readonly List<string> Errors = new List<string>();
        public ConflictChoice Conflicts = ConflictChoice.Ask;
        internal volatile bool CancelRequested;
        public void AddError(string message) { lock (Errors) Errors.Add(message); }

        // Statistiques d'affichage (UI)
        public double Speed;
        internal long LastBytes;
        internal DateTime LastTick;

        public bool IsFinished => State == JobState.Done || State == JobState.DoneWithErrors || State == JobState.Canceled || State == JobState.Failed;

        public int Percent => TotalBytes > 0 ? (int)Math.Min(100, DoneBytes * 100 / TotalBytes) : (IsFinished ? 100 : 0);

        public string KindText => Kind == JobKind.Copy ? Lang.T("Copie", "Copy") : Lang.T("Déplacement", "Move");

        public string SourceText => Sources.Length == 1 ? Sources[0] : Sources.Length + Lang.T(" éléments de ", " items from ") + Path.GetDirectoryName(Sources[0]);

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case JobState.Waiting: return Lang.T("En attente", "Waiting");
                    case JobState.Scanning: return Lang.T("Analyse…", "Scanning…");
                    case JobState.Running: return TransferQueue.Paused ? Lang.T("En pause", "Paused") : Lang.T("En cours", "Running");
                    case JobState.Done: return Lang.T("Terminé", "Done");
                    case JobState.DoneWithErrors: return Lang.T("Terminé (", "Done (") + Errors.Count + Lang.T(" erreur", " error") + (Errors.Count > 1 ? "s" : "") + ")";
                    case JobState.Canceled: return Lang.T("Annulé", "Canceled");
                    default: return Lang.T("Échec", "Failed");
                }
            }
        }
    }

    /// <summary>
    /// File de transferts : les tâches (copie / déplacement) sont exécutées une par une,
    /// dans un thread dédié, avec pause, annulation et gestion des conflits.
    /// </summary>
    internal sealed class TransferQueue
    {
        readonly List<TransferJob> _jobs = new List<TransferJob>();
        readonly AutoResetEvent _signal = new AutoResetEvent(false);
        static readonly ManualResetEvent _runGate = new ManualResetEvent(true);
        static volatile bool _paused;

        /// <summary>Appelé (thread de travail) quand l'état d'une tâche change.</summary>
        public event Action<TransferJob> JobChanged;
        /// <summary>Résolution des conflits : appelé depuis le thread de travail, doit être synchrone.</summary>
        public Func<TransferJob, ConflictInfo, ConflictChoice> ResolveConflict;

        public TransferQueue()
        {
            new Thread(Worker) { IsBackground = true, Name = "S3bopus transferts" }.Start();
        }

        public static bool Paused => _paused;

        public TransferJob[] Jobs { get { lock (_jobs) return _jobs.ToArray(); } }

        public TransferJob Enqueue(JobKind kind, IEnumerable<string> sources, string destDir)
        {
            var job = new TransferJob { Kind = kind, Sources = sources.ToArray(), DestDir = destDir };
            lock (_jobs) _jobs.Add(job);
            _signal.Set();
            JobChanged?.Invoke(job);
            return job;
        }

        public void Cancel(TransferJob job)
        {
            job.CancelRequested = true;
            if (job.State == JobState.Waiting) { job.State = JobState.Canceled; JobChanged?.Invoke(job); }
        }

        public void Remove(TransferJob job)
        {
            if (!job.IsFinished) return;
            lock (_jobs) _jobs.Remove(job);
        }

        public void ClearFinished() { lock (_jobs) _jobs.RemoveAll(j => j.IsFinished); }

        public void Retry(TransferJob job)
        {
            if (!job.IsFinished) return;
            lock (_jobs) _jobs.Remove(job);
            Enqueue(job.Kind, job.Sources.Where(s => File.Exists(s) || Directory.Exists(s)), job.DestDir);
        }

        public void MoveJob(TransferJob job, int delta)
        {
            lock (_jobs)
            {
                int i = _jobs.IndexOf(job), j = i + delta;
                if (i < 0 || j < 0 || j >= _jobs.Count) return;
                _jobs[i] = _jobs[j]; _jobs[j] = job;
            }
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused) _runGate.Reset(); else _runGate.Set();
        }

        void Worker()
        {
            while (true)
            {
                TransferJob job;
                lock (_jobs) job = _jobs.FirstOrDefault(j => j.State == JobState.Waiting);
                if (job == null) { _signal.WaitOne(); continue; }
                _runGate.WaitOne();
                if (job.State != JobState.Waiting) continue;
                try { Run(job); }
                catch (Exception ex)
                {
                    job.AddError(ex.Message);
                    job.State = JobState.Failed;
                }
                job.Finished = DateTime.Now;
                JobChanged?.Invoke(job);
            }
        }

        // ---------------------------------------------------------------------------------

        enum OpKind { MkDir, File, QuickMove }
        sealed class Op { public OpKind Kind; public string Src, Dst; public long Size; public DateTime Date; }

        static bool Exists(string p) => File.Exists(p) || Directory.Exists(p) || new FileInfo(p).LinkTarget != null;

        static string Unique(string dir, string name, bool copySuffix)
        {
            string baseName = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            string b = copySuffix ? baseName + Lang.T(" - Copie", " - Copy") : baseName;
            string candidate = Path.Combine(dir, b + ext);
            for (int i = 2; Exists(candidate); i++) candidate = Path.Combine(dir, b + " (" + i + ")" + ext);
            return candidate;
        }

        /// <summary>Renommage direct (même système de fichiers) ; false si les deux chemins sont sur des disques différents.</summary>
        static bool TryRename(string src, string dst, bool isDir)
        {
            try
            {
                if (isDir) { Directory.Move(src, dst); return true; }
                return TryMoveFile(src, dst, false);
            }
            catch (IOException) when (isDir && !Directory.Exists(dst)) { return false; } // EXDEV : autre disque
        }

        void Run(TransferJob job)
        {
            job.Started = DateTime.Now;
            job.State = JobState.Scanning;
            JobChanged?.Invoke(job);

            // ---- 1. Analyse : liste des opérations
            var ops = new List<Op>();
            var dirsToRemove = new List<string>();
            string destDir = job.DestDir.Length > 1 ? job.DestDir.TrimEnd('/') : job.DestDir;
            foreach (var src0 in job.Sources)
            {
                if (job.CancelRequested) break;
                string src = src0.Length > 1 ? src0.TrimEnd('/') : src0;
                string name = Path.GetFileName(src);
                if (string.IsNullOrEmpty(name)) { job.AddError(Lang.T("Impossible de transférer la racine : ", "Can't transfer the root: ") + src0); continue; }
                var fsi = new FileInfo(src);
                bool isLink = fsi.LinkTarget != null;
                bool isDir = !isLink && Directory.Exists(src);
                if (!isDir && !isLink && !File.Exists(src)) { job.AddError(Lang.T("Introuvable : ", "Not found: ") + src); continue; }

                bool sameFolder = Path.GetDirectoryName(src) == destDir;
                if (sameFolder && job.Kind == JobKind.Move) continue; // rien à faire
                string dst = sameFolder ? Unique(destDir, name, true) : Path.Combine(destDir, name);

                if (isDir && (destDir + "/").StartsWith(src + "/", StringComparison.Ordinal))
                {
                    job.AddError(Lang.T("Le dossier de destination est un sous-dossier du dossier source : ", "The destination folder is a subfolder of the source folder: ") + src);
                    continue;
                }

                if (job.Kind == JobKind.Move && !Exists(dst))
                {
                    // Même disque : simple renommage, instantané
                    ops.Add(new Op { Kind = OpKind.QuickMove, Src = src, Dst = dst, Size = isDir ? -1 : 0 });
                    job.TotalFiles++;
                    continue;
                }

                if (!isDir)
                {
                    long len = isLink ? 0 : fsi.Length;
                    ops.Add(new Op { Kind = OpKind.File, Src = src, Dst = dst, Size = len, Date = fsi.LastWriteTime });
                    job.TotalBytes += len; job.TotalFiles++;
                    continue;
                }
                Scan(job, src, dst, ops, dirsToRemove);
            }

            if (job.CancelRequested) { job.State = JobState.Canceled; return; }
            job.State = JobState.Running;
            JobChanged?.Invoke(job);

            // ---- 2. Exécution
            long baseBytes = 0;
            for (int k = 0; k < ops.Count; k++)
            {
                var op = ops[k];
                _runGate.WaitOne();
                if (job.CancelRequested) break;
                job.Current = op.Src;
                try
                {
                    switch (op.Kind)
                    {
                        case OpKind.MkDir:
                            Directory.CreateDirectory(op.Dst);
                            break;

                        case OpKind.QuickMove:
                            if (TryRename(op.Src, op.Dst, op.Size < 0)) { job.DoneFiles++; break; }
                            // Autre disque : copie puis suppression, comme un déplacement normal
                            job.TotalFiles--;
                            var extra = new List<Op>();
                            if (op.Size < 0) Scan(job, op.Src, op.Dst, extra, dirsToRemove);
                            else
                            {
                                var fi = new FileInfo(op.Src);
                                long len = Sys.IsLink(fi) ? 0 : fi.Length;
                                extra.Add(new Op { Kind = OpKind.File, Src = op.Src, Dst = op.Dst, Size = len, Date = fi.LastWriteTime });
                                job.TotalBytes += len; job.TotalFiles++;
                            }
                            ops.InsertRange(k + 1, extra);
                            break;

                        case OpKind.File:
                            CopyOne(job, op, ref baseBytes);
                            break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    job.AddError(op.Src + " : " + ex.Message);
                    if (op.Kind == OpKind.File) { baseBytes += op.Size; job.DoneBytes = baseBytes; job.DoneFiles++; }
                }
            }

            if (job.CancelRequested) { job.State = JobState.Canceled; return; }

            // Déplacement inter-disques : suppression des dossiers sources vidés
            for (int i = dirsToRemove.Count - 1; i >= 0; i--)
                try { Directory.Delete(dirsToRemove[i], false); } catch { }

            job.Current = "";
            job.DoneBytes = job.TotalBytes;
            job.State = job.Errors.Count == 0 ? JobState.Done : JobState.DoneWithErrors;
        }

        static void Scan(TransferJob job, string src, string dst, List<Op> ops, List<string> dirsToRemove)
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 };
            var stack = new Stack<(string s, string d)>();
            stack.Push((src, dst));
            while (stack.Count > 0 && !job.CancelRequested)
            {
                var (s, d) = stack.Pop();
                ops.Add(new Op { Kind = OpKind.MkDir, Src = s, Dst = d });
                if (job.Kind == JobKind.Move) dirsToRemove.Add(s);
                try
                {
                    foreach (var e in new DirectoryInfo(s).EnumerateFileSystemInfos("*", opts))
                    {
                        string cd = Path.Combine(d, e.Name);
                        if (e is DirectoryInfo && !Sys.IsLink(e)) stack.Push((e.FullName, cd));
                        else
                        {
                            long len = e is FileInfo fi && !Sys.IsLink(e) ? fi.Length : 0;
                            ops.Add(new Op { Kind = OpKind.File, Src = e.FullName, Dst = cd, Size = len, Date = e.LastWriteTime });
                            job.TotalBytes += len; job.TotalFiles++;
                        }
                    }
                }
                catch (Exception ex) { job.AddError(s + " : " + ex.Message); }
            }
        }

        void CopyOne(TransferJob job, Op op, ref long baseBytes)
        {
            string dst = op.Dst;
            bool replacing = false;
            if (Exists(dst))
            {
                var choice = job.Conflicts;
                if (choice == ConflictChoice.Ask)
                {
                    var di = new FileInfo(dst);
                    var info = new ConflictInfo
                    {
                        Source = op.Src, Destination = dst,
                        SourceSize = op.Size, SourceDate = op.Date,
                        DestSize = di.Exists ? di.Length : 0, DestDate = di.Exists ? di.LastWriteTime : DateTime.MinValue
                    };
                    choice = ResolveConflict?.Invoke(job, info) ?? ConflictChoice.Skip;
                    if (info.ApplyToAll && choice != ConflictChoice.CancelJob) job.Conflicts = choice;
                }
                if (choice == ConflictChoice.ReplaceIfNewer)
                    choice = op.Date > File.GetLastWriteTime(dst) ? ConflictChoice.Replace : ConflictChoice.Skip;

                switch (choice)
                {
                    case ConflictChoice.CancelJob:
                        job.CancelRequested = true;
                        throw new OperationCanceledException();
                    case ConflictChoice.Skip:
                        baseBytes += op.Size; job.DoneBytes = baseBytes; job.DoneFiles++;
                        return;
                    case ConflictChoice.KeepBoth:
                        dst = Unique(Path.GetDirectoryName(dst), Path.GetFileName(dst), false);
                        break;
                    default:
                        replacing = true;
                        break;
                }
            }

            var src = new FileInfo(op.Src);
            if (src.LinkTarget != null)
            {
                // Lien symbolique : recréé tel quel, comme cp -a
                if (replacing) File.Delete(dst);
                File.CreateSymbolicLink(dst, src.LinkTarget);
                if (job.Kind == JobKind.Move) File.Delete(op.Src);
            }
            else if (job.Kind == JobKind.Move && TryMoveFile(op.Src, dst, replacing)) { }
            else
            {
                string tmp = dst + ".s3bopus-part";
                try
                {
                    using (var input = new FileStream(op.Src, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                    using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1))
                    {
                        var buf = new byte[1024 * 1024];
                        long done = 0;
                        int n;
                        while ((n = input.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (_paused) _runGate.WaitOne();
                            if (job.CancelRequested) throw new OperationCanceledException();
                            output.Write(buf, 0, n);
                            done += n;
                            job.DoneBytes = baseBytes + done;
                        }
                    }
                    File.SetLastWriteTime(tmp, src.LastWriteTime);
                    if (!OperatingSystem.IsWindows()) try { File.SetUnixFileMode(tmp, src.UnixFileMode); } catch { }
                    File.Move(tmp, dst, true);
                }
                catch { try { File.Delete(tmp); } catch { } throw; }

                if (job.Kind == JobKind.Move)
                    try { File.Delete(op.Src); }
                    catch (Exception ex) { job.AddError(Lang.T("Copié mais non supprimé : ", "Copied but not deleted: ") + op.Src + " : " + ex.Message); }
            }
            baseBytes += op.Size;
            job.DoneBytes = baseBytes;
            job.DoneFiles++;
        }

        /// <summary>Déplacement par renommage si possible (même disque) ; false s'il faut copier.</summary>
        static bool TryMoveFile(string src, string dst, bool overwrite)
        {
            // File.Move copie en silence entre deux disques (sans progression ni pause) : on vérifie d'abord le disque
            if (MountOf(src) != MountOf(Path.GetDirectoryName(dst))) return false;
            File.Move(src, dst, overwrite);
            return true;
        }

        static string MountOf(string path)
        {
            string full = Path.GetFullPath(path), best = "/";
            foreach (var d in DriveInfo.GetDrives())
            {
                string root = d.RootDirectory.FullName;
                if (root.Length > best.Length && (full == root.TrimEnd('/') || full.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal))) best = root;
            }
            return best;
        }
    }
}
