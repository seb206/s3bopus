using System;
using System.IO;
using System.Net;

namespace S3bopus
{
    /// <summary>
    /// Vérification des mises à jour : le site publie version.txt (lignes « windows=1.0.2 », « linux=… », « mac=… »).
    /// S3bopus ne télécharge et n'installe rien : il propose seulement d'ouvrir la page du site.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string Page = "https://s3btify.com/s3bopus/";
        const string Url = Page + "version.txt";

        public static string Current => typeof(UpdateCheck).Assembly.GetName().Version.ToString(3);

        /// <summary>Valeur de la ligne « key=a.b.c » ; null si elle manque ou n'est pas un numéro de version.</summary>
        internal static string Parse(string text, string key)
        {
            foreach (var line in (text ?? "").Split('\n'))
            {
                string l = line.Trim();
                if (l.StartsWith(key + "=", StringComparison.Ordinal) && Version.TryParse(l.Substring(key.Length + 1), out var v) && v.Build >= 0 && v.Revision < 0)
                    return v.ToString(3);
            }
            return null;
        }

        public static bool IsNewer(string latest, string current) =>
            Version.TryParse(latest, out var a) && Version.TryParse(current, out var b) && a > b;

        /// <summary>Version Windows publiée sur le site, ou null (hors ligne, site injoignable). Bloquant : à appeler hors du fil de l'interface.</summary>
        public static string Latest()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(Url);
                req.Timeout = 5000; req.ReadWriteTimeout = 5000; req.UserAgent = "S3bopus";
                using (var resp = req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream()))
                {
                    var buf = new char[2000]; // le fichier fait quelques lignes : on ne lit jamais plus
                    int n = rd.ReadBlock(buf, 0, buf.Length);
                    return Parse(new string(buf, 0, n), "windows");
                }
            }
            catch { return null; }
        }
    }
}
