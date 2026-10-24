using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace S3bopus
{
    /// <summary>Un côté de la fenêtre : une barre d'onglets et un FilePane par onglet (comme Directory Opus).</summary>
    internal sealed class PaneTabs : UserControl
    {
        readonly ToolStrip _strip;
        readonly Panel _host;
        readonly ToolStripButton _add;
        readonly List<FilePane> _panes = new List<FilePane>();
        readonly Func<FilePane> _factory;

        public FilePane Current { get; private set; }
        public IReadOnlyList<FilePane> Panes => _panes;
        public event Action<FilePane> CurrentChanged;

        public PaneTabs(Func<FilePane> factory)
        {
            _factory = factory;
            BackColor = Ui.Back;
            _strip = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ExplorerRenderer(),
                BackColor = Ui.Back,
                Padding = new Padding(Ui.S(2), Ui.S(2), Ui.S(2), 0),
                ImageScalingSize = new Size(Ui.S(12), Ui.S(12)),
                ShowItemToolTips = true
            };
            _add = new ToolStripButton(null, Ui.Glyph('', Ui.Ink, 12), (s, e) => NewTab(Current?.CurrentPath).FocusList())
                { ToolTipText = Lang.T("Nouvel onglet (Ctrl+N)", "New tab (Ctrl+N)"), DisplayStyle = ToolStripItemDisplayStyle.Image };
            _strip.Items.Add(_add);
            _host = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Back };
            Controls.Add(_host);
            Controls.Add(_strip);
        }

        public FilePane NewTab(string path)
        {
            var p = _factory();
            p.Dock = DockStyle.Fill;
            p.Visible = false;
            p.PathChanged += _ => UpdateStrip();
            _panes.Insert(Current == null ? _panes.Count : _panes.IndexOf(Current) + 1, p);
            _host.Controls.Add(p);
            if (path != null) p.Navigate(path);
            Select(p);
            return p;
        }

        public void Select(FilePane p)
        {
            if (p == null) return;
            foreach (var x in _panes) x.Visible = x == p;
            bool changed = Current != p;
            Current = p;
            UpdateStrip();
            if (changed) CurrentChanged?.Invoke(p);
        }

        public void CloseTab(FilePane p)
        {
            if (_panes.Count <= 1 || p == null) return;
            int i = _panes.IndexOf(p);
            _panes.Remove(p);
            _host.Controls.Remove(p);
            if (Current == p) { Current = null; Select(_panes[Math.Min(i, _panes.Count - 1)]); }
            else UpdateStrip();
            p.Dispose();
        }

        public void Cycle(int delta)
        {
            if (_panes.Count < 2) return;
            Select(_panes[(_panes.IndexOf(Current) + delta + _panes.Count) % _panes.Count]);
            Current.FocusList();
        }

        public static string Title(FilePane p)
        {
            if (p.CurrentPath == null) return "…";
            if (p.IsThisPC) return Lang.T("Ce PC", "This PC");
            string name = Path.GetFileName(p.CurrentPath);
            return string.IsNullOrEmpty(name) ? p.CurrentPath : name;
        }

        void UpdateStrip()
        {
            _strip.SuspendLayout();
            foreach (var it in _strip.Items.Cast<ToolStripItem>().Where(i => i != _add).ToList()) { _strip.Items.Remove(it); it.Dispose(); }
            int index = 0;
            foreach (var p in _panes)
            {
                var pane = p;
                var b = new ToolStripButton(Title(p))
                {
                    Checked = p == Current,
                    ToolTipText = p.CurrentPath == AddressBar.ThisPC ? Title(p) : p.CurrentPath,
                    Padding = new Padding(Ui.S(6), 0, Ui.S(6), 0),
                    AutoToolTip = false
                };
                if (b.Text.Length > 28) b.Text = b.Text.Substring(0, 27) + "…";
                b.MouseUp += (s, e) =>
                {
                    if (e.Button == MouseButtons.Middle) CloseTab(pane);
                    else if (e.Button == MouseButtons.Right) TabMenu(pane).Show(Cursor.Position);
                    else { Select(pane); pane.FocusList(); }
                };
                _strip.Items.Insert(index++, b);
            }
            _strip.ResumeLayout();
        }

        ContextMenuStrip TabMenu(FilePane p)
        {
            var m = new ContextMenuStrip { Renderer = new ExplorerRenderer() };
            m.Items.Add(Lang.T("Nouvel onglet", "New tab"), null, (s, e) => NewTab(p.CurrentPath).FocusList());
            m.Items.Add(new ToolStripMenuItem(Lang.T("Fermer l'onglet", "Close tab"), null, (s, e) => CloseTab(p)) { Enabled = _panes.Count > 1, ShortcutKeyDisplayString = "Ctrl+W" });
            m.Items.Add(new ToolStripMenuItem(Lang.T("Fermer les autres onglets", "Close other tabs"), null, (s, e) => { foreach (var x in _panes.Where(x => x != p).ToList()) CloseTab(x); }) { Enabled = _panes.Count > 1 });
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }
    }
}
