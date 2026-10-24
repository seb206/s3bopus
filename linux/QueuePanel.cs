using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace S3bopus
{
    /// <summary>Une ligne de la file (rafraîchie par minuterie).</summary>
    internal sealed class JobRow : INotifyPropertyChanged
    {
        public readonly TransferJob Job;
        public JobRow(TransferJob job) { Job = job; }
        public string Kind => Job.KindText;
        public string Source => Job.SourceText;
        public string Dest => Job.DestDir;
        public int Percent => Job.Percent;
        public string PercentText => Percent + " %";
        public string Files => Job.State == JobState.Waiting ? "" : Job.DoneFiles + " / " + Job.TotalFiles;
        public string Size => Job.State == JobState.Waiting ? "" : Fmt.Size(Job.DoneBytes) + " / " + Fmt.Size(Job.TotalBytes);
        public string Speed => Job.State == JobState.Running && Job.Speed > 0 ? Fmt.Size((long)Job.Speed) + "/s" : "";
        public string Remaining => Job.State == JobState.Running && Job.Speed > 0 ? Fmt.Duration(TimeSpan.FromSeconds((Job.TotalBytes - Job.DoneBytes) / Job.Speed)) : "";
        public string State => Job.StateText;
        public IBrush BarBrush =>
            Job.State == JobState.Canceled || Job.State == JobState.Failed ? new SolidColorBrush(Color.FromRgb(218, 59, 1)) :
            Job.State == JobState.DoneWithErrors || (Job.State == JobState.Running && TransferQueue.Paused) ? new SolidColorBrush(Color.FromRgb(218, 165, 32)) :
            new SolidColorBrush(Color.FromRgb(6, 176, 37));

        public event PropertyChangedEventHandler PropertyChanged;
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    internal sealed class QueuePanel : DockPanel
    {
        readonly TransferQueue _queue;
        readonly DataGrid _grid;
        readonly ObservableCollection<JobRow> _rows = new ObservableCollection<JobRow>();
        readonly Button _btnPause;
        readonly TextBlock _summary;

        public event Action CloseRequested;
        public event Action<string> OpenFolderRequested;

        public QueuePanel(TransferQueue queue)
        {
            _queue = queue;

            Button B(string text, string tip, Action a)
            {
                var b = new Button { Content = text, Padding = new Thickness(8, 2), Margin = new Thickness(1, 0), Background = Brushes.Transparent };
                if (tip != null) ToolTip.SetTip(b, tip);
                b.Click += (s, e) => a();
                return b;
            }
            var bar = new DockPanel { Margin = new Thickness(6, 2, 4, 2) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(new TextBlock { Text = Lang.T("File de transferts", "Transfer queue"), FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            _btnPause = B("⏸ " + Lang.T("Pause", "Pause"), Lang.T("Mettre en pause / reprendre tous les transferts", "Pause / resume all transfers"), TogglePause);
            left.Children.Add(_btnPause);
            left.Children.Add(B("✕ " + Lang.T("Annuler", "Cancel"), null, () => ForSelected(j => _queue.Cancel(j))));
            left.Children.Add(B("⟳ " + Lang.T("Relancer", "Retry"), null, () => ForSelected(j => _queue.Retry(j))));
            left.Children.Add(B("▲", Lang.T("Monter dans la file", "Move up"), () => ForSelected(j => _queue.MoveJob(j, -1))));
            left.Children.Add(B("▼", Lang.T("Descendre dans la file", "Move down"), () => ForSelected(j => _queue.MoveJob(j, +1))));
            left.Children.Add(B(Lang.T("Effacer terminés", "Clear finished"), null, () => { _queue.ClearFinished(); RefreshNow(); }));
            left.Children.Add(B("⚠ " + Lang.T("Erreurs", "Errors"), null, ShowErrors));
            var close = B("✕", Lang.T("Masquer (Ctrl+T)", "Hide (Ctrl+T)"), () => CloseRequested?.Invoke());
            _summary = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
            SetDock(close, Dock.Right); bar.Children.Add(close);
            SetDock(_summary, Dock.Right); bar.Children.Add(_summary);
            bar.Children.Add(left);
            SetDock(bar, Dock.Top);
            Children.Add(bar);

            _grid = new DataGrid
            {
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                CanUserSortColumns = false,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 24,
                ItemsSource = _rows
            };
            void Col(string header, string binding, double width) =>
                _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(binding), Width = new DataGridLength(width) });
            Col(Lang.T("Opération", "Operation"), "Kind", 95);
            Col("Source", "Source", 260);
            Col("Destination", "Dest", 220);
            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = Lang.T("Progression", "Progress"),
                Width = new DataGridLength(150),
                CellTemplate = new FuncDataTemplate<JobRow>((r, ns) =>
                {
                    var pb = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16, MinHeight = 16, Margin = new Thickness(4, 0), ShowProgressText = true,
                        [!ProgressBar.ValueProperty] = new Binding("Percent"), [!ProgressBar.ForegroundProperty] = new Binding("BarBrush") };
                    return pb;
                })
            });
            Col(Lang.T("Fichiers", "Files"), "Files", 90);
            Col(Lang.T("Taille", "Size"), "Size", 150);
            Col(Lang.T("Vitesse", "Speed"), "Speed", 85);
            Col(Lang.T("Restant", "Remaining"), "Remaining", 75);
            Col(Lang.T("État", "Status"), "State", 140);
            _grid.DoubleTapped += (s, e) => ShowErrors();
            _grid.KeyDown += (s, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Delete) return;
                ForSelected(j => { if (j.IsFinished) _queue.Remove(j); else _queue.Cancel(j); });
                RefreshNow();
            };

            var menu = new ContextMenu();
            void M(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (s, e) => a(); menu.Items.Add(mi); }
            M(Lang.T("Annuler", "Cancel"), () => ForSelected(j => _queue.Cancel(j)));
            M(Lang.T("Relancer", "Retry"), () => ForSelected(j => _queue.Retry(j)));
            M(Lang.T("Retirer de la liste", "Remove from list"), () => { ForSelected(j => _queue.Remove(j)); RefreshNow(); });
            M(Lang.T("Ouvrir la destination", "Open destination"), () => { var j = Selected().FirstOrDefault(); if (j != null) OpenFolderRequested?.Invoke(j.DestDir); });
            M(Lang.T("Voir les erreurs", "Show errors"), ShowErrors);
            _grid.ContextMenu = menu;
            Children.Add(_grid);

            DispatcherTimer.Run(() => { RefreshNow(); return true; }, TimeSpan.FromMilliseconds(500));
        }

        IEnumerable<TransferJob> Selected() => _grid.SelectedItems.OfType<JobRow>().Select(r => r.Job).ToList();
        void ForSelected(Action<TransferJob> a) { foreach (var j in Selected()) a(j); RefreshNow(); }

        void TogglePause()
        {
            _queue.SetPaused(!TransferQueue.Paused);
            _btnPause.Content = TransferQueue.Paused ? "▶ " + Lang.T("Reprendre", "Resume") : "⏸ " + Lang.T("Pause", "Pause");
            RefreshNow();
        }

        async void ShowErrors()
        {
            var job = Selected().FirstOrDefault(j => j.Errors.Count > 0) ?? _queue.Jobs.LastOrDefault(j => j.Errors.Count > 0);
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (job == null) { await Dialogs.Message(owner, Lang.T("Aucune erreur.", "No errors.")); return; }
            string[] lines;
            lock (job.Errors) lines = job.Errors.ToArray();
            var w = new Window
            {
                Title = Lang.T("Erreurs — ", "Errors — ") + job.KindText + Lang.T(" vers ", " to ") + job.DestDir,
                Width = 720, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox { IsReadOnly = true, AcceptsReturn = true, Text = string.Join("\n", lines), FontFamily = new FontFamily("monospace") }
            };
            await w.ShowDialog(owner);
        }

        public int OverallPercent()
        {
            var active = _queue.Jobs.Where(j => !j.IsFinished).ToList();
            if (active.Count == 0) return -1;
            long total = active.Sum(j => j.TotalBytes), done = active.Sum(j => j.DoneBytes);
            return total > 0 ? (int)(done * 100 / total) : 0;
        }

        public void RefreshNow()
        {
            var jobs = _queue.Jobs;
            // Resynchronise les lignes si la file a changé (ajout, retrait, ordre)
            if (jobs.Length != _rows.Count || jobs.Where((j, i) => _rows[i].Job != j).Any())
            {
                var sel = Selected().ToHashSet();
                var existing = _rows.ToDictionary(r => r.Job);
                _rows.Clear();
                foreach (var j in jobs) _rows.Add(existing.TryGetValue(j, out var r) ? r : new JobRow(j));
                foreach (var r in _rows) if (sel.Contains(r.Job)) _grid.SelectedItems.Add(r);
            }
            var now = DateTime.UtcNow;
            double speed = 0;
            long remaining = 0;
            int waiting = 0, active = 0;
            foreach (var r in _rows)
            {
                var j = r.Job;
                if (j.State == JobState.Running)
                {
                    double dt = (now - j.LastTick).TotalSeconds;
                    if (j.LastTick != default && dt > 0.2)
                    {
                        double inst = (j.DoneBytes - j.LastBytes) / dt;
                        j.Speed = j.Speed <= 0 ? inst : j.Speed * 0.7 + inst * 0.3;
                    }
                    if (j.LastTick == default || dt > 0.2) { j.LastBytes = j.DoneBytes; j.LastTick = now; }
                    speed += j.Speed; remaining += j.TotalBytes - j.DoneBytes; active++;
                }
                else if (j.State == JobState.Waiting) waiting++;
                r.Refresh();
            }
            string sum = "";
            if (active > 0) sum = Fmt.Size(remaining) + Lang.T(" restants", " remaining") + (speed > 0 ? " • " + Fmt.Size((long)speed) + "/s" : "");
            if (waiting > 0) sum += (sum.Length > 0 ? " • " : "") + waiting + Lang.T(" en attente", " waiting");
            if (TransferQueue.Paused) sum = Lang.T("En pause", "Paused") + (sum.Length > 0 ? " • " + sum : "");
            _summary.Text = sum;
        }
    }

    /// <summary>Petites boîtes de dialogue (Avalonia n'a pas de MessageBox).</summary>
    internal static class Dialogs
    {
        static Window Make(string title, Control content, double width = 460) => new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(18), Child = content }
        };

        static StackPanel Buttons(params Button[] buttons)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
            foreach (var b in buttons) { b.MinWidth = 90; b.HorizontalContentAlignment = HorizontalAlignment.Center; sp.Children.Add(b); }
            return sp;
        }

        public static async Task Message(Window owner, string text)
        {
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(Buttons(ok));
            var w = Make("S3bopus", sp);
            ok.Click += (s, e) => w.Close();
            if (owner != null) await w.ShowDialog(owner); else w.Show();
        }

        public static async Task<bool> Confirm(Window owner, string text)
        {
            var yes = new Button { Content = Lang.T("Oui", "Yes"), IsDefault = true };
            var no = new Button { Content = Lang.T("Non", "No"), IsCancel = true };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(Buttons(yes, no));
            var w = Make("S3bopus", sp);
            yes.Click += (s, e) => w.Close(true);
            no.Click += (s, e) => w.Close(false);
            return await w.ShowDialog<bool>(owner);
        }

        public static async Task<string> Input(Window owner, string title, string value)
        {
            var tb = new TextBox { Text = value };
            var ok = new Button { Content = "OK", IsDefault = true };
            var cancel = new Button { Content = Lang.T("Annuler", "Cancel"), IsCancel = true };
            var sp = new StackPanel();
            sp.Children.Add(tb);
            sp.Children.Add(Buttons(ok, cancel));
            var w = Make(title, sp);
            ok.Click += (s, e) => w.Close(tb.Text?.Trim());
            cancel.Click += (s, e) => w.Close(null);
            w.Opened += (s, e) =>
            {
                tb.Focus();
                // Sélectionne le nom sans l'extension, comme l'Explorateur
                int dot = value.LastIndexOf('.');
                tb.SelectionStart = 0;
                tb.SelectionEnd = dot > 0 ? dot : value.Length;
            };
            return await w.ShowDialog<string>(owner);
        }

        /// <summary>Fenêtre "Remplacer ou ignorer les fichiers".</summary>
        public static async Task<ConflictChoice> Conflict(Window owner, TransferJob job, ConflictInfo info)
        {
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = job.KindText + Lang.T(" vers « ", " to “") + Path.GetFileName(Path.GetDirectoryName(info.Destination)) + Lang.T(" »", "”"), Opacity = 0.7 });
            sp.Children.Add(new TextBlock { Text = Lang.T("La destination contient déjà un fichier nommé « ", "The destination already has a file named “") + Path.GetFileName(info.Destination) + Lang.T(" »", "”"),
                FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            sp.Children.Add(new TextBlock { Text = Lang.T("Source : ", "Source: ") + Fmt.Size(info.SourceSize) + Lang.T("   •   modifié le ", "   •   modified ") + Fmt.Date(info.SourceDate) + (info.SourceDate > info.DestDate ? Lang.T("   (plus récent)", "   (newer)") : "") });
            sp.Children.Add(new TextBlock { Text = Lang.T("Destination : ", "Destination: ") + Fmt.Size(info.DestSize) + Lang.T("   •   modifié le ", "   •   modified ") + Fmt.Date(info.DestDate) + (info.DestDate > info.SourceDate ? Lang.T("   (plus récent)", "   (newer)") : ""), Margin = new Thickness(0, 0, 0, 8) });
            var all = new CheckBox { Content = Lang.T("Appliquer à tous les conflits de cette tâche", "Apply to all conflicts of this task") };
            Window w = null;
            void B(string text, ConflictChoice c)
            {
                var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
                b.Click += (s, e) => w.Close(c);
                sp.Children.Add(b);
            }
            B("✔  " + Lang.T("Remplacer le fichier dans la destination", "Replace the file in the destination"), ConflictChoice.Replace);
            B("↷  " + Lang.T("Ignorer ce fichier", "Skip this file"), ConflictChoice.Skip);
            B("⧉  " + Lang.T("Conserver les deux fichiers (renommer la copie)", "Keep both files (rename the copy)"), ConflictChoice.KeepBoth);
            B("⟳  " + Lang.T("Remplacer seulement si la source est plus récente", "Replace only if the source is newer"), ConflictChoice.ReplaceIfNewer);
            var cancel = new Button { Content = Lang.T("Annuler la tâche", "Cancel task"), IsCancel = true };
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(cancel, Dock.Right);
            bottom.Children.Add(cancel);
            bottom.Children.Add(all);
            sp.Children.Add(bottom);
            w = Make(Lang.T("Remplacer ou ignorer les fichiers", "Replace or skip files"), sp, 520);
            cancel.Click += (s, e) => w.Close(ConflictChoice.CancelJob);
            // Fermer la fenêtre = annuler la tâche (ShowDialog renvoie alors la valeur par défaut, Ask)
            var choice = await w.ShowDialog<ConflictChoice>(owner);
            if (choice == ConflictChoice.Ask) choice = ConflictChoice.CancelJob;
            info.ApplyToAll = all.IsChecked == true;
            return choice;
        }
    }
}
