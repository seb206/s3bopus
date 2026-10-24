using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace S3bopus
{
    /// <summary>Un côté de la fenêtre : une barre d'onglets et un FilePane par onglet (comme Directory Opus).</summary>
    internal sealed class PaneTabs : DockPanel
    {
        readonly StackPanel _strip;
        readonly Panel _host = new Panel();
        readonly Button _add;
        readonly List<FilePane> _panes = new List<FilePane>();
        readonly Func<FilePane> _factory;

        public FilePane Current { get; private set; }
        public IReadOnlyList<FilePane> Panes => _panes;
        public event Action<FilePane> CurrentChanged;

        public PaneTabs(Func<FilePane> factory)
        {
            _factory = factory;
            _strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(2, 2, 2, 0) };
            _add = new Button { Content = "+", Padding = new Thickness(8, 0), Background = Brushes.Transparent };
            ToolTip.SetTip(_add, Lang.T("Nouvel onglet (Ctrl+N)", "New tab (Ctrl+N)"));
            _add.Click += (s, e) => NewTab(Current?.CurrentPath).FocusList();
            var scroll = new ScrollViewer { Content = _strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            SetDock(scroll, Dock.Top);
            Children.Add(scroll);
            Children.Add(_host);
        }

        public FilePane NewTab(string path)
        {
            var p = _factory();
            p.IsVisible = false;
            p.PathChanged += _ => UpdateStrip();
            _panes.Insert(Current == null ? _panes.Count : _panes.IndexOf(Current) + 1, p);
            _host.Children.Add(p);
            if (path != null) p.Navigate(path);
            Select(p);
            return p;
        }

        public void Select(FilePane p)
        {
            if (p == null) return;
            foreach (var x in _panes) x.IsVisible = x == p;
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
            _host.Children.Remove(p);
            if (Current == p) { Current = null; Select(_panes[Math.Min(i, _panes.Count - 1)]); }
            else UpdateStrip();
        }

        public void Cycle(int delta)
        {
            if (_panes.Count < 2) return;
            Select(_panes[(_panes.IndexOf(Current) + delta + _panes.Count) % _panes.Count]);
            Current.FocusList();
        }

        static string Title(FilePane p)
        {
            if (p.CurrentPath == null) return "…";
            string name = Path.GetFileName(p.CurrentPath);
            name = string.IsNullOrEmpty(name) ? p.CurrentPath : name;
            return name.Length > 28 ? name.Substring(0, 27) + "…" : name;
        }

        void UpdateStrip()
        {
            _strip.Children.Clear();
            foreach (var p in _panes)
            {
                var pane = p;
                var b = new ToggleButton { Content = Title(p), IsChecked = p == Current, Padding = new Thickness(10, 2), FontSize = 12 };
                ToolTip.SetTip(b, p.CurrentPath);
                b.Click += (s, e) => { Select(pane); pane.FocusList(); };
                // Clic molette : fermer l'onglet
                b.PointerReleased += (s, e) => { if (e.InitialPressMouseButton == MouseButton.Middle) CloseTab(pane); };
                b.ContextMenu = TabMenu(pane);
                _strip.Children.Add(b);
            }
            _strip.Children.Add(_add);
        }

        ContextMenu TabMenu(FilePane p)
        {
            var m = new ContextMenu();
            void I(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (s, e) => a(); m.Items.Add(mi); }
            I(Lang.T("Nouvel onglet", "New tab"), () => NewTab(p.CurrentPath).FocusList());
            I(Lang.T("Fermer l'onglet (Ctrl+W)", "Close tab (Ctrl+W)"), () => CloseTab(p), _panes.Count > 1);
            I(Lang.T("Fermer les autres onglets", "Close other tabs"), () => { foreach (var x in _panes.Where(x => x != p).ToList()) CloseTab(x); }, _panes.Count > 1);
            return m;
        }
    }
}
