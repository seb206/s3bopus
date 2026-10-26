using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace S3bopus
{
    /// <summary>Appels Win32 utilisés par S3bopus.</summary>
    internal static class Native
    {
        // ---------- Thèmes / fenêtres ----------
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        public static extern IntPtr SendMessageLVItem(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEM lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        public static extern IntPtr SendMessageHDItem(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEM lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        public const int WM_CONTEXTMENU = 0x007B;
        public const int WM_CHANGEUISTATE = 0x0127;
        public const int WM_INITMENUPOPUP = 0x0117;
        public const int WM_DRAWITEM = 0x002B;
        public const int WM_MEASUREITEM = 0x002C;
        public const int WM_MENUCHAR = 0x0120;
        public const int WM_MENUSELECT = 0x011F;
        public const int EM_SETSEL = 0x00B1;
        public const int EM_SETCUEBANNER = 0x1501;
        public const int LVM_FIRST = 0x1000;
        public const int LVM_SETIMAGELIST = LVM_FIRST + 3;
        public const int LVM_GETHEADER = LVM_FIRST + 31;
        public const int LVM_SETITEMSTATE = LVM_FIRST + 43;
        public const int LVM_GETEDITCONTROL = LVM_FIRST + 24;
        public const int LVM_EDITLABELW = LVM_FIRST + 118;
        public const int LVSIL_SMALL = 1;
        public const int LVIS_DROPHILITED = 0x0008;
        public const int LVS_SHAREIMAGELISTS = 0x0040;
        public const int HDM_GETITEMW = 0x1200 + 11;
        public const int HDM_SETITEMW = 0x1200 + 12;
        public const int HDI_FORMAT = 0x0004;
        public const int HDF_SORTUP = 0x0400;
        public const int HDF_SORTDOWN = 0x0200;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct LVITEM
        {
            public int mask, iItem, iSubItem, state, stateMask;
            public IntPtr pszText;
            public int cchTextMax, iImage;
            public IntPtr lParam;
            public int iIndent, iGroupId, cColumns;
            public IntPtr puColumns, piColFmt;
            public int iGroup;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct HDITEM
        {
            public int mask, cxy;
            public IntPtr pszText, hbm;
            public int cchTextMax, fmt;
            public IntPtr lParam;
            public int iImage, iOrder;
            public uint type;
            public IntPtr pvFilter;
            public uint state;
        }

        // ---------- Icônes / infos shell ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        public const uint SHGFI_ICON = 0x100, SHGFI_DISPLAYNAME = 0x200, SHGFI_TYPENAME = 0x400,
            SHGFI_SYSICONINDEX = 0x4000, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("comctl32.dll")]
        public static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern int StrCmpLogicalW(string a, string b);

        [DllImport("shell32.dll")]
        public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        public static string KnownFolder(Guid id)
        {
            try
            {
                if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr p) == 0)
                {
                    string s = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    return s;
                }
            }
            catch { }
            return null;
        }

        public static readonly Guid FOLDERID_Downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        // ---------- ShellExecute ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string lpVerb, lpFile, lpParameters, lpDirectory;
            public int nShow;
            public IntPtr hInstApp, lpIDList;
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon, hProcess;
        }

        public const uint SEE_MASK_INVOKEIDLIST = 0x0C, SEE_MASK_ASYNCOK = 0x100000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        /// <summary>Ouvre un élément exactement comme un double-clic dans l'Explorateur.</summary>
        public static void ShellOpen(IntPtr owner, string path, string verb = null)
        {
            var sei = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO)),
                hwnd = owner,
                lpVerb = verb,
                lpFile = path,
                lpDirectory = Path.GetDirectoryName(path),
                nShow = 1,
                fMask = verb == "properties" ? SEE_MASK_INVOKEIDLIST : 0
            };
            ShellExecuteEx(ref sei);
        }

        // ---------- Suppression (corbeille) ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT64
        {
            public IntPtr hwnd; public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags; public int fAnyOperationsAborted;
            public IntPtr hNameMappings; public string lpszProgressTitle;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        struct SHFILEOPSTRUCT32
        {
            public IntPtr hwnd; public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags; public int fAnyOperationsAborted;
            public IntPtr hNameMappings; public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 op);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 op);

        const uint FO_DELETE = 3;
        const ushort FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>Supprime avec la boîte de dialogue standard de Windows (corbeille sauf si permanent).</summary>
        public static void ShellDelete(IntPtr owner, IEnumerable<string> paths, bool permanent)
        {
            string from = string.Join("\0", paths) + "\0";
            ushort flags = permanent ? (ushort)0 : (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING);
            if (IntPtr.Size == 8)
            {
                var op = new SHFILEOPSTRUCT64 { hwnd = owner, wFunc = FO_DELETE, pFrom = from, fFlags = flags };
                SHFileOperation64(ref op);
            }
            else
            {
                var op = new SHFILEOPSTRUCT32 { hwnd = owner, wFunc = FO_DELETE, pFrom = from, fFlags = flags };
                SHFileOperation32(ref op);
            }
        }

        // ---------- Système de fichiers rapide ----------
        public const int ERROR_ALREADY_EXISTS = 183, ERROR_REQUEST_ABORTED = 1235;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_DATA
        {
            public FileAttributes dwFileAttributes;
            public FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileEx(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATA lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);
        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr hFindFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateDirectory(string lpPathName, IntPtr lpSecurityAttributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool RemoveDirectory(string lpPathName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DeleteFile(string lpFileName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetFileAttributes(string lpFileName, uint attrs);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFileAttributes(string lpFileName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool MoveFileEx(string src, string dst, uint flags);
        public const uint MOVEFILE_REPLACE_EXISTING = 0x1;
        public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

        public delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize,
            long streamBytesTransferred, uint dwStreamNumber, uint dwCallbackReason, IntPtr hSourceFile, IntPtr hDestinationFile, IntPtr lpData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CopyFileEx(string src, string dst, CopyProgressRoutine progress, IntPtr data, ref int cancel, uint flags);
        public const uint COPY_FILE_NO_BUFFERING = 0x1000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeForUser, out ulong total, out ulong totalFree);

        /// <summary>Préfixe \\?\ pour dépasser la limite MAX_PATH.</summary>
        public static string LongPath(string path)
        {
            if (path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        public struct FindEntry
        {
            public string Name;
            public FileAttributes Attributes;
            public long Size;
            public DateTime Modified;
            public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
        }

        public static long ToLong(uint high, uint low) => ((long)high << 32) | low;
        public static DateTime ToDate(FILETIME ft)
        {
            long v = ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
            try { return DateTime.FromFileTime(v); } catch { return DateTime.MinValue; }
        }

        /// <summary>Énumère un dossier via FindFirstFileEx (bien plus rapide que DirectoryInfo).</summary>
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        static extern int NetShareEnum(string server, int level, out IntPtr buf, int max, out int read, out int total, ref int resume);
        [DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr buf);

        /// <summary>Partages disques d'un serveur (\\serveur) ; lève Win32Exception si injoignable.</summary>
        public static List<string> Shares(string server)
        {
            int resume = 0, err = NetShareEnum(server, 1, out IntPtr buf, -1, out int read, out int _, ref resume);
            if (err != 0 && err != 234) throw new System.ComponentModel.Win32Exception(err);
            var list = new List<string>();
            try
            {
                for (int i = 0; i < read; i++)
                {
                    IntPtr item = buf + i * (IntPtr.Size * 2 + 8); // SHARE_INFO_1 : name, type, remark
                    uint type = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                    if ((type & 0xFF) == 0) list.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item))); // STYPE_DISKTREE
                }
            }
            finally { NetApiBufferFree(buf); }
            return list;
        }

        public static IEnumerable<FindEntry> Enumerate(string dir)
        {
            string pattern = LongPath(Path.Combine(dir, "*"));
            IntPtr h = FindFirstFileEx(pattern, 1 /*Basic*/, out WIN32_FIND_DATA fd, 0, IntPtr.Zero, 2 /*LARGE_FETCH*/);
            if (h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 2 || err == 18) yield break; // vide
                throw new System.ComponentModel.Win32Exception(err);
            }
            try
            {
                do
                {
                    if (fd.cFileName == "." || fd.cFileName == "..") continue;
                    yield return new FindEntry
                    {
                        Name = fd.cFileName,
                        Attributes = fd.dwFileAttributes,
                        Size = ToLong(fd.nFileSizeHigh, fd.nFileSizeLow),
                        Modified = ToDate(fd.ftLastWriteTime)
                    };
                } while (FindNextFile(h, out fd));
            }
            finally { FindClose(h); }
        }

        // ---------- Menu contextuel natif ----------
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
        public const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
        [DllImport("shell32.dll")]
        public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
        [DllImport("shell32.dll")]
        public static extern int SHBindToObject(IntPtr psf, IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out IntPtr ppidl, ref uint attrs);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr ppenum);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cchMax);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cchMax);
        [PreserveSig] int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cchMax);
        [PreserveSig] int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(int uMsg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }
}
