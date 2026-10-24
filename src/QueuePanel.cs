using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace S3bopus
{
    /// <summary>Panneau inférieur affichant la file de transferts.</summary>
    internal sealed class QueuePanel : UserControl
    {
        readonly TransferQueue _queue;
        readonly ListView _lv;
        readonly ToolStrip _bar;
        readonly ToolStripButton _btnPause;
        readonly ToolStripLabel _summary;
        readonly Timer _timer;
        readonly Dictionary<int, ListViewItem> _items = new Dictionary<int, ListViewItem>();

        public event Action CloseRequested;
        public event Action<string> OpenFolderRequested;

        const int ColProgress = 3;

        public QueuePanel(TransferQueue queue)
        {
            _queue = queue;
            Font = Ui.Font;
            BackColor = Ui.Back;

            _bar = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ExplorerRenderer(),
                BackColor = Ui.Back,
                ImageScalingSize = new Size(Ui.S(16), Ui.S(16)),
                Padding = new Padding(Ui.S(6), Ui.S(2), Ui.S(4), Ui.S(2))
            };
            var title = new ToolStripLabel(Lang.T("File de transferts", "Transfer queue")) { Font = new Font(Ui.Font, FontStyle.Bold), Margin = new Padding(0, 0, Ui.S(12), 0) };
            _btnPause = new ToolStripButton(Lang.T("Pause", "Pause"), Ui.Glyph('\uE769', Ui.Ink), (s, e) => TogglePause()) { ToolTipText = Lang.T("Mettre en pause / reprendre tous les transferts", "Pause / resume all transfers") };
            var cancel = new ToolStripButton(Lang.T("Annuler", "Cancel"), Ui.Glyph('\uE711', Color.FromArgb(196, 43, 28)), (s, e) => ForSelected(j => _queue.Cancel(j)));
            var retry = new ToolStripButton(Lang.T("Relancer", "Retry"), Ui.Glyph('\uE72C', Ui.Ink), (s, e) => ForSelected(j => _queue.Retry(j)));
            var up = new ToolStripButton(null, Ui.Glyph('\uE70E', Ui.Ink), (s, e) => ForSelected(j => _queue.MoveJob(j, -1))) { ToolTipText = Lang.T("Monter dans la file", "Move up") };
            var down = new ToolStripButton(null, Ui.Glyph('\uE70D', Ui.Ink), (s, e) => ForSelected(j => _queue.MoveJob(j, +1))) { ToolTipText = Lang.T("Descendre dans la file", "Move down") };
            var clear = new ToolStripButton(Lang.T("Effacer terminés", "Clear finished"), Ui.Glyph('\uE894', Ui.Ink), (s, e) => { _queue.ClearFinished(); RefreshNow(); });
            var errors = new ToolStripButton(Lang.T("Erreurs", "Errors"), Ui.Glyph('\uE7BA', Color.FromArgb(202, 80, 16)), (s, e) => ShowErrors());
            var close = new ToolStripButton(null, Ui.Glyph('\uE8BB', Ui.Ink, 12), (s, e) => CloseRequested?.Invoke()) { Alignment = ToolStripItemAlignment.Right, ToolTipText = Lang.T("Masquer (Ctrl+T)", "Hide (Ctrl+T)") };
            _summary = new ToolStripLabel("") { Alignment = ToolStripItemAlignment.Right, ForeColor = Ui.TextGray };
            _bar.Items.AddRange(new ToolStripItem[] { title, _btnPause, cancel, retry, new ToolStripSeparator(), up, down, new ToolStripSeparator(), clear, errors, close, _summary });

            _lv = new DoubleBufferedListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                OwnerDraw = true,
                ShowItemToolTips = true,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = Ui.Font,
                BackColor = Ui.Back,
                ForeColor = Ui.Fore
            };
            _lv.Columns.Add(Lang.T("Opération", "Operation"), Ui.S(95));
            _lv.Columns.Add("Source", Ui.S(260));
            _lv.Columns.Add("Destination", Ui.S(220));
            _lv.Columns.Add(Lang.T("Progression", "Progress"), Ui.S(150));
            _lv.Columns.Add(Lang.T("Fichiers", "Files"), Ui.S(90), HorizontalAlignment.Right);
            _lv.Columns.Add(Lang.T("Taille", "Size"), Ui.S(140), HorizontalAlignment.Right);
            _lv.Columns.Add(Lang.T("Vitesse", "Speed"), Ui.S(80), HorizontalAlignment.Right);
            _lv.Columns.Add(Lang.T("Restant", "Remaining"), Ui.S(70), HorizontalAlignment.Right);
            _lv.Columns.Add(Lang.T("État", "Status"), Ui.S(130));
            _lv.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            _lv.DrawItem += (s, e) => { };
            _lv.DrawSubItem += Lv_DrawSubItem;
            _lv.HandleCreated += (s, e) => Ui.ExplorerTheme(_lv);
            _lv.DoubleClick += (s, e) => ShowErrors();
            _lv.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { ForSelected(j => { if (j.IsFinished) _queue.Remove(j); else _queue.Cancel(j); }); RefreshNow(); } };

            var menu = new ContextMenuStrip { Renderer = new ExplorerRenderer() };
            menu.Items.Add(Lang.T("Annuler", "Cancel"), null, (s, e) => ForSelected(j => _queue.Cancel(j)));
            menu.Items.Add(Lang.T("Relancer", "Retry"), null, (s, e) => ForSelected(j => _queue.Retry(j)));
            menu.Items.Add(Lang.T("Retirer de la liste", "Remove from list"), null, (s, e) => { ForSelected(j => _queue.Remove(j)); RefreshNow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Lang.T("Ouvrir la destination", "Open destination"), null, (s, e) => { var j = Selected().FirstOrDefault(); if (j != null) OpenFolderRequested?.Invoke(j.DestDir); });
            menu.Items.Add(Lang.T("Voir les erreurs", "Show errors"), null, (s, e) => ShowErrors());
            _lv.ContextMenuStrip = menu;

            var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Ui.Line };
            Controls.Add(_lv);
            Controls.Add(_bar);
            Controls.Add(line);

            _timer = new Timer { Interval = 400, Enabled = true };
            _timer.Tick += (s, e) => RefreshNow();
        }

        sealed class DoubleBufferedListView : ListView
        {
            public DoubleBufferedListView() { DoubleBuffered = true; }
        }

        IEnumerable<TransferJob> Selected() =>
            _lv.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as TransferJob).Where(j => j != null).ToList();

        void ForSelected(Action<TransferJob> action)
        {
            foreach (var j in Selected()) action(j);
            RefreshNow();
        }

        void TogglePause()
        {
            _queue.SetPaused(!TransferQueue.Paused);
            _btnPause.Text = TransferQueue.Paused ? Lang.T("Reprendre", "Resume") : Lang.T("Pause", "Pause");
            _btnPause.Image = Ui.Glyph(TransferQueue.Paused ? '\uE768' : '\uE769', Ui.Ink);
            RefreshNow();
        }

        void ShowErrors()
        {
            var job = Selected().FirstOrDefault(j => j.Errors.Count > 0) ?? _queue.Jobs.LastOrDefault(j => j.Errors.Count > 0);
            if (job == null) { MessageBox.Show(FindForm(), Lang.T("Aucune erreur.", "No errors."), Lang.T("File de transferts", "Transfer queue"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (var f = new Form
            {
                Text = Lang.T("Erreurs — ", "Errors — ") + job.KindText + Lang.T(" vers ", " to ") + job.DestDir,
                Size = new Size(Ui.S(720), Ui.S(400)),
                StartPosition = FormStartPosition.CenterParent,
                Font = Ui.Font,
                ShowInTaskbar = false,
                MinimizeBox = false
            })
            {
                string[] lines;
                lock (job.Errors) lines = job.Errors.ToArray();
                f.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = string.Join(Environment.NewLine, lines), BackColor = Ui.Back, ForeColor = Ui.Fore });
                f.ShowDialog(FindForm());
            }
        }

        /// <summary>Synchronise la liste avec la file (appelé par minuterie et sur événements).</summary>
        public void RefreshNow()
        {
            var jobs = _queue.Jobs;
            var ids = new HashSet<int>(jobs.Select(j => j.Id));
            _lv.BeginUpdate();
            try
            {
                foreach (var id in _items.Keys.Where(k => !ids.Contains(k)).ToList())
                {
                    _lv.Items.Remove(_items[id]);
                    _items.Remove(id);
                }
                long remaining = 0; double speed = 0; int active = 0, waiting = 0;
                var now = DateTime.Now;
                for (int idx = 0; idx < jobs.Length; idx++)
                {
                    var j = jobs[idx];
                    if (!_items.TryGetValue(j.Id, out var it))
                    {
                        it = new ListViewItem(new string[9]) { Tag = j };
                        _items[j.Id] = it;
                        _lv.Items.Insert(Math.Min(idx, _lv.Items.Count), it);
                    }
                    else if (it.Index != idx)
                    {
                        bool sel = it.Selected;
                        _lv.Items.Remove(it);
                        _lv.Items.Insert(Math.Min(idx, _lv.Items.Count), it);
                        it.Selected = sel;
                    }

                    // Vitesse lissée
                    if (j.State == JobState.Running && !TransferQueue.Paused)
                    {
                        double dt = (now - j.LastTick).TotalSeconds;
                        if (j.LastTick == default) { j.LastTick = now; j.LastBytes = j.DoneBytes; }
                        else if (dt >= 0.8)
                        {
                            double inst = (j.DoneBytes - j.LastBytes) / dt;
                            j.Speed = j.Speed <= 0 ? inst : j.Speed * 0.6 + inst * 0.4;
                            j.LastTick = now; j.LastBytes = j.DoneBytes;
                        }
                    }
                    else if (j.IsFinished) j.Speed = 0;

                    string eta = "";
                    if (j.State == JobState.Running && j.Speed > 1)
                        eta = Fmt.Duration(TimeSpan.FromSeconds((j.TotalBytes - j.DoneBytes) / j.Speed));
                    else if (j.IsFinished && j.Finished > j.Started)
                        eta = Fmt.Duration(j.Finished - j.Started);

                    Set(it, 0, j.KindText);
                    Set(it, 1, j.SourceText);
                    Set(it, 2, j.DestDir);
                    Set(it, ColProgress, Percent(j).ToString());
                    Set(it, 4, j.State == JobState.Waiting ? "" : j.DoneFiles + " / " + j.TotalFiles);
                    Set(it, 5, j.State == JobState.Waiting ? "" : Fmt.Size(j.DoneBytes) + " / " + Fmt.Size(j.TotalBytes));
                    Set(it, 6, j.State == JobState.Running && j.Speed > 0 ? Fmt.Size((long)j.Speed) + "/s" : "");
                    Set(it, 7, eta);
                    Set(it, 8, j.StateText);
                    it.ToolTipText = j.Current;

                    if (j.State == JobState.Running || j.State == JobState.Scanning) { active++; remaining += j.TotalBytes - j.DoneBytes; speed += j.Speed; }
                    if (j.State == JobState.Waiting) waiting++;
                }
                string sum = "";
                if (active > 0) sum = Fmt.Size(remaining) + Lang.T(" restants", " remaining") + (speed > 0 ? " • " + Fmt.Size((long)speed) + "/s" : "");
                if (waiting > 0) sum += (sum.Length > 0 ? " • " : "") + waiting + Lang.T(" en attente", " waiting");
                if (TransferQueue.Paused) sum = Lang.T("En pause", "Paused") + (sum.Length > 0 ? " • " + sum : "");
                _summary.Text = sum;
            }
            finally { _lv.EndUpdate(); }
        }

        static void Set(ListViewItem it, int col, string text)
        {
            if (it.SubItems[col].Text != text) it.SubItems[col].Text = text;
        }

        static int Percent(TransferJob j)
        {
            if (j.State == JobState.Done || j.State == JobState.DoneWithErrors) return 100;
            if (j.TotalBytes > 0) return (int)Math.Min(100, j.DoneBytes * 100 / j.TotalBytes);
            if (j.TotalFiles > 0) return j.DoneFiles * 100 / j.TotalFiles;
            return 0;
        }

        /// <summary>Progression globale (0..100) des tâches non terminées, -1 si aucune.</summary>
        public int OverallPercent()
        {
            var jobs = _queue.Jobs.Where(j => j.State == JobState.Running || j.State == JobState.Scanning || j.State == JobState.Waiting).ToList();
            if (jobs.Count == 0) return -1;
            long total = jobs.Sum(j => j.TotalBytes), done = jobs.Sum(j => j.DoneBytes);
            return total > 0 ? (int)(done * 100 / total) : 0;
        }

        void Lv_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != ColProgress) { e.DrawDefault = true; return; }
            var j = e.Item.Tag as TransferJob;
            var g = e.Graphics;
            var r = e.Bounds;
            if (e.Item.Selected) using (var b = new SolidBrush(Ui.Pressed)) g.FillRectangle(b, r);
            int pct = j == null ? 0 : Percent(j);
            var bar = new Rectangle(r.X + Ui.S(4), r.Y + Ui.S(3), r.Width - Ui.S(8), r.Height - Ui.S(6));
            using (var bg = new SolidBrush(Ui.Line)) g.FillRectangle(bg, bar);
            Color fill = Color.FromArgb(6, 176, 37);
            if (j != null)
            {
                if (j.State == JobState.Running && TransferQueue.Paused) fill = Color.FromArgb(218, 165, 32);
                else if (j.State == JobState.Canceled || j.State == JobState.Failed) fill = Color.FromArgb(218, 59, 1);
                else if (j.State == JobState.DoneWithErrors) fill = Color.FromArgb(218, 165, 32);
            }
            if (pct > 0) using (var fb = new SolidBrush(fill)) g.FillRectangle(fb, new Rectangle(bar.X, bar.Y, bar.Width * pct / 100, bar.Height));
            using (var p = new Pen(Color.FromArgb(188, 188, 188))) g.DrawRectangle(p, bar);
            TextRenderer.DrawText(g, pct + " %", Ui.Font, bar, Ui.Fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>Boîte de dialogue Lang.T("Remplacer ou ignorer les fichiers", "Replace or skip files") façon Windows.</summary>
    internal sealed class ConflictForm : Form
    {
        ConflictChoice _choice = ConflictChoice.Skip;
        readonly CheckBox _all;

        ConflictForm(TransferJob job, ConflictInfo info)
        {
            Text = Lang.T("Remplacer ou ignorer les fichiers", "Replace or skip files");
            Font = Ui.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            ForeColor = Ui.Fore;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(500), Ui.S(330));

            int y = Ui.S(14);
            Label L(string t, int h, Font f = null, Color? c = null)
            {
                var l = new Label { Text = t, Location = new Point(Ui.S(18), y), Size = new Size(Ui.S(466), h), AutoEllipsis = true, Font = f ?? Ui.Font, ForeColor = c ?? Ui.Fore };
                Controls.Add(l); y += h; return l;
            }
            L((job.Kind == JobKind.Copy ? Lang.T("Copie", "Copy") : Lang.T("Déplacement", "Move")) + Lang.T(" vers « ", " to “") + Path.GetFileName(Path.GetDirectoryName(info.Destination)) + Lang.T(" »", "”"), Ui.S(20), null, Ui.TextGray);
            L(Lang.T("La destination contient déjà un fichier nommé « ", "The destination already has a file named “") + Path.GetFileName(info.Destination) + Lang.T(" »", "”"), Ui.S(44), new Font("Segoe UI", 11f), (Ui.Dark ? Color.FromArgb(120, 170, 255) : Color.FromArgb(0, 51, 153)));
            y += Ui.S(4);
            L(Lang.T("Source :        ", "Source:           ") + Fmt.Size(info.SourceSize) + Lang.T("   •   modifié le ", "   •   modified ") + Fmt.Date(info.SourceDate) + (info.SourceDate > info.DestDate ? Lang.T("   (plus récent)", "   (newer)") : ""), Ui.S(22));
            L(Lang.T("Destination :  ", "Destination:  ") + Fmt.Size(info.DestSize) + Lang.T("   •   modifié le ", "   •   modified ") + Fmt.Date(info.DestDate) + (info.DestDate > info.SourceDate ? Lang.T("   (plus récent)", "   (newer)") : ""), Ui.S(22));
            y += Ui.S(8);

            Button B(string text, ConflictChoice c)
            {
                var b = new Button { Text = text, Location = new Point(Ui.S(18), y), Size = new Size(Ui.S(466), Ui.S(30)), FlatStyle = FlatStyle.System, TextAlign = ContentAlignment.MiddleLeft };
                b.Click += (s, e) => { _choice = c; DialogResult = DialogResult.OK; };
                Controls.Add(b); y += Ui.S(34); return b;
            }
            B(Lang.T("✔  Remplacer le fichier dans la destination", "✔  Replace the file in the destination"), ConflictChoice.Replace);
            B(Lang.T("↷  Ignorer ce fichier", "↷  Skip this file"), ConflictChoice.Skip);
            B(Lang.T("⧉  Conserver les deux fichiers (renommer la copie)", "⧉  Keep both files (rename the copy)"), ConflictChoice.KeepBoth);
            B(Lang.T("⟳  Remplacer seulement si la source est plus récente", "⟳  Replace only if the source is newer"), ConflictChoice.ReplaceIfNewer);

            _all = new CheckBox { Text = Lang.T("Appliquer à tous les conflits de cette tâche", "Apply to all conflicts of this task"), Location = new Point(Ui.S(18), y + Ui.S(6)), AutoSize = true };
            Controls.Add(_all);
            var cancel = new Button { Text = Lang.T("Annuler la tâche", "Cancel task"), Size = new Size(Ui.S(130), Ui.S(28)), Location = new Point(ClientSize.Width - Ui.S(148), y + Ui.S(2)), FlatStyle = FlatStyle.System };
            cancel.Click += (s, e) => { _choice = ConflictChoice.CancelJob; DialogResult = DialogResult.Cancel; };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        public static ConflictChoice Ask(IWin32Window owner, TransferJob job, ConflictInfo info)
        {
            using (var f = new ConflictForm(job, info))
            {
                f.ShowDialog(owner);
                info.ApplyToAll = f._all.Checked;
                return f._choice;
            }
        }
    }
}
