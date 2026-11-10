#if !LIGHT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace S3bopus
{
    /// <summary>
    /// Lien avec S3btify (0.15.31+), version Windows complète seulement : lit le morceau en cours et les suivants, envoie précédent / lecture-pause / suivant.
    /// Local uniquement (127.0.0.1:34565). Interface : GET /api/player (JSON) et POST /api/player/{prev|playpause|next}.
    /// </summary>
    internal static class S3btifyLink
    {
        public static readonly System.Version MinVersion = new System.Version(0, 15, 31);
        public static int Port => Settings.GetInt("S3btifyPort", 34565);

        public static volatile bool Available;   // S3btify répond et sa version est >= MinVersion
        public static volatile bool Playing;
        public static string AppVersion = "", Title = "", Artist = "", Album = "", NextText = "";
        public static event Action Changed;

        static System.Threading.Timer _timer;

        public static void Start()
        {
            if (_timer == null) _timer = new System.Threading.Timer(_ => Poll(), null, 0, 2000);
        }

        static string Url(string path) => "http://127.0.0.1:" + Port + path;

        static string Field(string json, string name)
        {
            var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        }

        /// <summary>Morceaux suivants ("next":[{title,artist},...]) sous la forme « artiste – titre · artiste – titre ».</summary>
        static string NextTracks(string json)
        {
            int i = json.IndexOf("\"next\"", StringComparison.Ordinal);
            if (i < 0 || (i = json.IndexOf('[', i)) < 0) return "";
            int depth = 0, end = -1; bool str = false;
            for (int k = i; k < json.Length; k++)
            {
                char c = json[k];
                if (str) { if (c == '\\') k++; else if (c == '"') str = false; continue; }
                if (c == '"') str = true;
                else if (c == '[') depth++;
                else if (c == ']' && --depth == 0) { end = k; break; }
            }
            if (end < 0) return "";
            var items = new List<string>();
            foreach (Match o in Regex.Matches(json.Substring(i, end - i + 1), "\\{[^{}]*\\}"))
            {
                string t = Field(o.Value, "title"), a = Field(o.Value, "artist");
                if (t.Length > 0) items.Add(a.Length > 0 ? a + " – " + t : t);
                if (items.Count == 5) break;
            }
            return string.Join("  ·  ", items);
        }

        static void Poll()
        {
            bool avail = false, playing = false; string ver = "", title = "", artist = "", album = "", next = "";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Url("/api/player"));
                req.Timeout = 700; req.ReadWriteTimeout = 700; req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream()))
                {
                    string json = rd.ReadToEnd();
                    if (json.Length < 20000 && Field(json, "app") == "S3btify" && System.Version.TryParse(Field(json, "version"), out var v) && v >= MinVersion)
                    {
                        avail = true; ver = v.ToString();
                        playing = Regex.IsMatch(json, "\"playing\"\\s*:\\s*true");
                        title = Field(json, "title"); artist = Field(json, "artist"); album = Field(json, "album");
                        next = NextTracks(json);
                    }
                }
            }
            catch { }
            bool changed = avail != Available || playing != Playing || ver != AppVersion || title != Title || artist != Artist || album != Album || next != NextText;
            Available = avail; Playing = playing; AppVersion = ver; Title = title; Artist = artist; Album = album; NextText = next;
            if (changed) Changed?.Invoke();
        }

        /// <summary>command : "prev", "playpause" ou "next".</summary>
        public static void Send(string command)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(Url("/api/player/" + command));
                    req.Method = "POST"; req.ContentLength = 0; req.Timeout = 1500; req.Proxy = null;
                    using (req.GetResponse()) { }
                }
                catch { }
                Poll();
            });
        }
    }

    /// <summary>Bandeau de la barre de commandes : pastille en ligne / hors ligne, puis le morceau en cours et les suivants qui défilent.</summary>
    internal sealed class MarqueeBox : Control
    {
        string _text = "";
        bool _online;
        float _off;
        int _textW;   // largeur du texte, mesurée une fois par changement (pas à chaque image)
        Bitmap _bmp;  // tampon du texte, réutilisé d'une image à l'autre
        readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 40 };

        public MarqueeBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Ui.Font; BackColor = Ui.Back; ForeColor = Ui.Fore;
            Size = new Size(Ui.S(360), Ui.S(24));
            _tick.Tick += (s, e) => { if (_textW > TextArea.Width) { _off += Ui.S(1); Invalidate(); } };
            _tick.Start();
        }

        Rectangle TextArea => new Rectangle(Ui.S(22), 0, Math.Max(1, Width - Ui.S(26)), Height);
        int TextWidth() => TextRenderer.MeasureText(_text, Font, new Size(int.MaxValue, Height), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

        public void SetState(bool online, string text)
        {
            if (online == _online && text == _text) return;
            _online = online; _text = text; _off = 0; _textW = TextWidth(); Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var area = TextArea;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            int w = _textW;
            Color c = _online ? Ui.Fore : Ui.TextGray;
            // Le texte est dessiné dans un bitmap de la taille de la zone : il ne peut pas déborder à gauche vers la pastille
            if (_bmp == null || _bmp.Width != area.Width || _bmp.Height != Height) { _bmp?.Dispose(); _bmp = new Bitmap(area.Width, Math.Max(1, Height)); }
            var bmp = _bmp;
            using (var gb = Graphics.FromImage(bmp))
            {
                gb.Clear(BackColor);
                if (w <= area.Width) TextRenderer.DrawText(gb, _text, Font, new Rectangle(0, 0, area.Width, Height), c, flags);
                else
                {
                    int gap = Ui.S(60), cycle = w + gap;
                    if (_off >= cycle) _off -= cycle;
                    int x = -(int)_off;
                    TextRenderer.DrawText(gb, _text, Font, new Rectangle(x, 0, w + 4, Height), c, flags);
                    TextRenderer.DrawText(gb, _text, Font, new Rectangle(x + cycle, 0, w + 4, Height), c, flags);
                }
                g.DrawImageUnscaled(bmp, area.Left, 0);
            }
            // La pastille est dessinée en dernier sur un fond uni : le texte (TextRenderer ignore le clip) ne peut pas la recouvrir
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, 0, 0, area.Left, Height);
            int d = Ui.S(9);
            using (var br = new SolidBrush(_online ? Color.FromArgb(16, 160, 70) : Color.FromArgb(200, 60, 50))) g.FillEllipse(br, Ui.S(6), (Height - d) / 2, d, d);
        }

        protected override void Dispose(bool disposing) { if (disposing) { _tick.Dispose(); _bmp?.Dispose(); } base.Dispose(disposing); }
    }
}
#endif
