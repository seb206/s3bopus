using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace S3bopus
{
    /// <summary>
    /// Barre d'adresse type Explorateur : fil d'Ariane cliquable (avec flèches listant les sous-dossiers),
    /// qui devient une zone de saisie au clic dans le vide ou avec Ctrl+L / Alt+D.
    /// </summary>
    internal sealed class AddressBar : Control
    {
        public const string ThisPC = "::PC";

        readonly ToolStrip _crumbs, _history;
        readonly TextBox _edit;
        string _path = "";
        bool _editing;

        public event Action<string> NavigateRequested;
        /// <summary>Chemin saisi (ou repris de l'historique) : à ajouter à l'historique s'il existe.</summary>
        public event Action<string> PathTyped;

        public AddressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Ui.Back;
            Padding = new Padding(1);
            Cursor = Cursors.IBeam;

            _crumbs = new ToolStrip
            {
                Dock = DockStyle.Fill,
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ExplorerRenderer(),
                CanOverflow = false,
                BackColor = Ui.Back,
                Padding = new Padding(2, 0, 0, 0),
                Font = Ui.Font,
                ImageScalingSize = new Size(Ui.S(16), Ui.S(16)),
                Cursor = Cursors.Default
            };
            _crumbs.MouseDown += (s, e) => { if (_crumbs.GetItemAt(e.Location) == null) BeginEdit(); };
            _crumbs.Resize += (s, e) => FitCrumbs();

            _edit = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = Ui.Font,
                BackColor = Ui.Back,
                ForeColor = Ui.Fore,
                Visible = false,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.FileSystemDirectories
            };
            _edit.KeyDown += Edit_KeyDown;
            _edit.LostFocus += (s, e) => { if (_editing) EndEdit(); };

            // Flèche à droite : historique des emplacements saisis
            _history = new ToolStrip
            {
                Dock = DockStyle.Right,
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ExplorerRenderer(),
                CanOverflow = false,
                AutoSize = false,
                Width = Ui.S(26),
                BackColor = Ui.Back,
                Padding = Padding.Empty,
                ImageScalingSize = new Size(Ui.S(12), Ui.S(12)),
                Cursor = Cursors.Default
            };
            var hist = new ToolStripDropDownButton(null, Ui.Glyph('', Ui.Ink, 12)) { ShowDropDownArrow = false, ToolTipText = Lang.T("Emplacements récents", "Recent locations") };
            hist.DropDown.Renderer = new ExplorerRenderer();
            hist.DropDownOpening += (s, e) => FillHistory(hist);
            hist.DropDownItems.Add("…");
            _history.Items.Add(hist);

            Controls.Add(_edit);
            Controls.Add(_crumbs);
            Controls.Add(_history);
        }

        void FillHistory(ToolStripDropDownButton b)
        {
            b.DropDownItems.Clear();
            var items = PathHistory.Items;
            foreach (var p in items)
            {
                string path = p;
                b.DropDownItems.Add(new ToolStripMenuItem(p, null, (s, e) => PathTyped?.Invoke(path)));
            }
            if (items.Count == 0) { b.DropDownItems.Add(new ToolStripMenuItem(Lang.T("(aucun emplacement saisi)", "(no typed locations)")) { Enabled = false }); return; }
            b.DropDownItems.Add(new ToolStripSeparator());
            b.DropDownItems.Add(Lang.T("Effacer l'historique", "Clear history"), null, (s, e) => PathHistory.Clear());
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); BeginEdit(); }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_edit == null || _history == null) return; // appelé pendant le constructeur
            int h = _edit.PreferredHeight;
            _edit.SetBounds(Ui.S(6), (Height - h) / 2, Width - _history.Width - Ui.S(10), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using (var p = new Pen(_editing ? Ui.Accent : Ui.Border))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        public string Path
        {
            get => _path;
            set { _path = value; if (!_editing) Rebuild(); }
        }

        public void BeginEdit()
        {
            if (_editing) return;
            _editing = true;
            _edit.Text = _path == ThisPC ? "" : _path;
            _crumbs.Visible = false;
            _edit.Visible = true;
            _edit.Focus();
            _edit.SelectAll();
            Invalidate();
        }

        void EndEdit()
        {
            _editing = false;
            _edit.Visible = false;
            _crumbs.Visible = true;
            Rebuild();
            Invalidate();
        }

        void Edit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                string text = Environment.ExpandEnvironmentVariables(_edit.Text.Trim().Trim('"'));
                EndEdit();
                if (text.Length > 0) PathTyped?.Invoke(text);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                EndEdit();
                NavigateRequested?.Invoke(null); // rend le focus à la liste
            }
        }

        // -------------------------------------------------------------- fil d'Ariane

        static List<string> Segments(string path)
        {
            var list = new List<string>();
            if (path == ThisPC || string.IsNullOrEmpty(path)) return list;
            string p = path;
            while (!string.IsNullOrEmpty(p))
            {
                list.Insert(0, p);
                string parent = System.IO.Path.GetDirectoryName(p);
                if (parent == null || parent == p) break;
                p = parent;
            }
            return list;
        }

        void Rebuild()
        {
            _crumbs.SuspendLayout();
            var old = _crumbs.Items.Cast<ToolStripItem>().ToArray();
            _crumbs.Items.Clear();
            foreach (var it in old) it.Dispose();

            var pc = new ToolStripSplitButton(Lang.T("Ce PC", "This PC")) { Tag = ThisPC, Image = Ui.Glyph('\uE7F4', Color.FromArgb(0, 99, 177)), DisplayStyle = ToolStripItemDisplayStyle.Image, ToolTipText = Lang.T("Ce PC", "This PC") };
            pc.ButtonClick += (s, e) => NavigateRequested?.Invoke(ThisPC);
            pc.DropDownOpening += (s, e) => FillRoots(pc);
            pc.DropDownItems.Add("…");
            _crumbs.Items.Add(pc);

            foreach (var seg in Segments(_path))
            {
                string name = System.IO.Path.GetFileName(seg);
                if (string.IsNullOrEmpty(name)) name = SysIcons.DisplayName(seg);
                var b = new ToolStripSplitButton(name) { Tag = seg, DisplayStyle = ToolStripItemDisplayStyle.Text };
                string target = seg;
                b.ButtonClick += (s, e) => NavigateRequested?.Invoke(target);
                b.DropDownOpening += (s, e) => FillChildren(b, target);
                b.DropDownItems.Add("…");
                _crumbs.Items.Add(b);
            }
            _crumbs.ResumeLayout();
            FitCrumbs();
        }

        /// <summary>Si le chemin est trop long, masque les premiers segments (comme l'Explorateur).</summary>
        void FitCrumbs()
        {
            if (_crumbs.Items.Count == 0) return;
            int avail = _crumbs.DisplayRectangle.Width - Ui.S(8);
            foreach (ToolStripItem it in _crumbs.Items) it.Visible = true;
            int total = _crumbs.Items.Cast<ToolStripItem>().Sum(i => i.GetPreferredSize(Size.Empty).Width + i.Margin.Horizontal);
            for (int i = 1; i < _crumbs.Items.Count - 1 && total > avail; i++)
            {
                var it = _crumbs.Items[i];
                total -= it.GetPreferredSize(Size.Empty).Width + it.Margin.Horizontal;
                it.Visible = false;
            }
        }

        void FillChildren(ToolStripSplitButton b, string folder)
        {
            b.DropDownItems.Clear();
            if (!Directory.Exists(folder)) { b.DropDownItems.Add(new ToolStripMenuItem(Lang.T("(archive)", "(archive)")) { Enabled = false }); return; }
            try
            {
                var dirs = Native.Enumerate(folder)
                    .Where(e => e.IsDirectory && (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .Select(e => e.Name).ToList();
                dirs.Sort(Native.StrCmpLogicalW);
                var img = SysIcons.Bitmap(SysIcons.FolderIcon());
                foreach (var d in dirs.Take(300))
                {
                    string full = System.IO.Path.Combine(folder, d);
                    var mi = new ToolStripMenuItem(d, img, (s, e) => NavigateRequested?.Invoke(full));
                    // Mettre en gras le dossier qui fait partie du chemin courant
                    if (_path.StartsWith(full, StringComparison.OrdinalIgnoreCase) && (_path.Length == full.Length || _path[full.Length] == '\\'))
                        mi.Font = new Font(Ui.Font, FontStyle.Bold);
                    b.DropDownItems.Add(mi);
                }
                if (dirs.Count == 0) b.DropDownItems.Add(new ToolStripMenuItem(Lang.T("(aucun sous-dossier)", "(no subfolders)")) { Enabled = false });
            }
            catch (Exception ex) { b.DropDownItems.Add(new ToolStripMenuItem(ex.Message) { Enabled = false }); }
        }

        void FillRoots(ToolStripDropDownItem b)
        {
            b.DropDownItems.Clear();
            foreach (var (name, path) in Places())
                b.DropDownItems.Add(new ToolStripMenuItem(name, SysIcons.Bitmap(SysIcons.IconForPath(path)), (s, e) => NavigateRequested?.Invoke(path)));
            b.DropDownItems.Add(new ToolStripSeparator());
            foreach (var d in DriveInfo.GetDrives())
            {
                string root = d.Name;
                b.DropDownItems.Add(new ToolStripMenuItem(SysIcons.DisplayName(root), SysIcons.Bitmap(SysIcons.IconForPath(root)), (s, e) => NavigateRequested?.Invoke(root)));
            }
        }

        public static IEnumerable<(string name, string path)> Places()
        {
            var list = new List<(string, string)>
            {
                (Lang.T("Bureau", "Desktop"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                (Lang.T("Téléchargements", "Downloads"), Native.KnownFolder(Native.FOLDERID_Downloads)),
                ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                (Lang.T("Images", "Pictures"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                (Lang.T("Musique", "Music"), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
                (Lang.T("Vidéos", "Videos"), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
                (Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            };
            return list.Where(p => !string.IsNullOrEmpty(p.Item2) && Directory.Exists(p.Item2));
        }
    }
}
