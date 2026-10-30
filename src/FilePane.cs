using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace S3bopus
{
    internal sealed class Entry
    {
        public string Name, FullPath;
        public bool IsDir, IsDrive;
        public long Size;           // fichiers : taille ; lecteurs : capacité totale
        public long Free = -1;      // lecteurs : espace libre
        public DateTime Modified;
        public FileAttributes Attributes;
        public int Icon = -1;
        string _type;
        public string Type
        {
            get => _type ??= SysIcons.TypeName(Name, IsDir);
            set => _type = value;
        }
        public bool Hidden => (Attributes & FileAttributes.Hidden) != 0;
        public long SortSize => IsDir && !IsDrive ? (DirSizes.Get(FullPath)?.Bytes ?? -1) : Size;
    }

    /// <summary>ListView en mode virtuel avec le thème de l'Explorateur et la liste d'images système.</summary>
    internal sealed class ExplorerListView : ListView
    {
        public event Action<Point, bool> ShellMenuRequested;

        public ExplorerListView()
        {
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= Native.LVS_SHAREIMAGELISTS; // ne jamais détruire la liste d'images système
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.ExplorerTheme(this);
            Native.SendMessage(Handle, Native.LVM_SETIMAGELIST, (IntPtr)Native.LVSIL_SMALL, SysIcons.SmallImageList);
            Native.SendMessage(Handle, Native.WM_CHANGEUISTATE, (IntPtr)(1 | (0x1 << 16)), IntPtr.Zero); // masque les rectangles de focus
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_CONTEXTMENU)
            {
                long lp = m.LParam.ToInt64();
                bool keyboard = (int)lp == -1;
                Point pt;
                if (keyboard)
                {
                    var r = FocusedItem != null ? FocusedItem.Bounds : new Rectangle(0, 0, 10, 10);
                    pt = PointToScreen(new Point(r.Left + Ui.S(20), r.Bottom));
                }
                else
                {
                    pt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                    // Clic droit sur l'en-tête de colonnes : pas de menu shell
                    IntPtr header = Native.SendMessage(Handle, Native.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero && Native.GetWindowRect(header, out var hr) && pt.X >= hr.Left && pt.X < hr.Right && pt.Y >= hr.Top && pt.Y < hr.Bottom)
                        return;
                }
                ShellMenuRequested?.Invoke(pt, keyboard);
                return;
            }
            if (m.Msg == 0x0201 /* WM_LBUTTONDOWN */ || m.Msg == 0x0203 /* WM_LBUTTONDBLCLK */)
            {
                var p = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                if (!OverName(p))
                {
                    // Hors du nom (autre colonne, espace vide) : rien n'est sélectionné, le double-clic remonte.
                    if (m.Msg == 0x0203) EmptyDoubleClick?.Invoke();
                    else { SelectedIndices.Clear(); Focus(); }
                    return;
                }
            }
            base.WndProc(ref m);
        }

        // Vrai si le point est sur l'icône ou le texte du nom (pas sur le reste de la ligne).
        bool OverName(Point p)
        {
            var it = HitTest(p).Item;
            if (it == null) return false;
            var ic = it.GetBounds(ItemBoundsPortion.Icon);
            var lb = it.GetBounds(ItemBoundsPortion.Label);
            return p.X >= ic.Left && p.X <= lb.Left + TextRenderer.MeasureText(it.Text, Font).Width + 8;
        }

        public event Action EmptyDoubleClick;

        public void SetSortIcon(int column, bool ascending)
        {
            IntPtr header = Native.SendMessage(Handle, Native.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < Columns.Count; i++)
            {
                var item = new Native.HDITEM { mask = Native.HDI_FORMAT };
                Native.SendMessageHDItem(header, Native.HDM_GETITEMW, (IntPtr)i, ref item);
                item.fmt &= ~(Native.HDF_SORTUP | Native.HDF_SORTDOWN);
                if (i == column) item.fmt |= ascending ? Native.HDF_SORTUP : Native.HDF_SORTDOWN;
                Native.SendMessageHDItem(header, Native.HDM_SETITEMW, (IntPtr)i, ref item);
            }
        }

        int _dropHilite = -1;
        public void SetDropHighlight(int index)
        {
            if (index == _dropHilite) return;
            if (_dropHilite >= 0) SetState(_dropHilite, 0);
            _dropHilite = index;
            if (index >= 0) SetState(index, Native.LVIS_DROPHILITED);
        }

        void SetState(int index, int state)
        {
            var it = new Native.LVITEM { stateMask = Native.LVIS_DROPHILITED, state = state };
            Native.SendMessageLVItem(Handle, Native.LVM_SETITEMSTATE, (IntPtr)index, ref it);
        }
    }

    /// <summary>Un panneau de l'explorateur : barre de navigation, liste de fichiers, barre d'état.</summary>
    internal sealed class FilePane : UserControl
    {
        public static bool ShowHidden;
        public static bool AutoSizes;

        readonly Panel _activeBar;
        readonly ToolStrip _nav;
        readonly ToolStripButton _btnBack, _btnFwd, _btnUp;
        readonly AddressBar _address;
        readonly TextBox _filter;
        readonly ExplorerListView _lv;
        readonly Label _statusLeft, _statusRight;
        readonly System.Windows.Forms.Timer _reloadTimer, _uiTimer;
        readonly ShellContextMenu _shellMenu = new ShellContextMenu();
        readonly Stack<string> _back = new Stack<string>(), _fwd = new Stack<string>();

        List<Entry> _all = new List<Entry>();
        List<Entry> _view = new List<Entry>();
        FileSystemWatcher _watcher;
        CancellationTokenSource _sizeCts;
        volatile bool _sizesDirty;
        int _gen;
        int _sortCol;
        bool _sortAsc = true;
        bool _active, _editingLabel, _statusDirty;
        string _pendingSelect;
        bool _pendingEdit;
        string _archiveFile, _archiveInner; // non null : on est dans une archive (lecture seule)

        public string CurrentPath { get; private set; }
        public TransferQueue Queue;
        public Func<FilePane> OtherPane;
        public event Action<FilePane> Activated;
        public event Action<FilePane> PathChanged;
        public bool IsArchive => _archiveFile != null;

        public FilePane()
        {
            Font = Ui.Font;
            BackColor = Ui.Back;

            _activeBar = new Panel { Dock = DockStyle.Top, Height = Ui.S(2), BackColor = Ui.Line };

            var top = new Panel { Dock = DockStyle.Top, Height = Ui.S(34), Padding = new Padding(Ui.S(2), Ui.S(4), Ui.S(6), Ui.S(4)), BackColor = Ui.Back };
            _nav = new ToolStrip
            {
                Dock = DockStyle.Left,
                LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow,
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ExplorerRenderer(),
                AutoSize = false,
                Width = Ui.S(96),
                BackColor = Ui.Back,
                ImageScalingSize = new Size(Ui.S(16), Ui.S(16)),
                CanOverflow = false
            };
            Color navColor = Ui.Ink;
            _btnBack = NavButton('\uE72B', Lang.T("Précédent (Alt+Gauche)", "Back (Alt+Left)"), navColor, (s, e) => Back());
            _btnFwd = NavButton('\uE72A', Lang.T("Suivant (Alt+Droite)", "Forward (Alt+Right)"), navColor, (s, e) => Forward());
            _btnUp = NavButton('\uE74A', Lang.T("Dossier parent (Retour arrière)", "Parent folder (Backspace)"), navColor, (s, e) => GoUp());
            _nav.Items.AddRange(new ToolStripItem[] { _btnBack, _btnFwd, _btnUp });

            _address = new AddressBar { Dock = DockStyle.Fill };
            _address.NavigateRequested += p => { if (p == null) _lv.Focus(); else { Navigate(p); _lv.Focus(); } };
            _address.PathTyped += p => { if (Navigate(p)) PathHistory.Add(CurrentPath); _lv.Focus(); };

            var refresh = new ToolStrip
            {
                Dock = DockStyle.Right, LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow, GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ExplorerRenderer(), AutoSize = false, Width = Ui.S(30), BackColor = Ui.Back,
                ImageScalingSize = new Size(Ui.S(16), Ui.S(16)), CanOverflow = false
            };
            refresh.Items.Add(NavButton('\uE72C', Lang.T("Actualiser (Ctrl+R)", "Refresh (Ctrl+R)"), navColor, (s, e) => Reload(true)));

            var filterBox = new BorderBox { Dock = DockStyle.Right, Width = Ui.S(170) };
            _filter = new TextBox { BorderStyle = BorderStyle.None, Font = Ui.Font, BackColor = Ui.Back, ForeColor = Ui.Fore };
            Ui.CueBanner(_filter, Lang.T("Filtrer", "Filter"));
            _filter.TextChanged += (s, e) => ApplyView(SelectedPaths().ToList(), null);
            _filter.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _filter.Text = ""; _lv.Focus(); }
                else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; _lv.Focus(); }
            };
            filterBox.Inner = _filter;

            top.Controls.Add(_address);
            top.Controls.Add(new Panel { Dock = DockStyle.Right, Width = Ui.S(6) });
            top.Controls.Add(filterBox);
            top.Controls.Add(refresh);
            top.Controls.Add(_nav);

            _lv = new ExplorerListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                VirtualMode = true,
                FullRowSelect = false,
                HideSelection = false,
                LabelEdit = true,
                AllowColumnReorder = true,
                AllowDrop = true,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Clickable,
                Font = Ui.Font,
                BackColor = Ui.Back,
                ForeColor = Ui.Fore
            };
            _lv.Columns.Add(Lang.T("Nom", "Name"), Ui.S(250));
            _lv.Columns.Add(Lang.T("Modifié le", "Date modified"), Ui.S(120));
            _lv.Columns.Add("Type", Ui.S(130));
            _lv.Columns.Add(Lang.T("Taille", "Size"), Ui.S(90), HorizontalAlignment.Right);
            _lv.RetrieveVirtualItem += Lv_RetrieveVirtualItem;
            _lv.SearchForVirtualItem += Lv_SearchForVirtualItem;
            _lv.ColumnClick += (s, e) => SortBy(e.Column);
            _lv.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSelected(); };
            _lv.EmptyDoubleClick += GoUp;
            _lv.MouseUp += Lv_MouseUp;
            _lv.KeyDown += Lv_KeyDown;
            _lv.SelectedIndexChanged += (s, e) => _statusDirty = true;
            _lv.VirtualItemsSelectionRangeChanged += (s, e) => _statusDirty = true;
            _lv.BeforeLabelEdit += Lv_BeforeLabelEdit;
            _lv.AfterLabelEdit += Lv_AfterLabelEdit;
            _lv.ItemDrag += Lv_ItemDrag;
            _lv.DragOver += Lv_DragOver;
            _lv.DragDrop += Lv_DragDrop;
            _lv.DragLeave += (s, e) => _lv.SetDropHighlight(-1);
            _lv.ShellMenuRequested += Lv_ShellMenu;
            _shellMenu.VerbHandler = HandleShellVerb;

            var status = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(22), Padding = new Padding(Ui.S(8), 0, Ui.S(8), 0), BackColor = Ui.Back };
            _statusLeft = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.TextGray, AutoEllipsis = true };
            _statusRight = new Label { Dock = DockStyle.Right, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = Ui.TextGray, Padding = new Padding(0, Ui.S(4), 0, 0) };
            status.Controls.Add(_statusLeft);
            status.Controls.Add(_statusRight);

            Controls.Add(_lv);
            Controls.Add(status);
            Controls.Add(top);
            Controls.Add(_activeBar);

            _reloadTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _reloadTimer.Tick += (s, e) =>
            {
                _reloadTimer.Stop();
                if (_editingLabel) { _reloadTimer.Start(); return; } // ne pas interrompre un renommage
                Reload(true);
            };
            _uiTimer = new System.Windows.Forms.Timer { Interval = 250, Enabled = true };
            _uiTimer.Tick += (s, e) =>
            {
                if (_sizesDirty) { _sizesDirty = false; _lv.Invalidate(); _statusDirty = true; }
                if (_statusDirty) { _statusDirty = false; UpdateStatus(); }
            };
        }

        ToolStripButton NavButton(char glyph, string tip, Color c, EventHandler click) =>
            new ToolStripButton(null, Ui.Glyph(glyph, c), click) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Image, AutoSize = false, Size = new Size(Ui.S(28), Ui.S(26)) };

        // ======================================================================= état / API

        public bool IsActive
        {
            get => _active;
            set { _active = value; _activeBar.BackColor = value ? Ui.Accent : Ui.Line; }
        }

        public ListView List => _lv;
        public bool IsThisPC => CurrentPath == AddressBar.ThisPC;
        public bool IsEditing => _editingLabel;
        public void FocusList() => _lv.Focus();
        public void FocusAddress() => _address.BeginEdit();
        public void FocusFilter() { _filter.Focus(); _filter.SelectAll(); }

        public int SortColumn => _sortCol;
        public bool SortAscending => _sortAsc;

        public string ColumnWidths
        {
            get => string.Join(",", _lv.Columns.Cast<ColumnHeader>().Select(c => c.Width));
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                var parts = value.Split(',');
                for (int i = 0; i < parts.Length && i < _lv.Columns.Count; i++)
                    if (int.TryParse(parts[i], out int w) && w > 10) _lv.Columns[i].Width = w;
            }
        }

        public void SetSort(int col, bool asc)
        {
            _sortCol = Math.Max(0, Math.Min(3, col));
            _sortAsc = asc;
            if (_lv.IsHandleCreated) _lv.SetSortIcon(_sortCol, _sortAsc);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _lv.SetSortIcon(_sortCol, _sortAsc);
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            Activated?.Invoke(this);
        }

        public List<Entry> SelectedEntries()
        {
            var list = new List<Entry>();
            foreach (int i in _lv.SelectedIndices) if (i >= 0 && i < _view.Count) list.Add(_view[i]);
            return list;
        }

        public IEnumerable<string> SelectedPaths() => SelectedEntries().Select(e => e.FullPath);

        // ======================================================================= navigation

        /// <summary>Ouvre un dossier (ou un dossier dans une archive). False si le chemin est introuvable.</summary>
        public bool Navigate(string path, bool addHistory = true, string select = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string archive = null, inner = null;
            if (path != AddressBar.ThisPC)
            {
                try
                {
                    if (path.Length == 2 && path[1] == ':') path += "\\";
                    if (IsServer(path.TrimEnd('\\'))) path = path.TrimEnd('\\'); // GetFullPath refuse \\serveur
                    else
                    {
                        path = Path.GetFullPath(path);
                        if (path.EndsWith("\\") && Path.GetPathRoot(path) != path) path = path.TrimEnd('\\');
                    }
                }
                catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "S3bopus", MessageBoxButtons.OK, MessageBoxIcon.Warning); _address.Path = CurrentPath; return false; }

                if (!IsServer(path) && !Directory.Exists(path) && !Archives.Split(path, out archive, out inner))
                {
                    if (File.Exists(path)) { select = path; path = Path.GetDirectoryName(path); }
                    else
                    {
                        MessageBox.Show(FindForm(), Lang.T("Windows ne trouve pas « ", "Windows can't find “") + path + Lang.T(" ». Vérifiez l'orthographe et réessayez.", "”. Check the spelling and try again."), "S3bopus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _address.Path = CurrentPath;
                        return false;
                    }
                }
            }
            _archiveFile = archive;
            _archiveInner = inner;

            if (addHistory && CurrentPath != null && !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _back.Push(CurrentPath);
                _fwd.Clear();
            }
            CurrentPath = path;
            _address.Path = path;
            _filter.Text = "";
            _sizeCts?.Cancel();
            SetupWatcher();
            UpdateNavButtons();
            LoadEntries(select, false);
            PathChanged?.Invoke(this);
            return true;
        }

        void UpdateNavButtons()
        {
            _btnBack.Enabled = _back.Count > 0;
            _btnFwd.Enabled = _fwd.Count > 0;
            _btnUp.Enabled = !IsThisPC;
        }

        public void Back()
        {
            if (_back.Count == 0) return;
            _fwd.Push(CurrentPath);
            Navigate(_back.Pop(), false);
        }

        public void Forward()
        {
            if (_fwd.Count == 0) return;
            _back.Push(CurrentPath);
            Navigate(_fwd.Pop(), false);
        }

        /// <summary>\\serveur sans partage : on liste les partages (NetShareEnum).</summary>
        static bool IsServer(string p) => p.Length > 2 && p.StartsWith(@"\\") && p.IndexOf('\\', 2) < 0 && p[2] != '?';

        public void GoUp()
        {
            if (IsThisPC) return;
            string parent = Path.GetDirectoryName(CurrentPath);
            if (parent == null && CurrentPath.StartsWith(@"\\") && !IsServer(CurrentPath) && CurrentPath.IndexOf('\\', 2) > 0 && CurrentPath.IndexOf('\\', CurrentPath.IndexOf('\\', 2) + 1) < 0)
                parent = CurrentPath.Substring(0, CurrentPath.IndexOf('\\', 2));
            Navigate(string.IsNullOrEmpty(parent) ? AddressBar.ThisPC : parent, true, CurrentPath);
        }

        public void Reload(bool keepSelection)
        {
            if (CurrentPath == null) return;
            LoadEntries(null, keepSelection);
        }

        void SetupWatcher()
        {
            if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
            if (IsThisPC || IsArchive || IsServer(CurrentPath)) return;
            try
            {
                _watcher = new FileSystemWatcher(CurrentPath)
                {
                    IncludeSubdirectories = false,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                    SynchronizingObject = this
                };
                FileSystemEventHandler h = (s, e) => { DirSizes.Invalidate(e.FullPath); ScheduleReload(); };
                _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h;
                _watcher.Renamed += (s, e) => { DirSizes.Invalidate(e.OldFullPath); ScheduleReload(); };
                _watcher.Error += (s, e) => ScheduleReload();
                _watcher.EnableRaisingEvents = true;
            }
            catch { _watcher = null; }
        }

        void ScheduleReload()
        {
            if (!_reloadTimer.Enabled && !_editingLabel) _reloadTimer.Start();
        }

        // ======================================================================= chargement

        void LoadEntries(string selectPath, bool keepSelection)
        {
            int gen = ++_gen;
            string path = CurrentPath;
            bool hidden = ShowHidden;
            var keep = keepSelection ? SelectedPaths().ToList() : null;
            string focused = keepSelection && _lv.FocusedItem != null && _lv.FocusedItem.Index < _view.Count ? _view[_lv.FocusedItem.Index].FullPath : null;
            int topIndex = -1;
            if (keepSelection) try { topIndex = _lv.TopItem?.Index ?? -1; } catch { }

            string archive = _archiveFile, inner = _archiveInner;
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task.Run(() => path == AddressBar.ThisPC ? LoadDrives() : IsServer(path) ? LoadShares(path) : archive != null ? LoadArchive(archive, inner, path) : LoadFolder(path, hidden)).ContinueWith(t =>
            {
                if (gen != _gen || IsDisposed) return;
                if (t.IsFaulted)
                {
                    var ex = t.Exception.GetBaseException();
                    _all = new List<Entry>();
                    ApplyView(null, null);
                    if (!keepSelection)
                    {
                        MessageBox.Show(FindForm(), Lang.T("Impossible d'ouvrir « ", "Can't open “") + path + Lang.T(" »", "”") + "\n\n" + ex.Message, "S3bopus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        if (_back.Count > 0) Navigate(_back.Pop(), false);
                    }
                    return;
                }
                _all = t.Result;
                if (path == AddressBar.ThisPC)
                    foreach (var d in _all) d.Name = SysIcons.DisplayName(d.FullPath);

                if (_pendingSelect != null) { selectPath = _pendingSelect; _pendingSelect = null; }
                ApplyView(keep, selectPath ?? focused, topIndex);
                if (_pendingEdit) { _pendingEdit = false; BeginRename(); }
                if (AutoSizes && !keepSelection && !IsThisPC && !IsServer(path)) ComputeSizes(false);
            }, ui);
        }

        static List<Entry> LoadShares(string server)
        {
            var list = new List<Entry>();
            foreach (var n in Native.Shares(server))
                list.Add(new Entry { Name = n, FullPath = server + "\\" + n, IsDir = true, Size = -1, Attributes = FileAttributes.Directory });
            return list;
        }

        static List<Entry> LoadFolder(string path, bool showHidden)
        {
            var list = new List<Entry>();
            foreach (var f in Native.Enumerate(path))
            {
                if (!showHidden && (f.Attributes & FileAttributes.Hidden) != 0) continue;
                list.Add(new Entry
                {
                    Name = f.Name,
                    FullPath = Path.Combine(path, f.Name),
                    IsDir = f.IsDirectory,
                    Size = f.IsDirectory ? -1 : f.Size,
                    Modified = f.Modified,
                    Attributes = f.Attributes
                });
            }
            return list;
        }

        static List<Entry> LoadArchive(string file, string inner, string path)
        {
            var all = Archives.List(file);
            if (inner.Length > 0 && !all.Any(i => i.IsDir && i.Key == inner)) throw new DirectoryNotFoundException(path);
            return Archives.Children(all, inner).Select(i => new Entry
            {
                Name = i.Name,
                FullPath = Path.Combine(path, i.Name),
                IsDir = i.IsDir,
                Size = i.Size,
                Modified = i.Modified,
                Attributes = i.IsDir ? FileAttributes.Directory : FileAttributes.Normal
            }).ToList();
        }

        /// <summary>
        /// Chemins réels de la sélection : dans une archive, les éléments sont d'abord extraits dans un dossier temporaire.
        /// shortcut: l'extraction bloque l'interface (sablier) ; à passer en tâche de fond si les grosses archives gênent.
        /// </summary>
        public List<string> MaterializeSelection()
        {
            var sel = SelectedEntries();
            if (!IsArchive) return sel.Select(e => e.FullPath).ToList();
            string prefix = _archiveInner.Length == 0 ? "" : _archiveInner + "/";
            var old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try { return Archives.Extract(_archiveFile, sel.Select(e => prefix + e.Name).ToList(), Archives.NewTempDir()); }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), ex.Message, "S3bopus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return new List<string>();
            }
            finally { Cursor.Current = old; }
        }

        static List<Entry> LoadDrives()
        {
            var list = new List<Entry>();
            foreach (var d in DriveInfo.GetDrives())
            {
                var e = new Entry { Name = d.Name, FullPath = d.Name, IsDir = true, IsDrive = true, Size = -1, Attributes = FileAttributes.Directory };
                try
                {
                    if (d.IsReady) { e.Size = d.TotalSize; e.Free = d.AvailableFreeSpace; }
                }
                catch { }
                switch (d.DriveType)
                {
                    case DriveType.Fixed: e.Type = Lang.T("Disque local", "Local disk"); break;
                    case DriveType.Removable: e.Type = Lang.T("Lecteur amovible", "Removable drive"); break;
                    case DriveType.Network: e.Type = Lang.T("Lecteur réseau", "Network drive"); break;
                    case DriveType.CDRom: e.Type = Lang.T("Lecteur CD/DVD", "CD/DVD drive"); break;
                    default: e.Type = Lang.T("Lecteur", "Drive"); break;
                }
                list.Add(e);
            }
            return list;
        }

        void ApplyView(IList<string> keepSelected, string focusPath, int topIndex = -1)
        {
            IEnumerable<Entry> q = _all;
            string f = _filter.Text.Trim();
            if (f.Length > 0)
            {
                if (f.Contains("*") || f.Contains("?"))
                {
                    var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(f).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    q = q.Where(e => rx.IsMatch(e.Name));
                }
                else q = q.Where(e => e.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            var list = q.ToList();
            list.Sort(Compare);
            _view = list;

            _lv.BeginUpdate();
            try
            {
                _lv.SelectedIndices.Clear();
                _lv.VirtualListSize = _view.Count;
                var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _view.Count; i++) index[_view[i].FullPath] = i;
                if (keepSelected != null)
                    foreach (var p in keepSelected)
                        if (index.TryGetValue(p, out int i)) _lv.SelectedIndices.Add(i);
                if (topIndex >= 0 && topIndex < _view.Count) try { _lv.TopItem = _lv.Items[topIndex]; } catch { }
                if (focusPath != null && index.TryGetValue(focusPath, out int fi))
                {
                    _lv.FocusedItem = _lv.Items[fi];
                    if (keepSelected == null || keepSelected.Count == 0) _lv.SelectedIndices.Add(fi);
                    _lv.EnsureVisible(fi);
                }
                else if (_view.Count > 0 && keepSelected == null)
                {
                    _lv.FocusedItem = _lv.Items[0];
                    if (topIndex < 0) _lv.EnsureVisible(0);
                }
            }
            finally { _lv.EndUpdate(); }
            _lv.Invalidate();
            UpdateStatus();
        }

        int Compare(Entry a, Entry b)
        {
            if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
            int r = 0;
            switch (_sortCol)
            {
                case 1: r = a.Modified.CompareTo(b.Modified); break;
                case 2: r = string.Compare(a.Type, b.Type, StringComparison.CurrentCultureIgnoreCase); break;
                case 3: r = a.SortSize.CompareTo(b.SortSize); break;
            }
            if (r == 0) r = Native.StrCmpLogicalW(a.Name, b.Name);
            return _sortAsc ? r : -r;
        }

        void SortBy(int col)
        {
            if (col == _sortCol) _sortAsc = !_sortAsc;
            else { _sortCol = col; _sortAsc = col == 0 || col == 2; }
            _lv.SetSortIcon(_sortCol, _sortAsc);
            ApplyView(SelectedPaths().ToList(), _lv.FocusedItem != null && _lv.FocusedItem.Index < _view.Count ? _view[_lv.FocusedItem.Index].FullPath : null);
        }

        // ======================================================================= affichage

        void Lv_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _view.Count) { e.Item = new ListViewItem(""); return; }
            var en = _view[e.ItemIndex];
            if (en.Icon < 0) en.Icon = en.IsDrive ? SysIcons.IconForPath(en.FullPath) : SysIcons.IconFor(en.FullPath, en.IsDir);
            var it = new ListViewItem(en.Name, en.Icon);
            if (en.IsDrive)
            {
                it.SubItems.Add("");
                it.SubItems.Add(en.Type);
                it.SubItems.Add(en.Size >= 0 ? Fmt.Size(en.Free) + Lang.T(" libres sur ", " free of ") + Fmt.Size(en.Size) : "");
            }
            else
            {
                it.SubItems.Add(Fmt.Date(en.Modified));
                it.SubItems.Add(en.Type);
                string size = "";
                if (!en.IsDir) size = Fmt.Size(en.Size);
                else
                {
                    var ds = DirSizes.Get(en.FullPath);
                    if (ds != null) size = Fmt.Size(ds.Bytes) + (ds.Complete ? "" : " …");
                }
                it.SubItems.Add(size);
            }
            if (en.Hidden) it.ForeColor = Color.FromArgb(140, 140, 140);
            e.Item = it;
        }

        void Lv_SearchForVirtualItem(object sender, SearchForVirtualItemEventArgs e)
        {
            int start = Math.Max(0, e.StartIndex);
            for (int n = 0; n < _view.Count; n++)
            {
                int i = (start + n) % _view.Count;
                if (_view[i].Name.StartsWith(e.Text, StringComparison.CurrentCultureIgnoreCase)) { e.Index = i; return; }
            }
        }

        void UpdateStatus()
        {
            if (CurrentPath == null) return;
            var sel = SelectedEntries();
            string s = Fmt.Count(_view.Count, Lang.T("élément", "item"), Lang.T("éléments", "items"));
            if (_view.Count != _all.Count) s += Lang.T(" (filtrés sur ", " (filtered from ") + _all.Count + ")";

            IEnumerable<Entry> scope = sel.Count > 0 ? sel : _view;
            long total = 0; bool partial = false, any = false;
            foreach (var e in scope)
            {
                if (e.IsDrive) continue;
                if (e.IsDir)
                {
                    var ds = DirSizes.Get(e.FullPath);
                    if (ds == null || !ds.Complete) partial = true;
                    if (ds != null) { total += ds.Bytes; any = true; }
                }
                else { total += e.Size; any = true; }
            }
            if (sel.Count > 0)
            {
                s += "    " + Fmt.Count(sel.Count, Lang.T("élément sélectionné", "item selected"), Lang.T("éléments sélectionnés", "items selected"));
                if (any) s += "  " + (partial ? "≥ " : "") + Fmt.Size(total);
                if (sel.Count == 1 && sel[0].IsDir && !sel[0].IsDrive)
                {
                    var ds = DirSizes.Get(sel[0].FullPath);
                    if (ds != null) s += "  (" + Fmt.Count(ds.Files, Lang.T("fichier", "file"), Lang.T("fichiers", "files")) + ", " + Fmt.Count(ds.Folders, Lang.T("dossier", "folder"), Lang.T("dossiers", "folders")) + ")";
                }
            }
            else if (!IsThisPC && any) s += Lang.T("    Total : ", "    Total: ") + (partial ? "≥ " : "") + Fmt.Size(total);
            _statusLeft.Text = s;

            string free = "";
            if (!IsThisPC && Native.GetDiskFreeSpaceEx(CurrentPath, out ulong avail, out ulong size, out _))
                free = Fmt.Size((long)avail) + Lang.T(" libres sur ", " free of ") + Fmt.Size((long)size);
            _statusRight.Text = free;
        }

        // ======================================================================= tailles des dossiers

        /// <summary>Calcule la taille des dossiers sélectionnés (ou de tous les dossiers si aucun n'est sélectionné).</summary>
        public void ComputeSizes(bool force)
        {
            if (IsThisPC || IsArchive) return;
            var sel = SelectedEntries().Where(e => e.IsDir).ToList();
            var targets = (sel.Count > 0 ? sel : _view.Where(e => e.IsDir)).Select(e => e.FullPath).ToList();
            if (targets.Count == 0) return;
            _sizeCts?.Cancel();
            var cts = _sizeCts = new CancellationTokenSource();
            int gen = _gen;
            Task.Run(() => DirSizes.Compute(targets, force, cts.Token, (p, s) => _sizesDirty = true)).ContinueWith(t =>
            {
                if (IsDisposed || cts.IsCancellationRequested || gen != _gen) return;
                _sizesDirty = true;
                if (_sortCol == 3) ApplyView(SelectedPaths().ToList(), null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void RefreshSizesDisplay() => _sizesDirty = true;

        // ======================================================================= actions

        public void OpenSelected()
        {
            var sel = SelectedEntries();
            if (sel.Count == 0) return;
            var dirs = sel.Where(e => e.IsDir).ToList();
            if (dirs.Count > 0 && sel.Count == 1) { Navigate(dirs[0].FullPath); return; }
            if (IsArchive)
            {
                // Fichiers d'une archive : extraits dans un dossier temporaire puis ouverts par Windows
                var files = MaterializeSelection().Where(File.Exists).Take(20).ToList();
                if (files.Count == 1 && Archives.IsArchive(files[0])) { Navigate(files[0]); return; } // archive dans l'archive
                foreach (var f in files) Native.ShellOpen(FindForm().Handle, f);
                return;
            }
            if (sel.Count == 1 && Archives.IsArchive(sel[0].Name)) { Navigate(sel[0].FullPath); return; }
            foreach (var e in sel.Where(e => !e.IsDir).Take(20))
            {
                if (Path.GetExtension(e.Name).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string target = ResolveShortcut(e.FullPath);
                    if (target != null && Directory.Exists(target) && sel.Count == 1) { Navigate(target); return; }
                }
                Native.ShellOpen(FindForm().Handle, e.FullPath);
            }
        }

        static string ResolveShortcut(string lnk)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                string target = (string)sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
                Marshal.ReleaseComObject(sc);
                Marshal.ReleaseComObject(shell);
                return target;
            }
            catch { return null; }
        }

        public void BeginRename()
        {
            if (IsThisPC || IsArchive) return;
            int i = _lv.FocusedItem?.Index ?? -1;
            if (_lv.SelectedIndices.Count > 0 && !_lv.SelectedIndices.Contains(i)) i = _lv.SelectedIndices[0];
            if (i < 0 || i >= _view.Count) return;
            _lv.Focus();
            _lv.EnsureVisible(i);
            Native.SendMessage(_lv.Handle, Native.LVM_EDITLABELW, (IntPtr)i, IntPtr.Zero);
        }

        void Lv_BeforeLabelEdit(object sender, LabelEditEventArgs e)
        {
            if (IsThisPC || IsArchive || e.Item < 0 || e.Item >= _view.Count) { e.CancelEdit = true; return; }
            _editingLabel = true;
            var en = _view[e.Item];
            if (!en.IsDir)
            {
                int len = Path.GetFileNameWithoutExtension(en.Name).Length;
                BeginInvoke(new Action(() =>
                {
                    IntPtr edit = Native.SendMessage(_lv.Handle, Native.LVM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero);
                    if (edit != IntPtr.Zero) Native.SendMessage(edit, Native.EM_SETSEL, IntPtr.Zero, (IntPtr)len);
                }));
            }
        }

        void Lv_AfterLabelEdit(object sender, LabelEditEventArgs e)
        {
            _editingLabel = false;
            e.CancelEdit = true; // l'affichage vient des données (mode virtuel)
            if (e.Label == null || e.Item < 0 || e.Item >= _view.Count) return;
            var en = _view[e.Item];
            string newName = e.Label.Trim();
            if (newName.Length == 0 || newName == en.Name) return;
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(FindForm(), Lang.T("Un nom de fichier ne peut pas contenir les caractères suivants :", "A file name can't contain any of the following characters:") + "\n\\ / : * ? \" < > |", Lang.T("Renommer", "Rename"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string dst = Path.Combine(CurrentPath, newName);
            if (!Native.MoveFileEx(Native.LongPath(en.FullPath), Native.LongPath(dst), 0))
            {
                MessageBox.Show(FindForm(), Lang.T("Impossible de renommer « ", "Can't rename “") + en.Name + Lang.T(" » :", "”:") + "\n" + new Win32Exception(Marshal.GetLastWin32Error()).Message, Lang.T("Renommer", "Rename"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DirSizes.Invalidate(en.FullPath);
            en.Name = newName; en.FullPath = dst; en.Type = null; en.Icon = -1;
            _pendingSelect = dst;
            _lv.Invalidate();
            Reload(false);
        }

        public void NewFolder()
        {
            if (IsThisPC || IsArchive) return;
            string name = Lang.T("Nouveau dossier", "New folder"), path = Path.Combine(CurrentPath, name);
            for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = Path.Combine(CurrentPath, name + " (" + i + ")");
            if (!Native.CreateDirectory(Native.LongPath(path), IntPtr.Zero))
            {
                MessageBox.Show(FindForm(), new Win32Exception(Marshal.GetLastWin32Error()).Message, Lang.T("Nouveau dossier", "New folder"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _pendingSelect = path;
            _pendingEdit = true;
            LoadEntries(null, false);
        }

        public void DeleteSelected(bool permanent)
        {
            if (IsThisPC || IsArchive) return;
            var paths = SelectedPaths().ToList();
            if (paths.Count == 0) return;
            Native.ShellDelete(FindForm().Handle, paths, permanent);
            foreach (var p in paths) DirSizes.Invalidate(p);
            Reload(true);
        }

        public void ShowProperties()
        {
            var paths = IsArchive ? new List<string> { _archiveFile } : SelectedPaths().ToList();
            if (paths.Count == 0 && !IsThisPC) paths.Add(CurrentPath);
            foreach (var p in paths.Take(5)) Native.ShellOpen(FindForm().Handle, p, "properties");
        }

        public void SelectAll()
        {
            _lv.BeginUpdate();
            for (int i = 0; i < _view.Count; i++) _lv.SelectedIndices.Add(i);
            _lv.EndUpdate();
        }

        public void InvertSelection()
        {
            var sel = new HashSet<int>(_lv.SelectedIndices.Cast<int>());
            _lv.BeginUpdate();
            _lv.SelectedIndices.Clear();
            for (int i = 0; i < _view.Count; i++) if (!sel.Contains(i)) _lv.SelectedIndices.Add(i);
            _lv.EndUpdate();
        }

        // ---------------------------------------------------------------- presse-papiers (compatible Explorateur)

        public void CopyToClipboard(bool cut)
        {
            if (SelectedEntries().Count == 0 || IsThisPC) return;
            if (IsArchive) cut = false; // lecture seule : on copie les fichiers extraits
            var paths = MaterializeSelection().ToArray();
            if (paths.Length == 0) return;
            var sc = new StringCollection();
            sc.AddRange(paths);
            var data = new DataObject();
            data.SetFileDropList(sc);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            Clipboard.SetDataObject(data, true);
        }

        public void Paste()
        {
            if (IsThisPC || IsArchive || Queue == null) return;
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return;
                if (!data.GetDataPresent(DataFormats.FileDrop)) { PasteAsFile(); return; }
                var files = (string[])data.GetData(DataFormats.FileDrop);
                if (files == null || files.Length == 0) return;
                bool move = false;
                if (data.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
                {
                    int eff = BitConverter.ToInt32(ms.ToArray(), 0);
                    move = (eff & 2) != 0 && (eff & 1) == 0;
                }
                Queue.Enqueue(move ? JobKind.Move : JobKind.Copy, files, CurrentPath);
                if (move) Clipboard.Clear();
            }
            catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Lang.T("Coller", "Paste"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        /// <summary>Texte ou image du presse-papiers enregistré comme fichier (.txt / .jpg), puis renommage proposé.</summary>
        void PasteAsFile()
        {
            string path;
            // Le texte d'abord : Word ou Excel ajoutent aussi une image de ce qu'on a copié
            if (Clipboard.ContainsText())
            {
                path = UniqueName(Lang.T("Texte collé", "Pasted text"), ".txt");
                File.WriteAllText(path, Clipboard.GetText(), System.Text.Encoding.UTF8);
            }
            else if (Clipboard.ContainsImage())
            {
                path = UniqueName(Lang.T("Image collée", "Pasted image"), ".jpg");
                using (var img = Clipboard.GetImage())
                using (var flat = new Bitmap(img.Width, img.Height))
                {
                    // Le JPEG n'a pas de transparence : fond blanc plutôt que noir
                    using (var g = Graphics.FromImage(flat)) { g.Clear(Color.White); g.DrawImage(img, 0, 0, img.Width, img.Height); }
                    var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                    using (var p = new System.Drawing.Imaging.EncoderParameters(1))
                    {
                        p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                        flat.Save(path, jpeg, p);
                    }
                }
            }
            else return;
            _pendingSelect = path;
            _pendingEdit = true;
            LoadEntries(null, false);
        }

        string UniqueName(string name, string ext)
        {
            string path = Path.Combine(CurrentPath, name + ext);
            for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(CurrentPath, name + " (" + i + ")" + ext);
            return path;
        }

        // ======================================================================= menu contextuel Windows

        void Lv_ShellMenu(Point screenPt, bool keyboard)
        {
            Activated?.Invoke(this);
            if (IsArchive) return; // le menu Windows ne connaît pas les chemins dans une archive
            var client = _lv.PointToClient(screenPt);
            var hit = keyboard ? null : _lv.HitTest(client).Item;
            var sel = SelectedPaths().ToList();
            var owner = FindForm();
            if ((keyboard && sel.Count > 0) || (hit != null && sel.Count > 0))
                _shellMenu.ShowForItems(owner, IsThisPC ? null : CurrentPath, sel, screenPt);
            else if (!IsThisPC)
                _shellMenu.ShowForBackground(owner, CurrentPath, screenPt);
        }

        bool HandleShellVerb(string verb)
        {
            switch (verb)
            {
                case "rename": BeginRename(); return true;
                case "paste": Paste(); return true;
                case "open":
                case "explore":
                    var sel = SelectedEntries();
                    if (sel.Count == 1 && sel[0].IsDir) { Navigate(sel[0].FullPath); return true; }
                    return false;
            }
            return false;
        }

        // ======================================================================= clavier / souris

        void Lv_KeyDown(object sender, KeyEventArgs e)
        {
            bool handled = true;
            switch (e.KeyData)
            {
                case Keys.Enter: OpenSelected(); break;
                case Keys.Back:
                case Keys.Alt | Keys.Up: GoUp(); break;
                case Keys.Alt | Keys.Left: Back(); break;
                case Keys.Alt | Keys.Right: Forward(); break;
                case Keys.Delete: DeleteSelected(false); break;
                case Keys.Shift | Keys.Delete: DeleteSelected(true); break;
                case Keys.Control | Keys.A: SelectAll(); break;
                case Keys.Control | Keys.C: CopyToClipboard(false); break;
                case Keys.Control | Keys.X: CopyToClipboard(true); break;
                case Keys.Control | Keys.V: Paste(); break;
                case Keys.Control | Keys.R: Reload(true); break;
                case Keys.Control | Keys.F: FocusFilter(); break;
                case Keys.Control | Keys.L:
                case Keys.Alt | Keys.D:
                case Keys.F4: FocusAddress(); break;
                case Keys.Alt | Keys.Enter: ShowProperties(); break;
                case Keys.Control | Keys.Shift | Keys.N: NewFolder(); break;
                case Keys.F2: BeginRename(); break;
                default: handled = false; break;
            }
            if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
        }

        void Lv_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.XButton1) Back();
            else if (e.Button == MouseButtons.XButton2) Forward();
            else if (e.Button == MouseButtons.Middle)
            {
                // Clic molette sur un dossier : l'ouvrir dans l'autre panneau
                var hit = _lv.HitTest(e.Location).Item;
                var other = OtherPane?.Invoke();
                if (hit != null && other != null && hit.Index < _view.Count && _view[hit.Index].IsDir) other.Navigate(_view[hit.Index].FullPath);
            }
        }

        // ======================================================================= glisser-déposer

        void Lv_ItemDrag(object sender, ItemDragEventArgs e)
        {
            if (IsThisPC || IsArchive) return;
            var paths = SelectedPaths().ToArray();
            if (paths.Length == 0) return;
            var data = new DataObject(DataFormats.FileDrop, paths);
            _lv.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            _lv.SetDropHighlight(-1);
        }

        string DropTarget(int x, int y, out int itemIndex)
        {
            itemIndex = -1;
            if (IsArchive) return null;
            var hit = _lv.HitTest(_lv.PointToClient(new Point(x, y))).Item;
            if (hit != null && hit.Index < _view.Count && _view[hit.Index].IsDir)
            {
                itemIndex = hit.Index;
                return _view[hit.Index].FullPath;
            }
            return IsThisPC ? null : CurrentPath;
        }

        void Lv_DragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { _lv.SetDropHighlight(-1); return; }
            string target = DropTarget(e.X, e.Y, out int idx);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (target == null || files == null || files.Length == 0) { _lv.SetDropHighlight(-1); return; }

            // Interdit : déposer un dossier sur lui-même ou dans son propre dossier
            if (files.Any(f => string.Equals(f.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                || files.All(f => string.Equals(Path.GetDirectoryName(f)?.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            { _lv.SetDropHighlight(-1); return; }

            _lv.SetDropHighlight(idx);
            bool sameDrive = string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
            DragDropEffects want;
            if ((e.KeyState & 8) != 0) want = DragDropEffects.Copy;        // Ctrl
            else if ((e.KeyState & 4) != 0) want = DragDropEffects.Move;   // Maj
            else want = sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
            if ((e.AllowedEffect & want) == 0) want = (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Effect = want;
        }

        void Lv_DragDrop(object sender, DragEventArgs e)
        {
            _lv.SetDropHighlight(-1);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            string target = DropTarget(e.X, e.Y, out _);
            if (files == null || target == null || Queue == null || e.Effect == DragDropEffects.None) return;
            Queue.Enqueue(e.Effect == DragDropEffects.Move ? JobKind.Move : JobKind.Copy, files, target);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sizeCts?.Cancel();
                _watcher?.Dispose();
                _reloadTimer.Dispose();
                _uiTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Petit cadre fin autour d'une TextBox (aspect zone de recherche de l'Explorateur).</summary>
    internal sealed class BorderBox : Panel
    {
        TextBox _inner;
        public BorderBox()
        {
            BackColor = Ui.Back;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
        public TextBox Inner
        {
            get => _inner;
            set
            {
                _inner = value;
                Controls.Add(value);
                value.GotFocus += (s, e) => Invalidate();
                value.LostFocus += (s, e) => Invalidate();
                PerformLayout();
            }
        }
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_inner != null)
            {
                int h = _inner.PreferredHeight;
                _inner.SetBounds(Ui.S(6), (Height - h) / 2, Width - Ui.S(10), h);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            bool focus = _inner != null && _inner.Focused;
            using (var p = new Pen(focus ? Ui.Accent : Ui.Border))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _inner?.Focus(); }
    }
}
