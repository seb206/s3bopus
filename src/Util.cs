using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace S3bopus
{
    internal static class Fmt
    {
        static readonly CultureInfo Fr = CultureInfo.CurrentCulture;

        public static string Size(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return bytes.ToString("N0", Fr) + (bytes > 1 ? Lang.T(" octets", " bytes") : Lang.T(" octet", " byte"));
            string[] units = { "Ko", "Mo", "Go", "To", "Po" };
            double v = bytes / 1024.0;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            string f = v < 10 ? "0.##" : v < 100 ? "0.#" : "N0";
            return v.ToString(f, Fr) + " " + units[u];
        }

        public static string Count(long n, string singular, string plural) =>
            n.ToString("N0", Fr) + " " + (n > 1 ? plural : singular);

        public static string Duration(TimeSpan t)
        {
            if (t.TotalHours >= 1) return ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
            return t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }

        public static string Date(DateTime d) => d == DateTime.MinValue ? "" : d.ToString("dd/MM/yyyy HH:mm", Fr);
    }

    /// <summary>Icônes et noms de type issus de la liste d'images système de Windows (mêmes icônes que l'Explorateur).</summary>
    internal static class SysIcons
    {
        static IntPtr _small;
        static readonly ConcurrentDictionary<string, int> _extIcon = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, string> _extType = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<int, Image> _bitmaps = new Dictionary<int, Image>();
        static readonly HashSet<string> _perFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".msc", ".scr", ".appref-ms", ".cpl" };

        public static IntPtr SmallImageList
        {
            get
            {
                if (_small == IntPtr.Zero)
                {
                    var sfi = new Native.SHFILEINFO();
                    _small = Native.SHGetFileInfo("C:\\", 0, ref sfi, (uint)Marshal.SizeOf(sfi), Native.SHGFI_SYSICONINDEX | Native.SHGFI_SMALLICON);
                }
                return _small;
            }
        }

        static int Query(string path, uint attr, bool useAttr)
        {
            var sfi = new Native.SHFILEINFO();
            uint flags = Native.SHGFI_SYSICONINDEX | Native.SHGFI_SMALLICON | (useAttr ? Native.SHGFI_USEFILEATTRIBUTES : 0);
            return Native.SHGetFileInfo(path, attr, ref sfi, (uint)Marshal.SizeOf(sfi), flags) == IntPtr.Zero ? 0 : sfi.iIcon;
        }

        public static int FolderIcon(string path = null) =>
            _extIcon.GetOrAdd("<dir>", _ => Query("dossier", Native.FILE_ATTRIBUTE_DIRECTORY, true));

        public static int IconFor(string path, bool isDir)
        {
            if (isDir) return FolderIcon();
            string ext = Path.GetExtension(path);
            if (_perFile.Contains(ext)) return Query(path, 0, false);
            return _extIcon.GetOrAdd(ext ?? "", e => Query("x" + e, Native.FILE_ATTRIBUTE_NORMAL, true));
        }

        public static int IconForPath(string path) => Query(path, 0, false);

        public static string TypeName(string name, bool isDir)
        {
            string key = isDir ? "<dir>" : (Path.GetExtension(name) ?? "");
            return _extType.GetOrAdd(key, k =>
            {
                var sfi = new Native.SHFILEINFO();
                Native.SHGetFileInfo(isDir ? "dossier" : "x" + k, isDir ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL,
                    ref sfi, (uint)Marshal.SizeOf(sfi), Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
                return string.IsNullOrEmpty(sfi.szTypeName) ? (isDir ? Lang.T("Dossier", "Folder") : Lang.T("Fichier ", "File ") + k.TrimStart('.').ToUpperInvariant()) : sfi.szTypeName;
            });
        }

        public static string DisplayName(string path)
        {
            var sfi = new Native.SHFILEINFO();
            Native.SHGetFileInfo(path, 0, ref sfi, (uint)Marshal.SizeOf(sfi), Native.SHGFI_DISPLAYNAME);
            return string.IsNullOrEmpty(sfi.szDisplayName) ? path : sfi.szDisplayName;
        }

        public static Image Bitmap(int index)
        {
            lock (_bitmaps)
            {
                if (_bitmaps.TryGetValue(index, out var img)) return img;
                img = null;
                IntPtr h = Native.ImageList_GetIcon(SmallImageList, index, 0);
                if (h != IntPtr.Zero)
                {
                    using (var ic = Icon.FromHandle(h)) img = ic.ToBitmap();
                    Native.DestroyIcon(h);
                }
                _bitmaps[index] = img;
                return img;
            }
        }
    }

    /// <summary>Aspect "Explorateur Windows" : couleurs, police, glyphes Segoe MDL2.</summary>
    internal static class Ui
    {
        public static float Scale = 1f;
        public static int S(int px) => (int)Math.Round(px * Scale);
        public static readonly Font Font = new Font("Segoe UI", 9f);
        public static bool Dark;
        public static Color Back => Dark ? Color.FromArgb(32, 32, 32) : Color.White;
        public static Color Fore => Dark ? Color.FromArgb(230, 230, 230) : Color.Black;
        public static Color Ink => Dark ? Color.FromArgb(210, 210, 210) : Color.FromArgb(64, 64, 64);
        public static Color Border => Dark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(217, 217, 217);
        public static Color Splitter => Dark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(235, 235, 235);
        public static Color Hover => Dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(229, 243, 255);
        public static Color HoverBorder => Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(204, 232, 255);
        public static Color Pressed => Dark ? Color.FromArgb(65, 65, 65) : Color.FromArgb(204, 232, 255);
        public static readonly Color Accent = Color.FromArgb(0, 120, 215);
        public static Color Line => Dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(229, 229, 229);
        public static Color TextGray => Dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(96, 96, 96);

        static string _glyphFont;
        static string GlyphFont
        {
            get
            {
                if (_glyphFont == null)
                {
                    _glyphFont = "";
                    using (var fonts = new InstalledFontCollection())
                        foreach (var f in fonts.Families)
                            if (f.Name == "Segoe Fluent Icons") { _glyphFont = f.Name; break; }
                            else if (f.Name == "Segoe MDL2 Assets" && _glyphFont == "") _glyphFont = f.Name;
                }
                return _glyphFont;
            }
        }

        /// <summary>Dessine un glyphe Segoe MDL2/Fluent en bitmap (icônes de barre d'outils légères, sans ressources).</summary>
        public static Image Glyph(char c, Color color, int size = 16)
        {
            int px = S(size);
            var bmp = new Bitmap(px, px);
            if (GlyphFont == "") return bmp;
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font(GlyphFont, px * 0.72f, GraphicsUnit.Pixel))
            using (var br = new SolidBrush(color))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(c.ToString(), font, br, new RectangleF(0, 0, px, px + 1), sf);
            }
            return bmp;
        }

        public static void ExplorerTheme(Control c)
        {
            try { Native.SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        public static void CueBanner(TextBox tb, string text)
        {
            tb.HandleCreated += (s, e) => Native.SendMessage(tb.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, text);
        }
    }

    /// <summary>Langue de l'interface : Lang.T("français", "english").</summary>
    internal static class Lang
    {
        public static bool En;
        public static string T(string fr, string en) => En ? en : fr;
    }

    internal sealed class ExplorerColors : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Ui.Back;
        public override Color ToolStripGradientMiddle => Ui.Back;
        public override Color ToolStripGradientEnd => Ui.Back;
        public override Color ToolStripBorder => Ui.Back;
        public override Color MenuStripGradientBegin => Ui.Back;
        public override Color MenuStripGradientEnd => Ui.Back;
        public override Color ButtonSelectedHighlight => Ui.Hover;
        public override Color ButtonSelectedGradientBegin => Ui.Hover;
        public override Color ButtonSelectedGradientMiddle => Ui.Hover;
        public override Color ButtonSelectedGradientEnd => Ui.Hover;
        public override Color ButtonSelectedBorder => Ui.HoverBorder;
        public override Color ButtonPressedGradientBegin => Ui.Pressed;
        public override Color ButtonPressedGradientMiddle => Ui.Pressed;
        public override Color ButtonPressedGradientEnd => Ui.Pressed;
        public override Color ButtonPressedBorder => Ui.HoverBorder;
        public override Color ButtonCheckedGradientBegin => Ui.Pressed;
        public override Color ButtonCheckedGradientMiddle => Ui.Pressed;
        public override Color ButtonCheckedGradientEnd => Ui.Pressed;
        public override Color CheckBackground => Ui.Pressed;
        public override Color CheckSelectedBackground => Ui.Pressed;
        public override Color CheckPressedBackground => Ui.Pressed;
        public override Color MenuItemSelected => Ui.Hover;
        public override Color MenuItemBorder => Ui.HoverBorder;
        public override Color MenuItemSelectedGradientBegin => Ui.Hover;
        public override Color MenuItemSelectedGradientEnd => Ui.Hover;
        public override Color MenuItemPressedGradientBegin => Ui.Back;
        public override Color MenuItemPressedGradientEnd => Ui.Back;
        public override Color MenuBorder => Ui.Border;
        public override Color ToolStripDropDownBackground => Ui.Back;
        public override Color ImageMarginGradientBegin => Ui.Back;
        public override Color ImageMarginGradientMiddle => Ui.Back;
        public override Color ImageMarginGradientEnd => Ui.Back;
        public override Color SeparatorDark => Ui.Line;
        public override Color SeparatorLight => Ui.Back;
        public override Color StatusStripGradientBegin => Ui.Back;
        public override Color StatusStripGradientEnd => Ui.Back;
        public override Color OverflowButtonGradientBegin => Ui.Back;
        public override Color OverflowButtonGradientMiddle => Ui.Back;
        public override Color OverflowButtonGradientEnd => Ui.Back;
    }

    internal sealed class ExplorerRenderer : ToolStripProfessionalRenderer
    {
        public ExplorerRenderer() : base(new ExplorerColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Ui.Fore : Color.FromArgb(128, 128, 128);
            base.OnRenderItemText(e);
        }
    }

    /// <summary>Réglages persistants dans %APPDATA%\S3bopus\S3bopus.ini.</summary>
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
                var lines = new List<string>();
                foreach (var kv in _v) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
        }

        public static string Get(string key, string def = null) => _v.TryGetValue(key, out var v) ? v : def;
        public static bool GetBool(string key, bool def) => _v.TryGetValue(key, out var v) ? v == "1" : def;
        public static int GetInt(string key, int def) => _v.TryGetValue(key, out var v) && int.TryParse(v, out int i) ? i : def;
        public static void Set(string key, object value) => _v[key] = value is bool b ? (b ? "1" : "0") : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
