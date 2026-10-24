import AppKit
import UniformTypeIdentifiers

extension String {
    var lastName: String { (self as NSString).lastPathComponent }
    var parentPath: String { (self as NSString).deletingLastPathComponent }
    func joined(_ name: String) -> String { (self as NSString).appendingPathComponent(name) }
}

struct SysError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
    static func posix(_ code: Int32 = errno) -> SysError { SysError(message: String(cString: strerror(code))) }
}

enum Sys {
    struct Stat {
        var isDir: Bool, isLink: Bool
        var size: Int64
        var mtime: Date
        var dev: Int32
    }

    /// lstat : ne suit pas les liens symboliques.
    static func stat(_ p: String) -> Stat? {
        var st = Darwin.stat()
        guard lstat(p, &st) == 0 else { return nil }
        let fmt = st.st_mode & S_IFMT
        let t = Double(st.st_mtimespec.tv_sec) + Double(st.st_mtimespec.tv_nsec) / 1e9
        return Stat(isDir: fmt == S_IFDIR, isLink: fmt == S_IFLNK, size: Int64(st.st_size), mtime: Date(timeIntervalSince1970: t), dev: Int32(st.st_dev))
    }

    static func exists(_ p: String) -> Bool { stat(p) != nil }

    /// Dossier (en suivant les liens symboliques).
    static func isDirectory(_ p: String) -> Bool {
        var d: ObjCBool = false
        return FileManager.default.fileExists(atPath: p, isDirectory: &d) && d.boolValue
    }

    static func unique(dir: String, name: String, copySuffix: Bool) -> String {
        let ns = name as NSString
        let base = ns.deletingPathExtension, ext = ns.pathExtension
        let b = copySuffix ? base + Lang.T(" - Copie", " - Copy") : base
        func make(_ suffix: String) -> String { dir.joined(b + suffix + (ext.isEmpty ? "" : "." + ext)) }
        var candidate = make("")
        var i = 2
        while exists(candidate) { candidate = make(" (\(i))"); i += 1 }
        return candidate
    }

    static func open(_ path: String) { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }

    static func reveal(_ paths: [String]) {
        NSWorkspace.shared.activateFileViewerSelecting(paths.map { URL(fileURLWithPath: $0) })
    }

    static func openTerminal(at dir: String) {
        let term = URL(fileURLWithPath: "/System/Applications/Utilities/Terminal.app")
        NSWorkspace.shared.open([URL(fileURLWithPath: dir, isDirectory: true)], withApplicationAt: term, configuration: NSWorkspace.OpenConfiguration())
    }

    static func getInfo(_ path: String) {
        let esc = path.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let src = "tell application \"Finder\"\nopen information window of (POSIX file \"\(esc)\" as alias)\nactivate\nend tell"
        var err: NSDictionary?
        NSAppleScript(source: src)?.executeAndReturnError(&err)
    }
}

/// Entrée d'un dossier affiché dans un panneau.
final class FileItem {
    let name: String
    let path: String
    let isDir: Bool        // dossier navigable (les paquets .app sont des fichiers)
    let isPackage: Bool
    let isLink: Bool
    let isHidden: Bool
    let size: Int64
    let date: Date
    lazy var kind: String = Fmt.typeName(name, isDir: isDir)

    init(name: String, path: String, isDir: Bool, isPackage: Bool, isLink: Bool, isHidden: Bool, size: Int64, date: Date) {
        self.name = name; self.path = path; self.isDir = isDir; self.isPackage = isPackage
        self.isLink = isLink; self.isHidden = isHidden; self.size = size; self.date = date
    }

    var hasFolderSize: Bool { isDir || isPackage }

    static func list(_ dir: String) -> [FileItem]? {
        let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey, .contentModificationDateKey, .isHiddenKey, .isPackageKey]
        guard let urls = try? FileManager.default.contentsOfDirectory(at: URL(fileURLWithPath: dir, isDirectory: true), includingPropertiesForKeys: keys, options: []) else { return nil }
        return urls.map { u in
            let v = try? u.resourceValues(forKeys: Set(keys))
            let name = u.lastPathComponent
            let path = dir.joined(name)
            let link = v?.isSymbolicLink ?? false
            let pkg = v?.isPackage ?? false
            let dirFlag = link ? Sys.isDirectory(path) : (v?.isDirectory ?? false) && !pkg
            return FileItem(name: name, path: path, isDir: dirFlag, isPackage: pkg, isLink: link,
                            isHidden: name.hasPrefix(".") || (v?.isHidden ?? false),
                            size: dirFlag || pkg ? -1 : Int64(v?.fileSize ?? 0),
                            date: v?.contentModificationDate ?? .distantPast)
        }
    }
}

enum Icons {
    private static var cache: [String: NSImage] = [:]

    static func icon(for item: FileItem) -> NSImage {
        if item.isPackage { return NSWorkspace.shared.icon(forFile: item.path) }
        let key = item.isDir ? "/dir" : (item.name as NSString).pathExtension.lowercased()
        if let i = cache[key] { return i }
        let type: UTType = item.isDir ? .folder : (UTType(filenameExtension: key) ?? .data)
        let img = NSWorkspace.shared.icon(for: type)
        cache[key] = img
        return img
    }
}

enum Dialogs {
    static func message(_ title: String, _ text: String, style: NSAlert.Style = .informational) {
        let a = NSAlert()
        a.alertStyle = style
        a.messageText = title
        a.informativeText = text
        a.runModal()
    }

    static func confirm(_ title: String, _ text: String, ok: String, destructive: Bool = false) -> Bool {
        let a = NSAlert()
        a.alertStyle = destructive ? .warning : .informational
        a.messageText = title
        a.informativeText = text
        a.addButton(withTitle: ok)
        a.addButton(withTitle: Lang.T("Annuler", "Cancel"))
        return a.runModal() == .alertFirstButtonReturn
    }

    /// Conflit de transfert ; à appeler sur le thread principal.
    static func conflict(_ job: TransferJob, _ info: ConflictInfo) -> ConflictChoice {
        func line(_ label: String, _ size: Int64, _ date: Date) -> String { "\(label) : \(Fmt.size(size)) — \(Fmt.date(date))" }
        let a = NSAlert()
        a.alertStyle = .warning
        a.messageText = Lang.T("« \(info.destination.lastName) » existe déjà", "“\(info.destination.lastName)” already exists")
        a.informativeText = info.destination.parentPath + "\n\n"
            + line(Lang.T("Existant", "Existing"), info.destSize, info.destDate) + "\n"
            + line(Lang.T("Nouveau", "New"), info.sourceSize, info.sourceDate)
        for t in [Lang.T("Remplacer", "Replace"), Lang.T("Conserver les deux", "Keep both"), Lang.T("Ignorer", "Skip"),
                  Lang.T("Remplacer si plus récent", "Replace if newer"), Lang.T("Annuler le transfert", "Cancel transfer")] { a.addButton(withTitle: t) }
        a.showsSuppressionButton = true
        a.suppressionButton?.title = Lang.T("Appliquer à tous", "Apply to all")
        let r = a.runModal().rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue
        info.applyToAll = a.suppressionButton?.state == .on
        switch r {
        case 0: return .replace
        case 1: return .keepBoth
        case 2: return .skip
        case 3: return .replaceIfNewer
        default: return .cancelJob
        }
    }
}
