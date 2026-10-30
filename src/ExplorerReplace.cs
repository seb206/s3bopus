using Microsoft.Win32;
using System.Windows.Forms;

namespace S3bopus
{
    /// <summary>Remplace l'Explorateur pour l'ouverture des dossiers et lecteurs (clés HKCU, sans droits admin, réversible).</summary>
    static class ExplorerReplace
    {
        static readonly string[] Roots = { @"Software\Classes\Directory\shell\open", @"Software\Classes\Drive\shell\open" };

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Roots[0] + @"\command"))
                    return (k?.GetValue(null) as string ?? "").Contains("S3bopus");
            }
        }

        public static void Set(bool on)
        {
            foreach (var root in Roots)
            {
                if (on)
                    using (var k = Registry.CurrentUser.CreateSubKey(root + @"\command"))
                        k.SetValue(null, "\"" + Application.ExecutablePath + "\" \"%1\"");
                else
                    Registry.CurrentUser.DeleteSubKeyTree(root, false);
            }
        }
    }
}
