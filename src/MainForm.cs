using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace S3bopus
{
    internal sealed class MainForm : Form
    {
        readonly PaneTabs _leftTabs, _rightTabs;
        readonly SplitContainer _split, _outer;
        readonly QueuePanel _queuePanel;
        readonly TransferQueue _queue = new TransferQueue();
        readonly ToolStrip _commands, _fkeys;
        readonly ToolStripMenuItem _miHidden, _miAutoSizes, _miFKeys, _miQueue;
        FilePane _active;
        double _ratio = 0.5;

        public MainForm(string[] args)
        {
            Text = "S3bopus";
            Font = Ui.Font;
            BackColor = Ui.Back;
            ForeColor = Ui.Fore;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = false;
            try { Icon = new Icon(typeof(MainForm).Assembly.GetManifestResourceStream("S3bopus.ico")); } catch { }
            MinimumSize = new Size(Ui.S(640), Ui.S(400));

            FilePane.ShowHidden = Settings.GetBool("ShowHidden", false);
            FilePane.AutoSizes = Settings.GetBool("AutoSizes", false);

            // ---- Barre de commandes (style Explorateur Windows 11)
            _commands = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ExplorerRenderer(),
                BackColor = Ui.Back,
                ImageScalingSize = new Size(Ui.S(16), Ui.S(16)),
                Padding = new Padding(Ui.S(8), Ui.S(4), Ui.S(8), Ui.S(4)),
                Font = Ui.Font
            };
            Color ink = Ui.Ink;
            ToolStripButton Cmd(string text, char glyph, Color c, string tip, Action a, bool showText = true) =>
                new ToolStripButton(text, Ui.Glyph(glyph, c), (s, e) => a())
                {
                    ToolTipText = tip,
                    DisplayStyle = showText ? ToolStripItemDisplayStyle.ImageAndText : ToolStripItemDisplayStyle.Image,
                    Padding = new Padding(Ui.S(4), Ui.S(2), Ui.S(4), Ui.S(2))
                };

            _commands.Items.AddRange(new ToolStripItem[]
            {
                Cmd(Lang.T("Nouveau dossier", "New folder"), '\uE8F4', Color.FromArgb(220, 160, 20), Lang.T("Nouveau dossier (F7 / Ctrl+Maj+N)", "New folder (F7 / Ctrl+Shift+N)"), () => _active.NewFolder()),
                new ToolStripSeparator(),
                Cmd(null, '\uE8C6', ink, Lang.T("Couper (Ctrl+X)", "Cut (Ctrl+X)"), () => _active.CopyToClipboard(true), false),
                Cmd(null, '\uE8C8', ink, Lang.T("Copier (Ctrl+C)", "Copy (Ctrl+C)"), () => _active.CopyToClipboard(false), false),
                Cmd(null, '\uE77F', ink, Lang.T("Coller (Ctrl+V) — via la file de transferts", "Paste (Ctrl+V) — via the transfer queue"), () => _active.Paste(), false),
                Cmd(null, '\uE8AC', ink, Lang.T("Renommer (F2)", "Rename (F2)"), () => _active.BeginRename(), false),
                Cmd(null, '\uE74D', ink, Lang.T("Supprimer (Suppr / F8) — Maj pour supprimer définitivement", "Delete (Del / F8) — Shift to delete permanently"), () => _active.DeleteSelected((ModifierKeys & Keys.Shift) != 0), false),
                Cmd(null, '\uE946', ink, Lang.T("Propriétés (Alt+Entrée)", "Properties (Alt+Enter)"), () => _active.ShowProperties(), false),
                new ToolStripSeparator(),
                Cmd(Lang.T("Copier vers", "Copy to"), '\uE8C8', Color.FromArgb(0, 120, 215), Lang.T("Copier la sélection vers l'autre panneau (F5)", "Copy the selection to the other pane (F5)"), () => TransferToOther(JobKind.Copy)),
                Cmd(Lang.T("Déplacer vers", "Move to"), '\uE8DE', Color.FromArgb(0, 120, 215), Lang.T("Déplacer la sélection vers l'autre panneau (F6)", "Move the selection to the other pane (F6)"), () => TransferToOther(JobKind.Move)),
                new ToolStripSeparator(),
                Cmd(Lang.T("Tailles", "Sizes"), '\uEB05', Color.FromArgb(16, 137, 62), Lang.T("Calculer la taille des dossiers sélectionnés, ou de tous (F3)", "Compute the size of selected folders, or all (F3)"), () => _active.ComputeSizes(true)),
                new ToolStripSeparator(),
                Cmd(null, '\uE8AB', ink, Lang.T("Échanger les panneaux (Ctrl+U)", "Swap panes (Ctrl+U)"), SwapPanes, false),
                Cmd(null, '\uE895', ink, Lang.T("Même dossier dans l'autre panneau (Ctrl+=)", "Same folder in the other pane (Ctrl+=)"), SyncOther, false),
                Cmd(null, '\uE756', ink, Lang.T("Ouvrir un terminal ici", "Open a terminal here"), OpenTerminal, false),
                Cmd(null, '\uEC50', ink, Lang.T("Ouvrir ce dossier dans l'Explorateur Windows", "Open this folder in Windows Explorer"), OpenInExplorer, false),
            });

            var more = new ToolStripDropDownButton(null, Ui.Glyph('\uE712', ink)) { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Options", ShowDropDownArrow = false };
            _miQueue = new ToolStripMenuItem(Lang.T("File de transferts", "Transfer queue"), null, (s, e) => ShowQueue(_outer.Panel2Collapsed)) { ShortcutKeyDisplayString = "Ctrl+T" };
            _miHidden = new ToolStripMenuItem(Lang.T("Afficher les éléments masqués", "Show hidden items"), null, (s, e) => ToggleHidden()) { Checked = FilePane.ShowHidden, ShortcutKeyDisplayString = "Ctrl+H" };
            _miAutoSizes = new ToolStripMenuItem(Lang.T("Calculer automatiquement la taille des dossiers", "Compute folder sizes automatically"), null, (s, e) => ToggleAutoSizes()) { Checked = FilePane.AutoSizes };
            _miFKeys = new ToolStripMenuItem(Lang.T("Barre des touches de fonction", "Function key bar"), null, (s, e) => { _fkeys.Visible = !_fkeys.Visible; _miFKeys.Checked = _fkeys.Visible; }) { Checked = Settings.GetBool("FKeys", true) };
            more.DropDownItems.AddRange(new ToolStripItem[]
            {
                _miQueue, _miHidden, _miAutoSizes, _miFKeys, new ToolStripSeparator(),
                new ToolStripMenuItem(Lang.T("Sélectionner tout", "Select all"), null, (s, e) => _active.SelectAll()) { ShortcutKeyDisplayString = "Ctrl+A" },
                new ToolStripMenuItem(Lang.T("Inverser la sélection", "Invert selection"), null, (s, e) => _active.InvertSelection()),
                new ToolStripMenuItem(Lang.T("Actualiser", "Refresh"), null, (s, e) => _active.Reload(true)) { ShortcutKeyDisplayString = "Ctrl+R" },
                new ToolStripSeparator(),
                new ToolStripMenuItem(Lang.T("À propos de S3bopus", "About S3bopus"), null, (s, e) => About())
            });
            more.DropDown.Renderer = new ExplorerRenderer();
            _commands.Items.Add(more);
            // Langue et thème : enregistrés puis appliqués en relançant l'application
            _commands.Items.Add(new ToolStripButton(Lang.En ? "FR" : "EN", null, (s, e) => Relaunch("English", !Lang.En))
                { Alignment = ToolStripItemAlignment.Right, ToolTipText = Lang.T("Switch to English", "Passer en français") });
            _commands.Items.Add(new ToolStripButton(null, Ui.Glyph(Ui.Dark ? '' : '', ink), (s, e) => Relaunch("Dark", !Ui.Dark))
                { Alignment = ToolStripItemAlignment.Right, ToolTipText = Ui.Dark ? Lang.T("Mode clair", "Light mode") : Lang.T("Mode sombre", "Dark mode") });

            // ---- Panneaux (un jeu d'onglets par côté)
            FilePane MakePane(string side)
            {
                var p = new FilePane { Queue = _queue };
                p.OtherPane = () => side == "Left" ? _right : _left;
                p.Activated += x => SetActive(x);
                // Les nouveaux onglets reprennent les colonnes et le tri de l'onglet courant
                var model = (side == "Left" ? _leftTabs : _rightTabs)?.Current;
                p.ColumnWidths = model?.ColumnWidths ?? Settings.Get(side + "Columns");
                p.SetSort(model?.SortColumn ?? Settings.GetInt(side + "Sort", 0), model?.SortAscending ?? Settings.GetBool(side + "Asc", true));
                return p;
            }
            _leftTabs = new PaneTabs(() => MakePane("Left")) { Dock = DockStyle.Fill };
            _rightTabs = new PaneTabs(() => MakePane("Right")) { Dock = DockStyle.Fill };
            _leftTabs.CurrentChanged += p => SetActive(p);
            _rightTabs.CurrentChanged += p => SetActive(p);

            _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = Ui.S(5), BackColor = Ui.Splitter, TabStop = false };
            _split.Panel1.BackColor = _split.Panel2.BackColor = Ui.Back;
            _split.Panel1.Controls.Add(_leftTabs);
            _split.Panel2.Controls.Add(_rightTabs);
            _split.SplitterMoved += (s, e) => { if (_split.Width > 0) _ratio = (double)_split.SplitterDistance / _split.Width; };

            _queuePanel = new QueuePanel(_queue) { Dock = DockStyle.Fill };
            _queuePanel.CloseRequested += () => ShowQueue(false);
            _queuePanel.OpenFolderRequested += p => { _active.Navigate(p); _active.FocusList(); };

            _outer = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = Ui.S(5), BackColor = Ui.Splitter, FixedPanel = FixedPanel.Panel2, TabStop = false };
            _outer.Panel1.Controls.Add(_split);
            _outer.Panel2.Controls.Add(_queuePanel);
            _outer.Panel2Collapsed = true;

            // ---- Barre des touches de fonction
            _fkeys = new ToolStrip
            {
                Dock = DockStyle.Bottom,
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ExplorerRenderer(),
                BackColor = Ui.Back,
                Padding = new Padding(Ui.S(4), Ui.S(1), Ui.S(4), Ui.S(1)),
                Visible = _miFKeys.Checked
            };
            void FK(string key, string label, Action a)
            {
                _fkeys.Items.Add(new ToolStripButton(key + "  " + label, null, (s, e) => a()) { DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(Ui.S(6), 0, Ui.S(6), 0) });
            }
            FK("F2", Lang.T("Renommer", "Rename"), () => _active.BeginRename());
            FK("F3", Lang.T("Tailles", "Sizes"), () => _active.ComputeSizes(true));
            FK("F5", Lang.T("Copier →", "Copy →"), () => TransferToOther(JobKind.Copy));
            FK("F6", Lang.T("Déplacer →", "Move →"), () => TransferToOther(JobKind.Move));
            FK("F7", Lang.T("Nouveau dossier", "New folder"), () => _active.NewFolder());
            FK("F8", Lang.T("Supprimer", "Delete"), () => _active.DeleteSelected(false));
            FK("Tab", Lang.T("Autre panneau", "Other pane"), () => SetActive(Other).FocusList());
            FK("Ctrl+T", Lang.T("File de transferts", "Transfer queue"), () => ShowQueue(_outer.Panel2Collapsed));

            var topLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Ui.Line };
            Controls.Add(_outer);
            Controls.Add(topLine);
            Controls.Add(_commands);
            Controls.Add(_fkeys);

            // ---- File de transferts : événements
            _queue.ResolveConflict = (job, info) => (ConflictChoice)Invoke(new Func<ConflictChoice>(() =>
            {
                ShowQueue(true);
                return ConflictForm.Ask(this, job, info);
            }));
            _queue.JobChanged += job =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() => OnJobChanged(job)));
            };

            var titleTimer = new Timer { Interval = 1000, Enabled = true };
            titleTimer.Tick += (s, e) =>
            {
                int pct = _queuePanel.OverallPercent();
                string t = pct >= 0 ? "S3bopus — " + pct + " %" : "S3bopus";
                if (Text != t) Text = t;
            };

            // ---- Position / chemins de départ
            RestoreWindow();
            string leftPath = args.Length > 0 ? args[0] : Settings.Get("LeftPath", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            string rightPath = args.Length > 1 ? args[1] : Settings.Get("RightPath", Native.KnownFolder(Native.FOLDERID_Downloads) ?? AddressBar.ThisPC);
            Load += (s, e) =>
            {
                _ratio = Settings.GetInt("Split", 500) / 1000.0;
                ApplyRatio();
                _queueHeight = Settings.GetInt("QueueHeight", Ui.S(200));
                RestoreTabs(_leftTabs, "Left", leftPath, args.Length > 0);
                RestoreTabs(_rightTabs, "Right", rightPath, args.Length > 1);
                SetActive(_left);
                _left.FocusList();
            };
            _split.Resize += (s, e) => ApplyRatio();
        }

        void Relaunch(string key, bool value)
        {
            Settings.Set(key, value);
            Close(); // enregistre les réglages ; peut être annulé si des transferts sont en cours
            if (IsDisposed) Process.Start(Application.ExecutablePath); // sans arguments : on reprend les dossiers enregistrés
            else Settings.Set(key, !value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = Ui.Dark ? 1 : 0;
            try { Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { } // barre de titre sombre (Windows 10 20H1+)
        }

        static string SafePath(string path) =>
            path == AddressBar.ThisPC || Directory.Exists(path) || Archives.Split(path, out _, out _) ? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>Rouvre les onglets enregistrés ; un dossier passé en ligne de commande remplace l'onglet courant.</summary>
        static void RestoreTabs(PaneTabs tabs, string side, string path, bool fromArgs)
        {
            var saved = (Settings.Get(side + "Tabs") ?? "").Split('\u001f').Where(p => p.Length > 0).ToList();
            int current = Settings.GetInt(side + "Tab", 0);
            if (saved.Count == 0) { saved.Add(path); current = 0; }
            current = Math.Max(0, Math.Min(current, saved.Count - 1));
            if (fromArgs) saved[current] = path;
            foreach (var p in saved) tabs.NewTab(SafePath(p));
            tabs.Select(tabs.Panes[current]);
        }

        FilePane _left => _leftTabs.Current;
        FilePane _right => _rightTabs.Current;
        IEnumerable<FilePane> AllPanes => _leftTabs.Panes.Concat(_rightTabs.Panes);
        PaneTabs ActiveTabs => _leftTabs.Panes.Contains(_active) ? _leftTabs : _rightTabs;

        void ApplyRatio()
        {
            int d = (int)(_split.Width * _ratio);
            if (d > _split.Panel1MinSize && d < _split.Width - _split.Panel2MinSize - _split.SplitterWidth)
                try { _split.SplitterDistance = d; } catch { }
        }

        FilePane Other => ActiveTabs == _leftTabs ? _right : _left;

        FilePane SetActive(FilePane pane)
        {
            _active = pane;
            foreach (var p in AllPanes) p.IsActive = p == pane;
            return pane;
        }

        // ======================================================================= commandes

        void TransferToOther(JobKind kind)
        {
            var src = _active;
            var dst = Other;
            // Archives en lecture seule : on peut copier depuis, pas déplacer ni écrire dedans
            if (src.SelectedEntries().Count == 0 || src.IsThisPC || dst.IsThisPC || dst.IsArchive || dst.CurrentPath == null || (src.IsArchive && kind == JobKind.Move))
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            var paths = src.MaterializeSelection();
            if (paths.Count == 0)
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            if (string.Equals(src.CurrentPath, dst.CurrentPath, StringComparison.OrdinalIgnoreCase) && kind == JobKind.Move)
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            _queue.Enqueue(kind, paths, dst.CurrentPath);
        }

        void OnJobChanged(TransferJob job)
        {
            if (job.State == JobState.Waiting && _outer.Panel2Collapsed) ShowQueue(true);
            _queuePanel.RefreshNow();
            if (job.IsFinished || job.State == JobState.Running)
            {
                DirSizes.Invalidate(job.DestDir);
                foreach (var s in job.Sources) DirSizes.Invalidate(s);
                foreach (var p in AllPanes) p.RefreshSizesDisplay();
            }
            if (job.IsFinished)
            {
                foreach (var pane in AllPanes)
                    if (pane.CurrentPath != null && !pane.IsThisPC &&
                        (string.Equals(pane.CurrentPath, job.DestDir, StringComparison.OrdinalIgnoreCase) ||
                         job.Sources.Any(s => string.Equals(Path.GetDirectoryName(s), pane.CurrentPath, StringComparison.OrdinalIgnoreCase))))
                        pane.Reload(true);
                if (job.State == JobState.DoneWithErrors || job.State == JobState.Failed) ShowQueue(true);
            }
        }

        int _queueHeight;

        void ShowQueue(bool show)
        {
            if (!show && !_outer.Panel2Collapsed) _queueHeight = _outer.Panel2.Height;
            bool wasCollapsed = _outer.Panel2Collapsed;
            _outer.Panel2Collapsed = !show;
            _miQueue.Checked = show;
            if (show && wasCollapsed)
            {
                int h = Math.Max(Ui.S(120), Math.Min(_queueHeight, _outer.Height / 2));
                try { _outer.SplitterDistance = Math.Max(Ui.S(100), _outer.Height - h - _outer.SplitterWidth); } catch { }
            }
        }

        void SwapPanes()
        {
            string a = _left.CurrentPath, b = _right.CurrentPath;
            _left.Navigate(b);
            _right.Navigate(a);
        }

        void SyncOther() => Other.Navigate(_active.CurrentPath);

        void ToggleHidden()
        {
            FilePane.ShowHidden = !FilePane.ShowHidden;
            _miHidden.Checked = FilePane.ShowHidden;
            foreach (var p in AllPanes) p.Reload(true);
        }

        void ToggleAutoSizes()
        {
            FilePane.AutoSizes = !FilePane.AutoSizes;
            _miAutoSizes.Checked = FilePane.AutoSizes;
            if (FilePane.AutoSizes) { _left.ComputeSizes(false); _right.ComputeSizes(false); }
        }

        void OpenTerminal()
        {
            if (_active.IsThisPC) return;
            string dir = _active.CurrentPath;
            try { Process.Start(new ProcessStartInfo("wt.exe", "-d \"" + dir.TrimEnd('\\') + "\\.\"") { UseShellExecute = true }); }
            catch
            {
                try { Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = dir, UseShellExecute = true }); } catch { }
            }
        }

        void OpenInExplorer()
        {
            try { Process.Start("explorer.exe", _active.IsThisPC ? "shell:MyComputerFolder" : "\"" + _active.CurrentPath + "\""); } catch { }
        }

        void About()
        {
            MessageBox.Show(this,
                "S3bopus " + Application.ProductVersion + " — 2026-10-23\n\n" + (Lang.En ?
                "Lightweight dual-pane file manager.\n\n" +
                "Shortcuts:\n" +
                "  Tab  switch pane\n" +
                "  F2  rename      F3  folder sizes\n" +
                "  F5  copy to the other pane\n" +
                "  F6  move to the other pane\n" +
                "  F7  new folder      F8 / Del  delete\n" +
                "  Ctrl+C / X / V  clipboard (Explorer compatible)\n" +
                "  Ctrl+T  transfer queue      Ctrl+H  hidden items\n" +
                "  Ctrl+L  address bar      Ctrl+F  filter\n" +
                "  Ctrl+U  swap panes      Ctrl+=  same folder\n" +
                "  Ctrl+N / Ctrl+W  new / close tab      Ctrl+Tab  next tab\n" +
                "  Backspace  parent folder\n" +
                "  Middle-click a folder  open it in the other pane" :
                "Explorateur de fichiers léger à deux panneaux.\n\n" +
                "Raccourcis :\n" +
                "  Tab  changer de panneau\n" +
                "  F2  renommer      F3  taille des dossiers\n" +
                "  F5  copier vers l'autre panneau\n" +
                "  F6  déplacer vers l'autre panneau\n" +
                "  F7  nouveau dossier      F8 / Suppr  supprimer\n" +
                "  Ctrl+C / X / V  presse-papiers (compatible Explorateur)\n" +
                "  Ctrl+T  file de transferts      Ctrl+H  éléments masqués\n" +
                "  Ctrl+L  barre d'adresse      Ctrl+F  filtrer\n" +
                "  Ctrl+U  échanger les panneaux      Ctrl+=  même dossier\n" +
                "  Ctrl+N / Ctrl+W  nouvel onglet / fermer      Ctrl+Tab  onglet suivant\n" +
                "  Retour arrière  dossier parent\n" +
                "  Clic molette sur un dossier  l'ouvrir dans l'autre panneau"),
                Lang.T("À propos de S3bopus", "About S3bopus"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inList = _left.List.Focused || _right.List.Focused;
            bool editing = _left.IsEditing || _right.IsEditing;
            if (!editing)
            {
                switch (keyData)
                {
                    case Keys.Tab when inList:
                        SetActive(Other).FocusList();
                        return true;
                    case Keys.F3: _active.ComputeSizes(true); return true;
                    case Keys.F5: TransferToOther(JobKind.Copy); return true;
                    case Keys.F6: TransferToOther(JobKind.Move); return true;
                    case Keys.F7: _active.NewFolder(); return true;
                    case Keys.F8 when inList: _active.DeleteSelected(false); return true;
                    case Keys.Shift | Keys.F8 when inList: _active.DeleteSelected(true); return true;
                    case Keys.Control | Keys.T: ShowQueue(_outer.Panel2Collapsed); return true;
                    case Keys.Control | Keys.H: ToggleHidden(); return true;
                    case Keys.Control | Keys.U: SwapPanes(); return true;
                    case Keys.Control | Keys.N: ActiveTabs.NewTab(_active.CurrentPath).FocusList(); return true;
                    case Keys.Control | Keys.W: ActiveTabs.CloseTab(_active); _active.FocusList(); return true;
                    case Keys.Control | Keys.Tab: ActiveTabs.Cycle(+1); return true;
                    case Keys.Control | Keys.Shift | Keys.Tab: ActiveTabs.Cycle(-1); return true;
                    case Keys.Control | Keys.Oemplus: SyncOther(); return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ======================================================================= fenêtre / réglages

        void RestoreWindow()
        {
            var b = Settings.Get("Bounds");
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            Size = new Size(Ui.S(1280), Ui.S(780));
            if (b != null)
            {
                var p = b.Split(',').Select(x => int.TryParse(x, out int v) ? v : 0).ToArray();
                if (p.Length == 4 && p[2] > 200 && p[3] > 200)
                {
                    var r = new Rectangle(p[0], p[1], p[2], p[3]);
                    if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
                    {
                        StartPosition = FormStartPosition.Manual;
                        Bounds = r;
                    }
                }
            }
            if (Settings.GetBool("Maximized", false)) WindowState = FormWindowState.Maximized;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_queue.Jobs.Any(j => !j.IsFinished) &&
                MessageBox.Show(this, Lang.T("Des transferts sont en cours ou en attente.\nQuitter quand même (ils seront interrompus) ?", "Transfers are running or pending.\nQuit anyway (they will be interrupted)?"), "S3bopus",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            foreach (var j in _queue.Jobs) _queue.Cancel(j);

            var rb = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Settings.Set("Bounds", rb.X + "," + rb.Y + "," + rb.Width + "," + rb.Height);
            Settings.Set("Maximized", WindowState == FormWindowState.Maximized);
            Settings.Set("LeftPath", _left.CurrentPath);
            Settings.Set("RightPath", _right.CurrentPath);
            foreach (var (side, tabs) in new[] { ("Left", _leftTabs), ("Right", _rightTabs) })
            {
                Settings.Set(side + "Tabs", string.Join("\u001f", tabs.Panes.Select(p => p.CurrentPath)));
                Settings.Set(side + "Tab", tabs.Panes.ToList().IndexOf(tabs.Current));
            }
            Settings.Set("LeftColumns", _left.ColumnWidths);
            Settings.Set("RightColumns", _right.ColumnWidths);
            Settings.Set("LeftSort", _left.SortColumn);
            Settings.Set("LeftAsc", _left.SortAscending);
            Settings.Set("RightSort", _right.SortColumn);
            Settings.Set("RightAsc", _right.SortAscending);
            Settings.Set("Split", (int)(_ratio * 1000));
            Settings.Set("QueueHeight", _outer.Panel2Collapsed ? _queueHeight : _outer.Panel2.Height);
            Settings.Set("ShowHidden", FilePane.ShowHidden);
            Settings.Set("AutoSizes", FilePane.AutoSizes);
            Settings.Set("FKeys", _fkeys.Visible);
            Settings.Save();
            base.OnFormClosing(e);
        }
    }
}
