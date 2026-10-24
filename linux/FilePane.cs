using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace S3bopus
{
    /// <summary>Une ligne de la liste (fichier ou dossier).</summary>
    internal sealed class Entry : INotifyPropertyChanged
    {
        static readonly Geometry FolderIcon = Geometry.Parse("M2,4 L9,4 L11,6 L22,6 L22,20 L2,20 Z");
        static readonly Geometry FileIcon = Geometry.Parse("M5,2 L14,2 L19,7 L19,22 L5,22 Z");
        static readonly IBrush FolderBrush = new SolidColorBrush(Color.FromRgb(220, 160, 20));
        static readonly IBrush FileBrush = new SolidColorBrush(Color.FromRgb(140, 140, 150));

        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDir { get; set; }
        public bool Hidden => Name.StartsWith(".");
        public DateTime Modified { get; set; }
        public long FileSize { get; set; } = -1;
        public string Type { get; set; }
        public Geometry Icon => IsDir ? FolderIcon : FileIcon;
        public IBrush IconBrush => IsDir ? FolderBrush : FileBrush;
        public double Opacity => Hidden ? 0.55 : 1;
        public string ModifiedText => Fmt.Date(Modified);

        /// <summary>Taille d'un dossier (calculée), "…" pendant le calcul.</summary>
        public DirSize DirSize;
        public long SortSize => IsDir ? (DirSize?.Bytes ?? -1) : FileSize;
        public string SizeText => IsDir ? (DirSize == null ? "" : (DirSize.Complete ? "" : "… ") + Fmt.Size(DirSize.Bytes)) : Fmt.Size(FileSize);

        public event PropertyChangedEventHandler PropertyChanged;
        public void SizeChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
    }

    /// <summary>Un panneau : barre de navigation, liste, barre d'état.</summary>
    internal sealed class FilePane : DockPanel
    {
        public static bool ShowHidden, AutoSizes;

        readonly DataGrid _grid;
        readonly TextBox _address, _filter;
        readonly TextBlock _statusLeft, _statusRight;
        readonly Border _activeBar;
        readonly Button _btnBack, _btnFwd, _btnUp;
        readonly List<string> _back = new List<string>(), _fwd = new List<string>();
        List<Entry> _all = new List<Entry>();
        int _sortCol;
        bool _sortAsc = true;
        CancellationTokenSource _sizeCts;
        bool _active;
        string _archiveFile, _archiveInner; // non null : on est dans une archive (lecture seule)

        public string CurrentPath { get; private set; }
        public TransferQueue Queue;
        public Func<FilePane> OtherPane;
        public event Action<FilePane> Activated;
        public event Action<FilePane> PathChanged;
        public bool IsArchive => _archiveFile != null;
        public DataGrid Grid => _grid;

        public FilePane()
        {
            _activeBar = new Border { Height = 2 };
            SetDock(_activeBar, Dock.Top);
            Children.Add(_activeBar);

            // ---- Barre de navigation
            Button Nav(string text, string tip, Action a)
            {
                var b = new Button { Content = text, Padding = new Thickness(8, 2), Margin = new Thickness(1, 0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
                ToolTip.SetTip(b, tip);
                b.Click += (s, e) => a();
                return b;
            }
            _btnBack = Nav("←", Lang.T("Précédent (Alt+Gauche)", "Back (Alt+Left)"), Back);
            _btnFwd = Nav("→", Lang.T("Suivant (Alt+Droite)", "Forward (Alt+Right)"), Forward);
            _btnUp = Nav("↑", Lang.T("Dossier parent (Retour arrière)", "Parent folder (Backspace)"), GoUp);
            var places = Nav("⌂", Lang.T("Emplacements", "Places"), () => { });
            places.Flyout = PlacesMenu();
            var refresh = Nav("⟳", Lang.T("Actualiser (Ctrl+R)", "Refresh (Ctrl+R)"), () => Reload());
            var history = Nav("▾", Lang.T("Emplacements récents", "Recent locations"), () => { });
            history.Flyout = HistoryMenu();

            _address = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            _address.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; GoTyped(_address.Text.Trim()); }
                else if (e.Key == Key.Escape) { e.Handled = true; _address.Text = CurrentPath; _grid.Focus(); }
            };
            _filter = new TextBox { Width = 160, Watermark = Lang.T("Filtrer", "Filter"), VerticalContentAlignment = VerticalAlignment.Center };
            _filter.TextChanged += (s, e) => ApplyView();
            _filter.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; _filter.Text = ""; _grid.Focus(); }
                else if (e.Key == Key.Enter || e.Key == Key.Down) { e.Handled = true; _grid.Focus(); }
            };

            var top = new DockPanel { Margin = new Thickness(2, 4, 6, 4), Height = 32 };
            foreach (var c in new Control[] { _btnBack, _btnFwd, _btnUp, places }) { SetDock(c, Dock.Left); top.Children.Add(c); }
            SetDock(_filter, Dock.Right); top.Children.Add(_filter);
            SetDock(refresh, Dock.Right); top.Children.Add(refresh);
            SetDock(history, Dock.Right); top.Children.Add(history);
            top.Children.Add(_address);
            SetDock(top, Dock.Top);
            Children.Add(top);

            // ---- Barre d'état
            _statusLeft = new TextBlock { Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            _statusRight = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var status = new DockPanel { Margin = new Thickness(8, 2), Height = 22 };
            SetDock(_statusRight, Dock.Right);
            status.Children.Add(_statusRight);
            status.Children.Add(_statusLeft);
            SetDock(status, Dock.Bottom);
            Children.Add(status);

            // ---- Liste
            _grid = new DataGrid
            {
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                CanUserReorderColumns = true,
                CanUserResizeColumns = true,
                CanUserSortColumns = true,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 24,
            };
            var nameCol = new DataGridTemplateColumn
            {
                Width = new DataGridLength(260),
                CellTemplate = new FuncDataTemplate<Entry>((e, ns) =>
                {
                    var icon = new PathIcon { Width = 14, Height = 14, Margin = new Thickness(6, 0, 6, 0), [!PathIcon.DataProperty] = new Binding("Icon"), [!PathIcon.ForegroundProperty] = new Binding("IconBrush") };
                    var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, [!TextBlock.TextProperty] = new Binding("Name") };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, [!OpacityProperty] = new Binding("Opacity") };
                    sp.Children.Add(icon); sp.Children.Add(text);
                    return sp;
                })
            };
            _grid.Columns.Add(nameCol);
            _grid.Columns.Add(new DataGridTextColumn { Width = new DataGridLength(135), Binding = new Binding("ModifiedText") });
            _grid.Columns.Add(new DataGridTextColumn { Width = new DataGridLength(130), Binding = new Binding("Type") });
            _grid.Columns.Add(new DataGridTextColumn { Width = new DataGridLength(95), Binding = new Binding("SizeText") });
            UpdateHeaders();
            _grid.Sorting += (s, e) => { e.Handled = true; SortBy(_grid.Columns.IndexOf(e.Column)); };
            _grid.DoubleTapped += (s, e) => { if (e.Source is Visual v && v.FindAncestorOfType<DataGridRow>() != null) OpenSelected(); };
            _grid.SelectionChanged += (s, e) => UpdateStatus();
            _grid.GotFocus += (s, e) => Activated?.Invoke(this);
            _grid.ContextMenu = BuildContextMenu();
            _grid.PointerReleased += (s, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Middle && e.Source is Visual v && v.FindAncestorOfType<DataGridRow>()?.DataContext is Entry en && en.IsDir)
                    OtherPane?.Invoke().Navigate(en.FullPath);
            };
            _grid.AddHandler(KeyDownEvent, Grid_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            PointerPressed += (s, e) => Activated?.Invoke(this);
            Children.Add(_grid);
        }

        public bool IsActive
        {
            get => _active;
            set { _active = value; _activeBar.Background = value ? new SolidColorBrush(Color.FromRgb(0, 120, 215)) : Brushes.Transparent; }
        }

        public void FocusList() => _grid.Focus();
        public void FocusAddress() { _address.Focus(); _address.SelectAll(); }
        public void FocusFilter() => _filter.Focus();
        public bool IsEditing => _address.IsFocused || _filter.IsFocused;
        public int SortColumn => _sortCol;
        public bool SortAscending => _sortAsc;
        public void SetSort(int col, bool asc) { _sortCol = Math.Clamp(col, 0, 3); _sortAsc = asc; UpdateHeaders(); }

        Window Owner => TopLevel.GetTopLevel(this) as Window;

        // ======================================================================= navigation

        /// <summary>Ouvre un dossier (ou un dossier dans une archive). False si le chemin est introuvable.</summary>
        public bool Navigate(string path, bool addHistory = true)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith("~")) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path.Substring(1);
            if (path.StartsWith("file://")) path = Uri.UnescapeDataString(path.Substring(7));
            try { path = Path.GetFullPath(path); } catch { }
            if (path.Length > 1) path = path.TrimEnd('/');
            string archive = null, inner = null;
            if (!Directory.Exists(path) && Archives.Split(path, out archive, out inner))
            {
                try
                {
                    var all = Archives.List(archive);
                    if (inner.Length > 0 && !all.Any(i => i.IsDir && i.Key == inner)) throw new DirectoryNotFoundException(path);
                }
                catch (Exception ex)
                {
                    _ = Dialogs.Message(Owner, Lang.T("Impossible d'ouvrir « ", "Can't open “") + path + Lang.T(" »", "”") + "\n\n" + ex.Message);
                    _address.Text = CurrentPath;
                    return false;
                }
            }
            else if (File.Exists(path)) { Sys.Open(path); _address.Text = CurrentPath; return false; }
            else if (!Directory.Exists(path))
            {
                _ = Dialogs.Message(Owner, Lang.T("Dossier introuvable : ", "Folder not found: ") + path);
                _address.Text = CurrentPath;
                return false;
            }
            if (addHistory && CurrentPath != null && CurrentPath != path) { _back.Add(CurrentPath); _fwd.Clear(); }
            string from = CurrentPath;
            CurrentPath = path;
            _archiveFile = archive;
            _archiveInner = inner;
            _filter.Text = "";
            Reload(false);
            // En remontant, resélectionner le dossier (ou l'archive) d'où l'on vient
            var sel = from != null && Path.GetDirectoryName(from) == path ? _all.FirstOrDefault(e => e.FullPath == from) : null;
            if (sel != null) { _grid.SelectedItem = sel; _grid.ScrollIntoView(sel, null); }
            else if (_grid.ItemsSource is IList<Entry> l && l.Count > 0) _grid.SelectedIndex = 0;
            PathChanged?.Invoke(this);
            return true;
        }

        void GoTyped(string text)
        {
            if (Navigate(text)) PathHistory.Add(CurrentPath);
            _grid.Focus();
        }

        MenuFlyout HistoryMenu()
        {
            var menu = new MenuFlyout();
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                var items = PathHistory.Items;
                foreach (var p in items)
                {
                    var mi = new MenuItem { Header = p };
                    mi.Click += (s2, e2) => GoTyped(p);
                    menu.Items.Add(mi);
                }
                if (items.Count == 0) { menu.Items.Add(new MenuItem { Header = Lang.T("(aucun emplacement saisi)", "(no typed locations)"), IsEnabled = false }); return; }
                menu.Items.Add(new Separator());
                var clear = new MenuItem { Header = Lang.T("Effacer l'historique", "Clear history") };
                clear.Click += (s2, e2) => PathHistory.Clear();
                menu.Items.Add(clear);
            };
            return menu;
        }

        void Back() { if (_back.Count == 0) return; string p = _back[^1]; _back.RemoveAt(_back.Count - 1); _fwd.Add(CurrentPath); Navigate(p, false); }
        void Forward() { if (_fwd.Count == 0) return; string p = _fwd[^1]; _fwd.RemoveAt(_fwd.Count - 1); _back.Add(CurrentPath); Navigate(p, false); }
        void GoUp() { var p = Path.GetDirectoryName(CurrentPath); if (p != null) Navigate(p); }

        MenuFlyout PlacesMenu()
        {
            var menu = new MenuFlyout();
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var (name, path) in new[]
                {
                    (Lang.T("Dossier personnel", "Home"), home),
                    (Lang.T("Bureau", "Desktop"), Xdg("DESKTOP", "Desktop")),
                    (Lang.T("Téléchargements", "Downloads"), Xdg("DOWNLOAD", "Downloads")),
                    ("Documents", Xdg("DOCUMENTS", "Documents")),
                    (Lang.T("Images", "Pictures"), Xdg("PICTURES", "Pictures")),
                    (Lang.T("Musique", "Music"), Xdg("MUSIC", "Music")),
                    (Lang.T("Vidéos", "Videos"), Xdg("VIDEOS", "Videos")),
                })
                    if (Directory.Exists(path)) menu.Items.Add(Item(name, path));
                menu.Items.Add(new Separator());
                foreach (var d in DriveInfo.GetDrives())
                {
                    string root = d.RootDirectory.FullName;
                    // Seulement / et les disques montés par l'utilisateur, pas /proc, /sys, /run...
                    if (root != "/" && !root.StartsWith("/media/") && !root.StartsWith("/mnt/") && !root.StartsWith("/run/media/") && !root.StartsWith("/home")) continue;
                    string label = root;
                    try { if (d.IsReady) label += "   (" + Fmt.Size(d.AvailableFreeSpace) + Lang.T(" libres", " free") + ")"; } catch { }
                    menu.Items.Add(Item(label, root));
                }
            };
            MenuItem Item(string header, string path)
            {
                var mi = new MenuItem { Header = header };
                mi.Click += (s, e) => { Navigate(path); _grid.Focus(); };
                return mi;
            }
            return menu;
        }

        /// <summary>Dossiers utilisateur XDG (~/.config/user-dirs.dirs), localisés selon la langue du système.</summary>
        static string Xdg(string key, string fallback)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                string file = Path.Combine(home, ".config", "user-dirs.dirs");
                if (File.Exists(file))
                    foreach (var line in File.ReadAllLines(file))
                        if (line.StartsWith("XDG_" + key + "_DIR="))
                            return line.Substring(line.IndexOf('=') + 1).Trim('"').Replace("$HOME", home);
            }
            catch { }
            return Path.Combine(home, fallback);
        }

        // ======================================================================= contenu

        public void Reload(bool keepSelection = true)
        {
            if (CurrentPath == null) return;
            var keep = keepSelection ? SelectedPaths().ToHashSet() : new HashSet<string>();
            var list = new List<Entry>();
            try
            {
                if (IsArchive)
                    foreach (var i in Archives.Children(Archives.List(_archiveFile), _archiveInner))
                        list.Add(new Entry { Name = i.Name, FullPath = Path.Combine(CurrentPath, i.Name), IsDir = i.IsDir, Modified = i.Modified, FileSize = i.Size, Type = Fmt.TypeName(i.Name, i.IsDir) });
                else
                foreach (var fsi in new DirectoryInfo(CurrentPath).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                {
                    if (!ShowHidden && fsi.Name.StartsWith(".")) continue;
                    bool isDir = fsi is DirectoryInfo || (fsi.LinkTarget != null && Directory.Exists(fsi.FullName));
                    long size = -1;
                    if (!isDir) try { size = fsi is FileInfo fi ? fi.Length : 0; } catch { }
                    var en = new Entry { Name = fsi.Name, FullPath = fsi.FullName, IsDir = isDir, Modified = fsi.LastWriteTime, FileSize = size, Type = Fmt.TypeName(fsi.Name, isDir) };
                    if (isDir) en.DirSize = DirSizes.Get(en.FullPath);
                    list.Add(en);
                }
            }
            catch (Exception ex) { _ = Dialogs.Message(Owner, ex.Message); }
            _all = list;
            _address.Text = CurrentPath;
            _btnBack.IsEnabled = _back.Count > 0;
            _btnFwd.IsEnabled = _fwd.Count > 0;
            _btnUp.IsEnabled = Path.GetDirectoryName(CurrentPath) != null;
            ApplyView(keep);
            if (AutoSizes) ComputeSizes(false);
        }

        void ApplyView(HashSet<string> select = null)
        {
            select ??= SelectedPaths().ToHashSet();
            IEnumerable<Entry> view = _all;
            string f = _filter.Text?.Trim();
            if (!string.IsNullOrEmpty(f))
            {
                if (f.Contains('*') || f.Contains('?'))
                {
                    var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(f).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    view = view.Where(e => rx.IsMatch(e.Name));
                }
                else view = view.Where(e => e.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
            }
            var sorted = Sort(view).ToList();
            _grid.ItemsSource = sorted;
            foreach (var e in sorted) if (select.Contains(e.FullPath)) _grid.SelectedItems.Add(e);
            UpdateStatus();
        }

        IEnumerable<Entry> Sort(IEnumerable<Entry> items)
        {
            // Dossiers toujours en premier, comme dans l'Explorateur
            var dirsFirst = items.OrderBy(e => e.IsDir ? 0 : 1);
            IOrderedEnumerable<Entry> o;
            switch (_sortCol)
            {
                case 1: o = _sortAsc ? dirsFirst.ThenBy(e => e.Modified) : dirsFirst.ThenByDescending(e => e.Modified); break;
                case 2: o = _sortAsc ? dirsFirst.ThenBy(e => e.Type, StringComparer.OrdinalIgnoreCase) : dirsFirst.ThenByDescending(e => e.Type, StringComparer.OrdinalIgnoreCase); break;
                case 3: o = _sortAsc ? dirsFirst.ThenBy(e => e.SortSize) : dirsFirst.ThenByDescending(e => e.SortSize); break;
                default: return _sortAsc ? dirsFirst.ThenBy(e => e.Name, NaturalComparer.Instance) : dirsFirst.ThenByDescending(e => e.Name, NaturalComparer.Instance);
            }
            return o.ThenBy(e => e.Name, NaturalComparer.Instance);
        }

        void SortBy(int col)
        {
            if (col < 0) return;
            if (col == _sortCol) _sortAsc = !_sortAsc; else { _sortCol = col; _sortAsc = col != 1 && col != 3; }
            UpdateHeaders();
            ApplyView();
        }

        void UpdateHeaders()
        {
            string[] names = { Lang.T("Nom", "Name"), Lang.T("Modifié le", "Date modified"), Lang.T("Type", "Type"), Lang.T("Taille", "Size") };
            for (int i = 0; i < 4 && i < _grid.Columns.Count; i++)
                _grid.Columns[i].Header = names[i] + (i == _sortCol ? (_sortAsc ? "  ▲" : "  ▼") : "");
        }

        public IEnumerable<string> SelectedPaths() => _grid.SelectedItems.OfType<Entry>().Select(e => e.FullPath);
        IEnumerable<Entry> SelectedEntries => _grid.SelectedItems.OfType<Entry>();

        public void SelectAll() => _grid.SelectAll();

        public void InvertSelection()
        {
            var sel = SelectedEntries.ToHashSet();
            _grid.SelectedItems.Clear();
            foreach (var e in (IEnumerable<Entry>)_grid.ItemsSource) if (!sel.Contains(e)) _grid.SelectedItems.Add(e);
        }

        public void UpdateStatus()
        {
            var view = _grid.ItemsSource as IList<Entry> ?? new List<Entry>();
            string s = Fmt.Count(view.Count, Lang.T("élément", "item"), Lang.T("éléments", "items"));
            if (view.Count != _all.Count) s += Lang.T(" (filtrés sur ", " (filtered from ") + _all.Count + ")";
            var sel = SelectedEntries.ToList();
            var pool = sel.Count > 0 ? sel : view.ToList();
            bool partial = pool.Any(e => e.IsDir && (e.DirSize == null || !e.DirSize.Complete));
            long total = pool.Sum(e => e.IsDir ? (e.DirSize?.Bytes ?? 0) : Math.Max(0, e.FileSize));
            if (sel.Count > 0)
            {
                s += "    " + Fmt.Count(sel.Count, Lang.T("élément sélectionné", "item selected"), Lang.T("éléments sélectionnés", "items selected"));
                s += "  " + (partial ? "≥ " : "") + Fmt.Size(total);
                if (sel.Count == 1 && sel[0].DirSize is DirSize ds && ds.Complete)
                    s += "  (" + Fmt.Count(ds.Files, Lang.T("fichier", "file"), Lang.T("fichiers", "files")) + ", " + Fmt.Count(ds.Folders, Lang.T("dossier", "folder"), Lang.T("dossiers", "folders")) + ")";
            }
            else s += Lang.T("    Total : ", "    Total: ") + (partial ? "≥ " : "") + Fmt.Size(total);
            _statusLeft.Text = s;
            try
            {
                var d = new DriveInfo(CurrentPath);
                _statusRight.Text = Fmt.Size(d.AvailableFreeSpace) + Lang.T(" libres sur ", " free of ") + Fmt.Size(d.TotalSize);
            }
            catch { _statusRight.Text = ""; }
        }

        // ======================================================================= tailles

        public void ComputeSizes(bool force)
        {
            if (IsArchive) return;
            var sel = SelectedEntries.Where(e => e.IsDir).ToList();
            var targets = sel.Count > 0 ? sel : _all.Where(e => e.IsDir).ToList();
            if (targets.Count == 0) return;
            _sizeCts?.Cancel();
            var cts = _sizeCts = new CancellationTokenSource();
            var byPath = targets.ToDictionary(e => e.FullPath);
            Task.Run(() => DirSizes.Compute(byPath.Keys.ToList(), force, cts.Token, (path, size) =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested || !byPath.TryGetValue(path, out var en)) return;
                    en.DirSize = size;
                    en.SizeChanged();
                    if (size.Complete) UpdateStatus();
                })));
        }

        /// <summary>Après un transfert : relit les tailles en cache (certaines ont été invalidées).</summary>
        public void RefreshSizesDisplay()
        {
            foreach (var e in _all.Where(e => e.IsDir)) { e.DirSize = DirSizes.Get(e.FullPath); e.SizeChanged(); }
            UpdateStatus();
        }

        // ======================================================================= actions

        public void OpenSelected()
        {
            var en = _grid.SelectedItem as Entry;
            if (en == null) return;
            if (en.IsDir || (!IsArchive && Archives.IsArchive(en.Name))) { Navigate(en.FullPath); return; }
            string path = en.FullPath;
            if (IsArchive)
            {
                // Fichier d'une archive : extrait dans un dossier temporaire, puis ouvert
                path = MaterializeSelection().FirstOrDefault(File.Exists);
                if (path == null) return;
                if (Archives.IsArchive(path)) { Navigate(path); return; } // archive dans l'archive
            }
            try { Sys.Open(path); }
            catch (Exception ex) { _ = Dialogs.Message(Owner, Lang.T("Impossible d'ouvrir « ", "Can't open “") + path + Lang.T(" »", "”") + "\n\n" + ex.Message); }
        }

        /// <summary>
        /// Chemins réels de la sélection : dans une archive, les éléments sont d'abord extraits dans un dossier temporaire.
        /// shortcut: l'extraction bloque l'interface ; à passer en tâche de fond si les grosses archives gênent.
        /// </summary>
        public List<string> MaterializeSelection()
        {
            var sel = SelectedEntries.ToList();
            if (!IsArchive) return sel.Select(e => e.FullPath).ToList();
            string prefix = _archiveInner.Length == 0 ? "" : _archiveInner + "/";
            try { return Archives.Extract(_archiveFile, sel.Select(e => prefix + e.Name).ToList(), Archives.NewTempDir()); }
            catch (Exception ex) { _ = Dialogs.Message(Owner, ex.Message); return new List<string>(); }
        }

        // ---------------------------------------------------------------- presse-papiers (compatible Nautilus, Dolphin, Thunar)

        const string GnomeFiles = "x-special/gnome-copied-files", UriList = "text/uri-list";

        public async void CopyToClipboard(bool cut)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null || SelectedEntries.Count() == 0) return;
            if (IsArchive) cut = false; // lecture seule : on copie les fichiers extraits
            var uris = MaterializeSelection().Select(p => new Uri(p).AbsoluteUri).ToList();
            if (uris.Count == 0) return;
            var data = new DataObject();
            data.Set(GnomeFiles, System.Text.Encoding.UTF8.GetBytes((cut ? "cut" : "copy") + "\n" + string.Join("\n", uris)));
            data.Set(UriList, System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", uris) + "\r\n"));
            await clipboard.SetDataObjectAsync(data);
        }

        /// <summary>Colle des fichiers (via la file de transferts), ou un texte / une image comme nouveau fichier .txt / .jpg.</summary>
        public async void Paste()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null || IsArchive || Queue == null) return;
            try
            {
                var formats = await clipboard.GetFormatsAsync();
                string files = formats.Contains(GnomeFiles) ? Utf8(await clipboard.GetDataAsync(GnomeFiles))
                             : formats.Contains(UriList) ? "copy\n" + Utf8(await clipboard.GetDataAsync(UriList)) : null;
                var lines = files?.Split('\n').Select(l => l.Trim('\r', ' ')).Where(l => l.Length > 0).ToList();
                if (lines != null && lines.Count > 1)
                {
                    var paths = lines.Skip(1).Where(l => l.StartsWith("file://")).Select(l => new Uri(l).LocalPath).ToList();
                    if (paths.Count > 0)
                    {
                        bool move = lines[0] == "cut";
                        Queue.Enqueue(move ? JobKind.Move : JobKind.Copy, paths, CurrentPath);
                        if (move) await clipboard.ClearAsync();
                        return;
                    }
                }
                string path;
                string text = await clipboard.GetTextAsync();
                // Le texte d'abord : les traitements de texte ajoutent aussi une image de ce qu'on a copié
                if (!string.IsNullOrEmpty(text))
                {
                    path = UniqueName(Lang.T("Texte collé", "Pasted text"), ".txt");
                    await File.WriteAllTextAsync(path, text);
                }
                else if (formats.FirstOrDefault(f => f.StartsWith("image/")) is string imageFormat && await clipboard.GetDataAsync(imageFormat) is byte[] bytes)
                {
                    path = UniqueName(Lang.T("Image collée", "Pasted image"), ".jpg");
                    await File.WriteAllBytesAsync(path, ToJpeg(bytes));
                }
                else return;
                Reload(false);
                Select(path);
            }
            catch (Exception ex) { await Dialogs.Message(Owner, ex.Message); }
        }

        static string Utf8(object data) => data is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : data as string;

        /// <summary>PNG/BMP/... → JPEG qualité 90, sur fond blanc (le JPEG n'a pas de transparence).</summary>
        internal static byte[] ToJpeg(byte[] image)
        {
            using var bmp = SkiaSharp.SKBitmap.Decode(image) ?? throw new InvalidDataException(Lang.T("Image illisible.", "Unreadable image."));
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height));
            surface.Canvas.Clear(SkiaSharp.SKColors.White);
            surface.Canvas.DrawBitmap(bmp, 0, 0);
            using var snapshot = surface.Snapshot();
            using var jpeg = snapshot.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
            return jpeg.ToArray();
        }

        string UniqueName(string name, string ext)
        {
            string path = Path.Combine(CurrentPath, name + ext);
            for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(CurrentPath, name + " (" + i + ")" + ext);
            return path;
        }

        public async void BeginRename()
        {
            if (IsArchive || !(_grid.SelectedItem is Entry en)) return;
            string name = await Dialogs.Input(Owner, Lang.T("Renommer", "Rename"), en.Name);
            if (string.IsNullOrEmpty(name) || name == en.Name) return;
            if (name.Contains('/')) { await Dialogs.Message(Owner, Lang.T("Un nom ne peut pas contenir « / ».", "A name can't contain “/”.")); return; }
            string dst = Path.Combine(CurrentPath, name);
            try
            {
                if (File.Exists(dst) || Directory.Exists(dst)) throw new IOException(Lang.T("Ce nom existe déjà.", "That name already exists."));
                if (en.IsDir) Directory.Move(en.FullPath, dst); else File.Move(en.FullPath, dst);
            }
            catch (Exception ex) { await Dialogs.Message(Owner, Lang.T("Impossible de renommer « ", "Can't rename “") + en.Name + Lang.T(" » :", "”:") + "\n" + ex.Message); return; }
            DirSizes.Invalidate(en.FullPath);
            Reload(false);
            Select(dst);
        }

        public async void NewFolder()
        {
            if (IsArchive) return;
            string name = await Dialogs.Input(Owner, Lang.T("Nouveau dossier", "New folder"), Lang.T("Nouveau dossier", "New folder"));
            if (string.IsNullOrWhiteSpace(name)) return;
            string path = Path.Combine(CurrentPath, name);
            for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = Path.Combine(CurrentPath, name + " (" + i + ")");
            try { Directory.CreateDirectory(path); }
            catch (Exception ex) { await Dialogs.Message(Owner, ex.Message); return; }
            Reload(false);
            Select(path);
        }

        void Select(string path)
        {
            var en = (_grid.ItemsSource as IList<Entry>)?.FirstOrDefault(e => e.FullPath == path);
            if (en == null) return;
            _grid.SelectedItem = en;
            _grid.ScrollIntoView(en, null);
            _grid.Focus();
        }

        public async void DeleteSelected(bool permanent)
        {
            var items = SelectedEntries.ToList();
            if (items.Count == 0 || IsArchive) return;
            string what = items.Count == 1 ? "« " + items[0].Name + " »" : Fmt.Count(items.Count, Lang.T("élément", "item"), Lang.T("éléments", "items"));
            if (permanent && !await Dialogs.Confirm(Owner, Lang.T("Supprimer définitivement ", "Permanently delete ") + what + " ?"))
                return;
            int index = _grid.SelectedIndex;
            var errors = new List<string>();
            await Task.Run(() =>
            {
                foreach (var en in items)
                    try
                    {
                        if (!permanent) Sys.Trash(en.FullPath);
                        else if (en.IsDir && new DirectoryInfo(en.FullPath).LinkTarget == null) Directory.Delete(en.FullPath, true);
                        else File.Delete(en.FullPath);
                        DirSizes.Invalidate(en.FullPath);
                    }
                    catch (Exception ex) { errors.Add(en.Name + " : " + ex.Message); }
            });
            Reload(false);
            if (_grid.ItemsSource is IList<Entry> l && l.Count > 0) _grid.SelectedIndex = Math.Min(index, l.Count - 1);
            if (errors.Count > 0) await Dialogs.Message(Owner, string.Join("\n", errors));
        }

        public void CopyPath()
        {
            var paths = SelectedPaths().ToList();
            if (paths.Count == 0) paths.Add(CurrentPath);
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(string.Join("\n", paths));
        }

        ContextMenu BuildContextMenu()
        {
            MenuItem I(string header, string key, Action a)
            {
                var mi = new MenuItem { Header = header, InputGesture = key == null ? null : KeyGesture.Parse(key) };
                mi.Click += (s, e) => a();
                return mi;
            }
            var other = new MenuItem { Header = Lang.T("Ouvrir dans l'autre panneau", "Open in the other pane") };
            other.Click += (s, e) => { if (_grid.SelectedItem is Entry en && en.IsDir) OtherPane?.Invoke().Navigate(en.FullPath); };
            var menu = new ContextMenu();
            menu.Items.Add(I(Lang.T("Ouvrir", "Open"), "Enter", OpenSelected));
            menu.Items.Add(other);
            menu.Items.Add(new Separator());
            menu.Items.Add(I(Lang.T("Couper", "Cut"), "Ctrl+X", () => CopyToClipboard(true)));
            menu.Items.Add(I(Lang.T("Copier", "Copy"), "Ctrl+C", () => CopyToClipboard(false)));
            menu.Items.Add(I(Lang.T("Coller", "Paste"), "Ctrl+V", Paste));
            menu.Items.Add(new Separator());
            menu.Items.Add(I(Lang.T("Renommer", "Rename"), "F2", BeginRename));
            menu.Items.Add(I(Lang.T("Mettre à la corbeille", "Move to trash"), "Delete", () => DeleteSelected(false)));
            menu.Items.Add(I(Lang.T("Supprimer définitivement", "Delete permanently"), "Shift+Delete", () => DeleteSelected(true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(I(Lang.T("Copier le chemin", "Copy path"), "Ctrl+Shift+C", CopyPath));
            menu.Items.Add(I(Lang.T("Taille des dossiers", "Folder sizes"), "F3", () => ComputeSizes(true)));
            return menu;
        }

        void Grid_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter: OpenSelected(); e.Handled = true; break;
                case Key.Back: GoUp(); e.Handled = true; break;
                case Key.F2: BeginRename(); e.Handled = true; break;
                case Key.C when e.KeyModifiers == KeyModifiers.Control: CopyToClipboard(false); e.Handled = true; break;
                case Key.X when e.KeyModifiers == KeyModifiers.Control: CopyToClipboard(true); e.Handled = true; break;
                case Key.V when e.KeyModifiers == KeyModifiers.Control: Paste(); e.Handled = true; break;
                case Key.Delete: DeleteSelected(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
                case Key.Left when e.KeyModifiers == KeyModifiers.Alt: Back(); e.Handled = true; break;
                case Key.Right when e.KeyModifiers == KeyModifiers.Alt: Forward(); e.Handled = true; break;
                case Key.Up when e.KeyModifiers == KeyModifiers.Alt: GoUp(); e.Handled = true; break;
                case Key.C when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift): CopyPath(); e.Handled = true; break;
            }
        }

        public void HistoryKey(bool back) { if (back) Back(); else Forward(); }
    }

    /// <summary>Tri "naturel" : fichier2 avant fichier10, comme l'Explorateur.</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();
        public int Compare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
