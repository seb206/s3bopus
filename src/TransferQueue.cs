using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace S3bopus
{
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

        public string KindText => Kind == JobKind.Copy ? Lang.T("Copie", "Copy") : Lang.T("Déplacement", "Move");

        public string SourceText
        {
            get
            {
                if (Sources.Length == 1) return Sources[0];
                return Sources.Length + Lang.T(" éléments de ", " items from ") + Path.GetDirectoryName(Sources[0]);
            }
        }

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
            var t = new Thread(Worker) { IsBackground = true, Name = "S3bopus transferts" };
            t.Start();
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

        public void ClearFinished()
        {
            lock (_jobs) _jobs.RemoveAll(j => j.IsFinished);
        }

        public void Retry(TransferJob job)
        {
            if (!job.IsFinished) return;
            var copy = Enqueue(job.Kind, job.Sources.Where(s => File.Exists(s) || Directory.Exists(s)), job.DestDir);
            lock (_jobs) { _jobs.Remove(job); }
            JobChanged?.Invoke(copy);
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

        static bool IsSameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

        static bool Exists(string p) => Native.GetFileAttributes(Native.LongPath(p)) != Native.INVALID_FILE_ATTRIBUTES;

        static string Unique(string dir, string name, bool copySuffix)
        {
            string baseName = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            string b = copySuffix ? baseName + Lang.T(" - Copie", " - Copy") : baseName;
            string candidate = Path.Combine(dir, b + ext);
            for (int i = 2; Exists(candidate); i++) candidate = Path.Combine(dir, b + " (" + i + ")" + ext);
            return candidate;
        }

        void Run(TransferJob job)
        {
            job.Started = DateTime.Now;
            job.State = JobState.Scanning;
            JobChanged?.Invoke(job);

            // ---- 1. Analyse : liste des opérations
            var ops = new List<Op>();
            var dirsToRemove = new List<string>();
            foreach (var src0 in job.Sources)
            {
                if (job.CancelRequested) break;
                string src = src0.TrimEnd('\\');
                string name = Path.GetFileName(src);
                if (string.IsNullOrEmpty(name)) { job.AddError(Lang.T("Impossible de transférer un lecteur entier : ", "Can't transfer a whole drive: ") + src0); continue; }
                bool isDir = Directory.Exists(src);
                if (!isDir && !File.Exists(src)) { job.AddError(Lang.T("Introuvable : ", "Not found: ") + src); continue; }

                string srcParent = Path.GetDirectoryName(src);
                bool sameFolder = string.Equals(srcParent?.TrimEnd('\\'), job.DestDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                if (sameFolder && job.Kind == JobKind.Move) continue; // rien à faire
                string dst = sameFolder ? Unique(job.DestDir, name, true) : Path.Combine(job.DestDir, name);

                if (isDir && (job.DestDir.TrimEnd('\\') + "\\").StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    job.AddError(Lang.T("Le dossier de destination est un sous-dossier du dossier source : ", "The destination folder is a subfolder of the source folder: ") + src);
                    continue;
                }

                if (job.Kind == JobKind.Move && IsSameVolume(src, job.DestDir) && !Exists(dst))
                {
                    ops.Add(new Op { Kind = OpKind.QuickMove, Src = src, Dst = dst });
                    job.TotalFiles++;
                    continue;
                }

                if (!isDir)
                {
                    var fi = new FileInfo(src);
                    ops.Add(new Op { Kind = OpKind.File, Src = src, Dst = dst, Size = fi.Length, Date = fi.LastWriteTime });
                    job.TotalBytes += fi.Length; job.TotalFiles++;
                    continue;
                }

                // Dossier : parcours récursif
                var stack = new Stack<(string s, string d)>();
                stack.Push((src, dst));
                while (stack.Count > 0 && !job.CancelRequested)
                {
                    var (s, d) = stack.Pop();
                    ops.Add(new Op { Kind = OpKind.MkDir, Src = s, Dst = d });
                    if (job.Kind == JobKind.Move) dirsToRemove.Add(s);
                    try
                    {
                        foreach (var e in Native.Enumerate(s))
                        {
                            string cs = Path.Combine(s, e.Name), cd = Path.Combine(d, e.Name);
                            if (e.IsDirectory)
                            {
                                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) { job.AddError(Lang.T("Lien ignoré : ", "Link skipped: ") + cs); continue; }
                                stack.Push((cs, cd));
                            }
                            else
                            {
                                ops.Add(new Op { Kind = OpKind.File, Src = cs, Dst = cd, Size = e.Size, Date = e.Modified });
                                job.TotalBytes += e.Size; job.TotalFiles++;
                            }
                        }
                    }
                    catch (Exception ex) { job.AddError(s + " : " + ex.Message); }
                }
            }

            if (job.CancelRequested) { job.State = JobState.Canceled; return; }
            job.State = JobState.Running;
            JobChanged?.Invoke(job);

            // ---- 2. Exécution
            long baseBytes = 0;
            Native.CopyProgressRoutine progress = (total, transferred, ss, sbt, sn, reason, hs, hd, data) =>
            {
                job.DoneBytes = baseBytes + transferred;
                if (_paused) _runGate.WaitOne();
                return job.CancelRequested ? 1u /*PROGRESS_CANCEL*/ : 0u;
            };

            foreach (var op in ops)
            {
                _runGate.WaitOne();
                if (job.CancelRequested) break;
                job.Current = op.Src;
                try
                {
                    switch (op.Kind)
                    {
                        case OpKind.MkDir:
                            if (!Native.CreateDirectory(Native.LongPath(op.Dst), IntPtr.Zero))
                            {
                                int err = Marshal.GetLastWin32Error();
                                if (err != Native.ERROR_ALREADY_EXISTS) throw new Win32Exception(err);
                            }
                            break;

                        case OpKind.QuickMove:
                            if (!Native.MoveFileEx(Native.LongPath(op.Src), Native.LongPath(op.Dst), 0))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                            job.DoneFiles++;
                            break;

                        case OpKind.File:
                            CopyOne(job, op, progress, ref baseBytes);
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

            // Déplacement inter-volumes : suppression des dossiers sources vidés
            for (int i = dirsToRemove.Count - 1; i >= 0; i--)
                Native.RemoveDirectory(Native.LongPath(dirsToRemove[i]));

            job.Current = "";
            job.DoneBytes = job.TotalBytes;
            job.State = job.Errors.Count == 0 ? JobState.Done : JobState.DoneWithErrors;
        }

        void CopyOne(TransferJob job, Op op, Native.CopyProgressRoutine progress, ref long baseBytes)
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
                        Native.SetFileAttributes(Native.LongPath(dst), Native.FILE_ATTRIBUTE_NORMAL);
                        break;
                }
            }

            string lsrc = Native.LongPath(op.Src), ldst = Native.LongPath(dst);
            if (job.Kind == JobKind.Move && IsSameVolume(op.Src, dst))
            {
                if (!Native.MoveFileEx(lsrc, ldst, replacing ? Native.MOVEFILE_REPLACE_EXISTING : 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            else
            {
                int cancel = 0;
                uint flags = op.Size > 256L * 1024 * 1024 ? Native.COPY_FILE_NO_BUFFERING : 0;
                if (!Native.CopyFileEx(lsrc, ldst, progress, IntPtr.Zero, ref cancel, flags))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Native.ERROR_REQUEST_ABORTED || job.CancelRequested) throw new OperationCanceledException();
                    throw new Win32Exception(err);
                }
                if (job.Kind == JobKind.Move)
                {
                    Native.SetFileAttributes(lsrc, Native.FILE_ATTRIBUTE_NORMAL);
                    if (!Native.DeleteFile(lsrc)) job.AddError(Lang.T("Copié mais non supprimé : ", "Copied but not deleted: ") + op.Src + " : " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
            }
            baseBytes += op.Size;
            job.DoneBytes = baseBytes;
            job.DoneFiles++;
        }
    }
}
