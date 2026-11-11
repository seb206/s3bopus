using System;
using System.Collections.Generic;
using System.Linq;

namespace S3bopus
{
    /// <summary>
    /// Dossiers favoris : ceux que l'on ajoute à la main (réglage « Favorites ») et les « favoris automatiques »,
    /// c'est-à-dire les dossiers les plus souvent ouverts (réglage « Visits » : chemin + nombre d'ouvertures).
    /// </summary>
    internal static class Favorites
    {
        const char Sep = '\u001f', Kv = '\u001e'; // séparateurs invisibles : impossibles dans un chemin
        const int MaxTracked = 200;
        static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

        // ---- manuels
        public static List<string> Manual => (Settings.Get("Favorites") ?? "").Split(Sep).Where(s => s.Length > 0).ToList();

        public static bool IsManual(string path) => Manual.Contains(path, Cmp);

        /// <summary>Ajoute le dossier aux favoris, ou le retire s'il y est déjà.</summary>
        public static void Toggle(string path)
        {
            var list = Manual;
            if (list.RemoveAll(p => Cmp.Equals(p, path)) == 0) list.Add(path);
            Settings.Set("Favorites", string.Join(Sep.ToString(), list));
        }

        // ---- automatiques
        static Dictionary<string, int> Visits()
        {
            var d = new Dictionary<string, int>(Cmp);
            foreach (var item in (Settings.Get("Visits") ?? "").Split(Sep))
            {
                int i = item.LastIndexOf(Kv);
                if (i > 0 && int.TryParse(item.Substring(i + 1), out int n) && n > 0) d[item.Substring(0, i)] = n;
            }
            return d;
        }

        static void Save(Dictionary<string, int> d) =>
            Settings.Set("Visits", string.Join(Sep.ToString(), d.Select(kv => kv.Key + Kv + kv.Value)));

        /// <summary>Compte une ouverture du dossier.</summary>
        public static void Visit(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var d = Visits();
            d.TryGetValue(path, out int n);
            d[path] = n + 1;
            // shortcut: pas de vieillissement, un dossier très ouvert autrefois reste en tête ; « Effacer » remet à zéro
            if (d.Count > MaxTracked)
                d = d.OrderByDescending(kv => kv.Value).Take(MaxTracked / 2).ToDictionary(kv => kv.Key, kv => kv.Value, Cmp);
            if (!d.ContainsKey(path)) d[path] = 1; // le dossier qu'on vient d'ouvrir n'est jamais celui qu'on oublie
            Save(d);
        }

        /// <summary>Les n dossiers les plus ouverts, avec leur nombre d'ouvertures.</summary>
        public static List<KeyValuePair<string, int>> Top(int n) =>
            Visits().OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, Cmp).Take(n).ToList();

        public static void ClearSmart() => Settings.Set("Visits", "");
    }
}
