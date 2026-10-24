using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace S3bopus
{
    internal sealed class DirSize
    {
        public long Bytes;
        public long Files;
        public long Folders;
        public bool Complete;
        public int Errors;
    }

    /// <summary>Calcul (en arrière-plan) et cache partagé des tailles de dossiers.</summary>
    internal static class DirSizes
    {
        static readonly ConcurrentDictionary<string, DirSize> _cache = new ConcurrentDictionary<string, DirSize>(StringComparer.OrdinalIgnoreCase);

        public static DirSize Get(string path) => _cache.TryGetValue(Norm(path), out var d) ? d : null;

        static string Norm(string p) => p.TrimEnd('\\');

        /// <summary>Oublie la taille de ce chemin et de tous ses parents (après une copie, suppression...).</summary>
        public static void Invalidate(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string p = Norm(path);
            foreach (var key in _cache.Keys)
            {
                if (p.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase) || p.Equals(key, StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                    _cache.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Calcule la taille de chaque dossier de la liste, l'un après l'autre.
        /// progress(path, size) est appelé régulièrement depuis le thread de travail.
        /// </summary>
        public static void Compute(IList<string> folders, bool force, CancellationToken ct, Action<string, DirSize> progress)
        {
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
                        foreach (var e in Native.Enumerate(dir))
                        {
                            if (e.IsDirectory)
                            {
                                Interlocked.Increment(ref size.Folders);
                                // Ne pas suivre les jonctions / liens symboliques (évite les boucles et doubles comptes)
                                if ((e.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(Path.Combine(dir, e.Name));
                            }
                            else
                            {
                                Interlocked.Add(ref size.Bytes, e.Size);
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
}
