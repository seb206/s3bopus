using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace S3bopus
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Aucun plantage silencieux : toute erreur est affichée et écrite dans %TEMP%\S3bopus-erreur.txt
            AppDomain.CurrentDomain.AssemblyResolve += LoadEmbedded;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(e.Exception);
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = g.DpiX / 96f;
                Settings.Load();
                Archives.CleanTemp();
                Lang.En = Settings.GetBool("English", false);
                Ui.Dark = Settings.GetBool("Dark", false);
                Application.Run(new MainForm(args));
            }
            catch (Exception ex) { Report(ex); }
        }

        /// <summary>Charge les DLL intégrées dans l'exe (SharpCompress et ses dépendances), par nom simple.</summary>
        static readonly System.Collections.Generic.Dictionary<string, System.Reflection.Assembly> _loaded = new System.Collections.Generic.Dictionary<string, System.Reflection.Assembly>();

        static System.Reflection.Assembly LoadEmbedded(object sender, ResolveEventArgs e)
        {
            string name = new System.Reflection.AssemblyName(e.Name).Name;
            lock (_loaded)
            {
                // Une seule copie par DLL, sinon les mêmes types existeraient en double
                if (_loaded.TryGetValue(name, out var done)) return done;
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("deps." + name + ".dll"))
                {
                    if (s == null) return null;
                    var bytes = new byte[s.Length];
                    s.Read(bytes, 0, bytes.Length);
                    return _loaded[name] = System.Reflection.Assembly.Load(bytes);
                }
            }
        }

        internal static void Report(Exception ex)
        {
            if (ex == null) return;
            string file = Path.Combine(Path.GetTempPath(), "S3bopus-erreur.txt");
            try { File.AppendAllText(file, DateTime.Now + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine); } catch { }
            try
            {
                MessageBox.Show(ex.GetType().Name + " : " + ex.Message + "\n\nDétails enregistrés dans :\n" + file,
                    "S3bopus — erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
