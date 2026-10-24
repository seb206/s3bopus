using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace S3bopus
{
    internal sealed class MainWindow : Window
    {
        readonly PaneTabs _leftTabs, _rightTabs;
        readonly QueuePanel _queuePanel;
        readonly TransferQueue _queue = new TransferQueue();
        readonly Grid _panes, _outer;
        readonly Control _fkeys;
        readonly MenuItem _miHidden, _miAutoSizes, _miFKeys, _miQueue;
        readonly Button _themeButton;
        FilePane _active;
        double _queueHeight;
        bool _closingConfirmed;

        public MainWindow(string[] args)
        {
            Title = "S3bopus";
            MinWidth = 640; MinHeight = 400;
            try { Icon = new WindowIcon(typeof(MainWindow).Assembly.GetManifestResourceStream("s3bopus.png")); } catch { }

            FilePane.ShowHidden = Settings.GetBool("ShowHidden", false);
            FilePane.AutoSizes = Settings.GetBool("AutoSizes", false);

            // ---- Barre de commandes
            Button Cmd(string text, string tip, Action a)
            {
                var b = new Button { Content = text, Padding = new Thickness(10, 4), Margin = new Thickness(1, 0), Background = Brushes.Transparent };
                ToolTip.SetTip(b, tip);
                b.Click += (s, e) => a();
                return b;
            }
            Control Sep() => new Border { Width = 1, Margin = new Thickness(6, 6), Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)) };

            var commands = new StackPanel { Orientation = Orientation.Horizontal };
            commands.Children.Add(Cmd("＋ " + Lang.T("Nouveau dossier", "New folder"), Lang.T("Nouveau dossier (F7 / Ctrl+Maj+N)", "New folder (F7 / Ctrl+Shift+N)"), () => _active.NewFolder()));
            commands.Children.Add(Sep());
            commands.Children.Add(Cmd("✎", Lang.T("Renommer (F2)", "Rename (F2)"), () => _active.BeginRename()));
            commands.Children.Add(Cmd("🗑", Lang.T("Mettre à la corbeille (Suppr / F8) — Maj pour supprimer définitivement", "Move to trash (Del / F8) — Shift to delete permanently"), () => _active.DeleteSelected(false)));
            commands.Children.Add(Sep());
            commands.Children.Add(Cmd("⧉ " + Lang.T("Copier vers", "Copy to"), Lang.T("Copier la sélection vers l'autre panneau (F5)", "Copy the selection to the other pane (F5)"), () => TransferToOther(JobKind.Copy)));
            commands.Children.Add(Cmd("➜ " + Lang.T("Déplacer vers", "Move to"), Lang.T("Déplacer la sélection vers l'autre panneau (F6)", "Move the selection to the other pane (F6)"), () => TransferToOther(JobKind.Move)));
            commands.Children.Add(Sep());
            commands.Children.Add(Cmd("Σ " + Lang.T("Tailles", "Sizes"), Lang.T("Calculer la taille des dossiers sélectionnés, ou de tous (F3)", "Compute the size of selected folders, or all (F3)"), () => _active.ComputeSizes(true)));
            commands.Children.Add(Sep());
            commands.Children.Add(Cmd("⇄", Lang.T("Échanger les panneaux (Ctrl+U)", "Swap panes (Ctrl+U)"), SwapPanes));
            commands.Children.Add(Cmd("⇉", Lang.T("Même dossier dans l'autre panneau (Ctrl+=)", "Same folder in the other pane (Ctrl+=)"), SyncOther));
            commands.Children.Add(Cmd(">_", Lang.T("Ouvrir un terminal ici", "Open a terminal here"), OpenTerminal));
            commands.Children.Add(Cmd("↗", Lang.T("Ouvrir ce dossier dans le gestionnaire de fichiers", "Open this folder in the file manager"), () => Sys.Open(_active.CurrentPath)));

            // Langue (relance) et thème (immédiat)
            var langButton = Cmd(Lang.En ? "FR" : "EN", Lang.T("Switch to English", "Passer en français"), ToggleLanguage);
            _themeButton = Cmd("", "", ToggleTheme);
            UpdateThemeButton();

            var more = Cmd("⋯", Lang.T("Options", "Options"), () => { });
            var menu = new MenuFlyout();
            MenuItem MI(string header, string key, Action a, bool? check = null)
            {
                var mi = new MenuItem { Header = header, InputGesture = key == null ? null : KeyGesture.Parse(key) };
                if (check != null) { mi.ToggleType = MenuItemToggleType.CheckBox; mi.IsChecked = check.Value; }
                mi.Click += (s, e) => a();
                return mi;
            }
            _miQueue = MI(Lang.T("File de transferts", "Transfer queue"), "Ctrl+T", () => ShowQueue(!QueueVisible), false);
            _miHidden = MI(Lang.T("Afficher les éléments masqués", "Show hidden items"), "Ctrl+H", ToggleHidden, FilePane.ShowHidden);
            _miAutoSizes = MI(Lang.T("Calculer automatiquement la taille des dossiers", "Compute folder sizes automatically"), null, ToggleAutoSizes, FilePane.AutoSizes);
            _miFKeys = MI(Lang.T("Barre des touches de fonction", "Function key bar"), null, () => { _fkeys.IsVisible = !_fkeys.IsVisible; _miFKeys.IsChecked = _fkeys.IsVisible; }, Settings.GetBool("FKeys", true));
            foreach (var item in new Control[]
            {
                _miQueue, _miHidden, _miAutoSizes, _miFKeys, new Separator(),
                MI(Lang.T("Sélectionner tout", "Select all"), "Ctrl+A", () => _active.SelectAll()),
                MI(Lang.T("Inverser la sélection", "Invert selection"), null, () => _active.InvertSelection()),
                MI(Lang.T("Actualiser", "Refresh"), "Ctrl+R", () => _active.Reload()),
                new Separator(),
                MI(Lang.T("À propos de S3bopus", "About S3bopus"), null, About)
            }) menu.Items.Add(item);
            more.Flyout = menu;

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(langButton);
            right.Children.Add(_themeButton);
            right.Children.Add(more);
            var top = new DockPanel { Margin = new Thickness(8, 4) };
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
            top.Children.Add(commands);

            // ---- Panneaux
            // (un jeu d'onglets par côté)
            FilePane MakePane(string side)
            {
                var p = new FilePane { Queue = _queue };
                p.OtherPane = () => side == "Left" ? _right : _left;
                p.Activated += x => SetActive(x);
                // Les nouveaux onglets reprennent le tri de l'onglet courant
                var model = (side == "Left" ? _leftTabs : _rightTabs)?.Current;
                p.SetSort(model?.SortColumn ?? Settings.GetInt(side + "Sort", 0), model?.SortAscending ?? Settings.GetBool(side + "Asc", true));
                return p;
            }
            _leftTabs = new PaneTabs(() => MakePane("Left"));
            _rightTabs = new PaneTabs(() => MakePane("Right"));
            _leftTabs.CurrentChanged += p => SetActive(p);
            _rightTabs.CurrentChanged += p => SetActive(p);

            double ratio = Settings.GetInt("Split", 500) / 1000.0;
            _panes = new Grid { ColumnDefinitions = new ColumnDefinitions($"{ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)}*,5,{(1 - ratio).ToString(System.Globalization.CultureInfo.InvariantCulture)}*") };
            Grid.SetColumn(_leftTabs, 0);
            Grid.SetColumn(_rightTabs, 2);
            var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) };
            Grid.SetColumn(split, 1);
            _panes.Children.Add(_leftTabs);
            _panes.Children.Add(split);
            _panes.Children.Add(_rightTabs);

            _queuePanel = new QueuePanel(_queue);
            _queuePanel.CloseRequested += () => ShowQueue(false);
            _queuePanel.OpenFolderRequested += p => { _active.Navigate(p); _active.FocusList(); };
            _queueHeight = Settings.GetInt("QueueHeight", 200);

            _outer = new Grid { RowDefinitions = new RowDefinitions("*,0,0") };
            var qsplit = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) };
            Grid.SetRow(qsplit, 1);
            Grid.SetRow(_queuePanel, 2);
            _outer.Children.Add(_panes);
            _outer.Children.Add(qsplit);
            _outer.Children.Add(_queuePanel);

            // ---- Barre des touches de fonction
            var fk = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 1) };
            void FK(string key, string label, Action a)
            {
                var b = new Button { Content = key + "  " + label, Padding = new Thickness(8, 2), Background = Brushes.Transparent, FontSize = 12 };
                b.Click += (s, e) => a();
                fk.Children.Add(b);
            }
            FK("F2", Lang.T("Renommer", "Rename"), () => _active.BeginRename());
            FK("F3", Lang.T("Tailles", "Sizes"), () => _active.ComputeSizes(true));
            FK("F5", Lang.T("Copier →", "Copy →"), () => TransferToOther(JobKind.Copy));
            FK("F6", Lang.T("Déplacer →", "Move →"), () => TransferToOther(JobKind.Move));
            FK("F7", Lang.T("Nouveau dossier", "New folder"), () => _active.NewFolder());
            FK("F8", Lang.T("Corbeille", "Trash"), () => _active.DeleteSelected(false));
            FK("Tab", Lang.T("Autre panneau", "Other pane"), () => SetActive(Other).FocusList());
            FK("Ctrl+T", Lang.T("File de transferts", "Transfer queue"), () => ShowQueue(!QueueVisible));
            _fkeys = fk;
            _fkeys.IsVisible = _miFKeys.IsChecked;

            var root = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(_fkeys, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), [DockPanel.DockProperty] = Dock.Top });
            root.Children.Add(_fkeys);
            root.Children.Add(_outer);
            Content = root;

            // ---- File de transferts : événements
            _queue.ResolveConflict = (job, info) => Dispatcher.UIThread.InvokeAsync(async () =>
            {
                ShowQueue(true);
                return await Dialogs.Conflict(this, job, info);
            }).GetAwaiter().GetResult();
            _queue.JobChanged += job => Dispatcher.UIThread.Post(() => OnJobChanged(job));

            DispatcherTimer.Run(() =>
            {
                int pct = _queuePanel.OverallPercent();
                Title = pct >= 0 ? "S3bopus — " + pct + " %" : "S3bopus";
                return true;
            }, TimeSpan.FromSeconds(1));

            AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);

            // ---- Position / chemins de départ
            RestoreWindow();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string leftPath = args.Length > 0 ? args[0] : Settings.Get("LeftPath", home);
            string rightPath = args.Length > 1 ? args[1] : Settings.Get("RightPath", home);
            Opened += (s, e) =>
            {
                RestoreTabs(_leftTabs, "Left", leftPath, args.Length > 0);
                RestoreTabs(_rightTabs, "Right", rightPath, args.Length > 1);
                SetActive(_left);
                _left.FocusList();
            };
        }

        static string SafePath(string path) =>
            Directory.Exists(path) || Archives.Split(path, out _, out _) ? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>Rouvre les onglets enregistrés ; un dossier passé en ligne de commande remplace l'onglet courant.</summary>
        static void RestoreTabs(PaneTabs tabs, string side, string path, bool fromArgs)
        {
            var saved = (Settings.Get(side + "Tabs") ?? "").Split('\u001f').Where(p => p.Length > 0).ToList();
            int current = Settings.GetInt(side + "Tab", 0);
            if (saved.Count == 0) { saved.Add(path); current = 0; }
            current = Math.Clamp(current, 0, saved.Count - 1);
            if (fromArgs) saved[current] = path;
            foreach (var p in saved) tabs.NewTab(SafePath(p));
            tabs.Select(tabs.Panes[current]);
        }

        FilePane _left => _leftTabs.Current;
        FilePane _right => _rightTabs.Current;
        IEnumerable<FilePane> AllPanes => _leftTabs.Panes.Concat(_rightTabs.Panes);
        PaneTabs ActiveTabs => _leftTabs.Panes.Contains(_active) ? _leftTabs : _rightTabs;
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
            if (!src.SelectedPaths().Any() || dst.CurrentPath == null || dst.IsArchive || (src.IsArchive && kind == JobKind.Move)) return;
            if (src.CurrentPath == dst.CurrentPath && kind == JobKind.Move) return;
            var paths = src.MaterializeSelection();
            if (paths.Count > 0) _queue.Enqueue(kind, paths, dst.CurrentPath);
        }

        void OnJobChanged(TransferJob job)
        {
            if (job.State == JobState.Waiting && !QueueVisible) ShowQueue(true);
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
                    if (pane.CurrentPath != null && (pane.CurrentPath == job.DestDir || job.Sources.Any(s => Path.GetDirectoryName(s) == pane.CurrentPath)))
                        pane.Reload();
                if (job.State == JobState.DoneWithErrors || job.State == JobState.Failed) ShowQueue(true);
            }
        }

        bool QueueVisible => _outer.RowDefinitions[2].Height.Value > 0;

        void ShowQueue(bool show)
        {
            if (show == QueueVisible) return;
            if (!show) _queueHeight = _outer.RowDefinitions[2].ActualHeight;
            _outer.RowDefinitions[1].Height = new GridLength(show ? 5 : 0);
            _outer.RowDefinitions[2].Height = new GridLength(show ? Math.Max(120, Math.Min(_queueHeight, Bounds.Height / 2)) : 0);
            _miQueue.IsChecked = show;
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
            _miHidden.IsChecked = FilePane.ShowHidden;
            foreach (var p in AllPanes) p.Reload();
        }

        void ToggleAutoSizes()
        {
            FilePane.AutoSizes = !FilePane.AutoSizes;
            _miAutoSizes.IsChecked = FilePane.AutoSizes;
            if (FilePane.AutoSizes) { _left.ComputeSizes(false); _right.ComputeSizes(false); }
        }

        void ToggleTheme()
        {
            bool dark = Application.Current.ActualThemeVariant != ThemeVariant.Dark;
            Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Settings.Set("Dark", dark);
            UpdateThemeButton();
        }

        void UpdateThemeButton()
        {
            bool dark = Settings.GetBool("Dark", false);
            _themeButton.Content = dark ? "☀" : "☾";
            ToolTip.SetTip(_themeButton, dark ? Lang.T("Mode clair", "Light mode") : Lang.T("Mode sombre", "Dark mode"));
        }

        /// <summary>La langue est appliquée en relançant l'application (dossiers et réglages conservés).</summary>
        async void ToggleLanguage()
        {
            Settings.Set("English", !Lang.En);
            if (!await ConfirmClose()) { Settings.Set("English", Lang.En); return; }
            SaveSettings();
            _closingConfirmed = true;
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false }); } catch (Exception ex) { Program.Report(ex); }
            Close();
        }

        void OpenTerminal()
        {
            string dir = _active.CurrentPath;
            // x-terminal-emulator : terminal par défaut de Debian (update-alternatives)
            foreach (var term in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal", "xterm" })
                try { Sys.Run(term, null, dir); return; } catch { }
        }

        async void About()
        {
            await Dialogs.Message(this,
                "S3bopus " + AppInfo.Version + " — " + AppInfo.Date + "\n\n" +
                (Lang.En
                    ? "Lightweight dual-pane file manager.\n\nShortcuts:\n" +
                      "  Tab  switch pane\n  F2  rename      F3  folder sizes\n  F5  copy to the other pane\n  F6  move to the other pane\n" +
                      "  F7  new folder      F8 / Del  trash (Shift: delete permanently)\n  Ctrl+T  transfer queue      Ctrl+H  hidden items\n" +
                      "  Ctrl+L  address bar      Ctrl+F  filter\n  Ctrl+U  swap panes      Ctrl+=  same folder\n" +
                      "  Ctrl+C / X / V  clipboard (text or image: pasted as a file)\n  Ctrl+N / Ctrl+W  new / close tab      Ctrl+Tab  next tab\n" +
                      "  Backspace  parent folder\n  Middle-click a folder  open it in the other pane"
                    : "Explorateur de fichiers léger à deux panneaux.\n\nRaccourcis :\n" +
                      "  Tab  changer de panneau\n  F2  renommer      F3  taille des dossiers\n  F5  copier vers l'autre panneau\n  F6  déplacer vers l'autre panneau\n" +
                      "  F7  nouveau dossier      F8 / Suppr  corbeille (Maj : supprimer définitivement)\n  Ctrl+T  file de transferts      Ctrl+H  éléments masqués\n" +
                      "  Ctrl+L  barre d'adresse      Ctrl+F  filtrer\n  Ctrl+U  échanger les panneaux      Ctrl+=  même dossier\n" +
                      "  Ctrl+C / X / V  presse-papiers (texte ou image : collé comme fichier)\n  Ctrl+N / Ctrl+W  nouvel onglet / fermer      Ctrl+Tab  onglet suivant\n" +
                      "  Retour arrière  dossier parent\n  Clic molette sur un dossier  l'ouvrir dans l'autre panneau"));
        }

        void Window_KeyDown(object sender, KeyEventArgs e)
        {
            bool editing = _left.IsEditing || _right.IsEditing;
            var m = e.KeyModifiers;
            bool handled = true;
            if (e.Key == Key.Tab && m == KeyModifiers.None && !editing) SetActive(Other).FocusList();
            else if (e.Key == Key.F3) _active.ComputeSizes(true);
            else if (e.Key == Key.F5) TransferToOther(JobKind.Copy);
            else if (e.Key == Key.F6) TransferToOther(JobKind.Move);
            else if (e.Key == Key.F7 || (e.Key == Key.N && m == (KeyModifiers.Control | KeyModifiers.Shift))) _active.NewFolder();
            else if (e.Key == Key.F8 && !editing) _active.DeleteSelected(m.HasFlag(KeyModifiers.Shift));
            else if ((e.Key == Key.L && m == KeyModifiers.Control) || (e.Key == Key.D && m == KeyModifiers.Alt) || e.Key == Key.F4) _active.FocusAddress();
            else if (e.Key == Key.F && m == KeyModifiers.Control) _active.FocusFilter();
            else if (e.Key == Key.T && m == KeyModifiers.Control) ShowQueue(!QueueVisible);
            else if (e.Key == Key.H && m == KeyModifiers.Control) ToggleHidden();
            else if (e.Key == Key.U && m == KeyModifiers.Control) SwapPanes();
            else if (e.Key == Key.R && m == KeyModifiers.Control) _active.Reload();
            else if ((e.Key == Key.OemPlus || e.Key == Key.Add) && m == KeyModifiers.Control) SyncOther();
            else if (e.Key == Key.N && m == KeyModifiers.Control) ActiveTabs.NewTab(_active.CurrentPath).FocusList();
            else if (e.Key == Key.W && m == KeyModifiers.Control) { ActiveTabs.CloseTab(_active); _active.FocusList(); }
            else if (e.Key == Key.Tab && m == KeyModifiers.Control) ActiveTabs.Cycle(+1);
            else if (e.Key == Key.Tab && m == (KeyModifiers.Control | KeyModifiers.Shift)) ActiveTabs.Cycle(-1);
            else handled = false;
            if (handled) e.Handled = true;
        }

        // ======================================================================= fenêtre / réglages

        void RestoreWindow()
        {
            Width = 1280; Height = 780;
            var p = (Settings.Get("Bounds") ?? "").Split(',').Select(x => int.TryParse(x, out int v) ? v : 0).ToArray();
            if (p.Length == 4 && p[2] > 200 && p[3] > 200)
            {
                Position = new PixelPoint(p[0], p[1]);
                Width = p[2]; Height = p[3];
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (Settings.GetBool("Maximized", false)) WindowState = WindowState.Maximized;
        }

        async System.Threading.Tasks.Task<bool> ConfirmClose() =>
            !_queue.Jobs.Any(j => !j.IsFinished) ||
            await Dialogs.Confirm(this, Lang.T("Des transferts sont en cours ou en attente.\nQuitter quand même (ils seront interrompus) ?", "Transfers are running or pending.\nQuit anyway (they will be interrupted)?"));

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (_closingConfirmed) return;
            if (_queue.Jobs.Any(j => !j.IsFinished))
            {
                e.Cancel = true;
                if (!await ConfirmClose()) return;
                _closingConfirmed = true;
                SaveSettings();
                Close();
                return;
            }
            SaveSettings();
        }

        void SaveSettings()
        {
            foreach (var j in _queue.Jobs) _queue.Cancel(j);
            if (WindowState == WindowState.Normal) Settings.Set("Bounds", Position.X + "," + Position.Y + "," + (int)Width + "," + (int)Height);
            Settings.Set("Maximized", WindowState == WindowState.Maximized);
            Settings.Set("LeftPath", _left.CurrentPath);
            Settings.Set("RightPath", _right.CurrentPath);
            foreach (var (side, tabs) in new[] { ("Left", _leftTabs), ("Right", _rightTabs) })
            {
                Settings.Set(side + "Tabs", string.Join("\u001f", tabs.Panes.Select(p => p.CurrentPath)));
                Settings.Set(side + "Tab", tabs.Panes.ToList().IndexOf(tabs.Current));
            }
            Settings.Set("LeftSort", _left.SortColumn);
            Settings.Set("LeftAsc", _left.SortAscending);
            Settings.Set("RightSort", _right.SortColumn);
            Settings.Set("RightAsc", _right.SortAscending);
            double l = _panes.ColumnDefinitions[0].ActualWidth, r = _panes.ColumnDefinitions[2].ActualWidth;
            if (l + r > 0) Settings.Set("Split", (int)(l * 1000 / (l + r)));
            Settings.Set("QueueHeight", (int)(QueueVisible ? _outer.RowDefinitions[2].ActualHeight : _queueHeight));
            Settings.Set("ShowHidden", FilePane.ShowHidden);
            Settings.Set("AutoSizes", FilePane.AutoSizes);
            Settings.Set("FKeys", _fkeys.IsVisible);
            Settings.Save();
        }
    }
}
