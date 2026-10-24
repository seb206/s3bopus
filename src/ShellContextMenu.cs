using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace S3bopus
{
    /// <summary>
    /// Affiche le vrai menu contextuel de Windows (celui de l'Explorateur) pour des fichiers
    /// ou pour le fond d'un dossier. Les extensions shell (7-Zip, antivirus, Envoyer vers...) y figurent.
    /// </summary>
    internal sealed class ShellContextMenu : NativeWindow
    {
        const uint CMD_FIRST = 1, CMD_LAST = 0x7FFF;
        const uint CMF_NORMAL = 0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
        const uint GCS_VERBW = 4;
        const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000,
                   CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;

        static Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        static Guid IID_IContextMenu = new Guid("000214e4-0000-0000-c000-000000000046");

        IContextMenu2 _cm2;
        IContextMenu3 _cm3;

        /// <summary>Callback pour intercepter certains verbes (rename, paste...). Retourne true si traité.</summary>
        public Func<string, bool> VerbHandler;

        public void ShowForItems(IWin32Window owner, string folder, IList<string> paths, System.Drawing.Point screenPt)
        {
            var pidls = new List<IntPtr>();
            IntPtr parentPtr = IntPtr.Zero;
            try
            {
                var relative = new List<IntPtr>();
                foreach (var p in paths)
                {
                    if (Native.SHParseDisplayName(p, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero) continue;
                    pidls.Add(pidl);
                    if (Native.SHBindToParent(pidl, ref IID_IShellFolder, out IntPtr parent, out IntPtr last) == 0)
                    {
                        if (parentPtr == IntPtr.Zero) parentPtr = parent; else Marshal.Release(parent);
                        relative.Add(last);
                    }
                }
                if (parentPtr == IntPtr.Zero || relative.Count == 0) return;
                var sf = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
                IntPtr cmPtr;
                int hr = sf.GetUIObjectOf(owner.Handle, (uint)relative.Count, relative.ToArray(), ref IID_IContextMenu, IntPtr.Zero, out cmPtr);
                Marshal.ReleaseComObject(sf);
                if (hr != 0 || cmPtr == IntPtr.Zero) return;
                Run(owner, cmPtr, folder, screenPt, CMF_EXPLORE | CMF_CANRENAME);
            }
            finally
            {
                if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
                foreach (var p in pidls) Native.ILFree(p);
            }
        }

        public void ShowForBackground(IWin32Window owner, string folder, System.Drawing.Point screenPt)
        {
            IntPtr pidl = IntPtr.Zero, sfPtr = IntPtr.Zero;
            try
            {
                if (Native.SHParseDisplayName(folder, IntPtr.Zero, out pidl, 0, out _) != 0) return;
                if (Native.SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, ref IID_IShellFolder, out sfPtr) != 0) return;
                var sf = (IShellFolder)Marshal.GetObjectForIUnknown(sfPtr);
                int hr = sf.CreateViewObject(owner.Handle, ref IID_IContextMenu, out IntPtr cmPtr);
                Marshal.ReleaseComObject(sf);
                if (hr != 0 || cmPtr == IntPtr.Zero) return;
                Run(owner, cmPtr, folder, screenPt, CMF_EXPLORE);
            }
            finally
            {
                if (sfPtr != IntPtr.Zero) Marshal.Release(sfPtr);
                if (pidl != IntPtr.Zero) Native.ILFree(pidl);
            }
        }

        void Run(IWin32Window owner, IntPtr cmPtr, string folder, System.Drawing.Point pt, uint extraFlags)
        {
            object obj = Marshal.GetObjectForIUnknown(cmPtr);
            Marshal.Release(cmPtr);
            var cm = obj as IContextMenu;
            _cm2 = obj as IContextMenu2;
            _cm3 = obj as IContextMenu3;
            IntPtr menu = Native.CreatePopupMenu();
            try
            {
                uint flags = CMF_NORMAL | extraFlags;
                if ((Control.ModifierKeys & Keys.Shift) != 0) flags |= CMF_EXTENDEDVERBS;
                cm.QueryContextMenu(menu, 0, CMD_FIRST, CMD_LAST, flags);

                AssignHandle(owner.Handle);
                int cmd;
                try { cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, pt.X, pt.Y, owner.Handle, IntPtr.Zero); }
                finally { ReleaseHandle(); }
                if (cmd <= 0) return;

                uint id = (uint)cmd - CMD_FIRST;
                string verb = null;
                try
                {
                    var sb = new StringBuilder(256);
                    if (cm.GetCommandString((UIntPtr)id, GCS_VERBW, IntPtr.Zero, sb, sb.Capacity) == 0) verb = sb.ToString();
                }
                catch { }
                if (verb != null && VerbHandler != null && VerbHandler(verb.ToLowerInvariant())) return;

                var mods = Control.ModifierKeys;
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX)),
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                            | ((mods & Keys.Control) != 0 ? CMIC_MASK_CONTROL_DOWN : 0)
                            | ((mods & Keys.Shift) != 0 ? CMIC_MASK_SHIFT_DOWN : 0),
                    hwnd = owner.Handle,
                    lpVerb = (IntPtr)id,
                    lpVerbW = (IntPtr)id,
                    lpDirectory = folder,
                    lpDirectoryW = folder,
                    nShow = 1,
                    ptInvoke = new POINT { X = pt.X, Y = pt.Y }
                };
                cm.InvokeCommand(ref info);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, ex.Message, "S3bopus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Native.DestroyMenu(menu);
                _cm2 = null; _cm3 = null;
                Marshal.ReleaseComObject(obj);
            }
        }

        protected override void WndProc(ref Message m)
        {
            // Sous-menus dynamiques (Envoyer vers, Ouvrir avec, Nouveau...) : ils ont besoin de ces messages.
            if (_cm3 != null && (m.Msg == Native.WM_MENUCHAR || m.Msg == Native.WM_INITMENUPOPUP ||
                                 m.Msg == Native.WM_DRAWITEM || m.Msg == Native.WM_MEASUREITEM))
            {
                if (_cm3.HandleMenuMsg2(m.Msg, m.WParam, m.LParam, out IntPtr result) == 0)
                {
                    m.Result = m.Msg == Native.WM_MENUCHAR ? result : (m.Msg == Native.WM_INITMENUPOPUP ? IntPtr.Zero : (IntPtr)1);
                    return;
                }
            }
            else if (_cm2 != null && (m.Msg == Native.WM_INITMENUPOPUP || m.Msg == Native.WM_DRAWITEM || m.Msg == Native.WM_MEASUREITEM))
            {
                if (_cm2.HandleMenuMsg(m.Msg, m.WParam, m.LParam) == 0)
                {
                    m.Result = m.Msg == Native.WM_INITMENUPOPUP ? IntPtr.Zero : (IntPtr)1;
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }
}
