import Foundation

/// Langue de l'interface : Lang.T("français", "english").
enum Lang {
    static var en: Bool = {
        if let v = UserDefaults.standard.object(forKey: "English") as? Bool { return v }
        return !(Locale.preferredLanguages.first ?? "fr").hasPrefix("fr")
    }()
    static func T(_ fr: String, _ en: String) -> String { Lang.en ? en : fr }
}

enum AppInfo {
    static let version = "0.0.2"
    static let date = "2026-10-24"
}

enum Fmt {
    static func group(_ n: Int64) -> String {
        var out = ""
        for (i, c) in String(n).reversed().enumerated() {
            if i > 0 && i % 3 == 0 { out.append(Lang.en ? "," : " ") }
            out.append(c)
        }
        return String(out.reversed())
    }

    private static func trim(_ s: String) -> String {
        guard s.contains(".") else { return s }
        var r = s
        while r.hasSuffix("0") { r.removeLast() }
        if r.hasSuffix(".") { r.removeLast() }
        return r
    }

    static func size(_ bytes: Int64) -> String {
        if bytes < 0 { return "" }
        if bytes < 1024 { return group(bytes) + (Lang.en ? (bytes > 1 ? " bytes" : " byte") : (bytes > 1 ? " octets" : " octet")) }
        let units = Lang.en ? ["KB", "MB", "GB", "TB", "PB"] : ["Ko", "Mo", "Go", "To", "Po"]
        var v = Double(bytes) / 1024
        var u = 0
        while v >= 1024 && u < units.count - 1 { v /= 1024; u += 1 }
        var s = v < 10 ? trim(String(format: "%.2f", v)) : v < 100 ? trim(String(format: "%.1f", v)) : group(Int64(v.rounded()))
        if !Lang.en { s = s.replacingOccurrences(of: ".", with: ",") }
        return s + " " + units[u]
    }

    static func count(_ n: Int, _ singular: String, _ plural: String) -> String { group(Int64(n)) + " " + (n > 1 ? plural : singular) }

    static func duration(_ seconds: Double) -> String {
        let t = Int(max(0, seconds))
        return t >= 3600 ? String(format: "%d:%02d:%02d", t / 3600, t / 60 % 60, t % 60) : String(format: "%02d:%02d", t / 60, t % 60)
    }

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = Lang.en ? "yyyy-MM-dd HH:mm" : "dd/MM/yyyy HH:mm"
        return f
    }()
    static func date(_ d: Date) -> String { d == .distantPast ? "" : dateFormatter.string(from: d) }

    static func typeName(_ name: String, isDir: Bool) -> String {
        if isDir { return Lang.T("Dossier", "Folder") }
        let ext = (name as NSString).pathExtension.uppercased()
        return ext.isEmpty ? Lang.T("Fichier", "File") : Lang.T("Fichier ", "") + ext + Lang.T("", " file")
    }
}

/// Réglages persistants (UserDefaults, domaine de l'appli).
enum Settings {
    private static var d: UserDefaults { .standard }
    static func bool(_ k: String, _ def: Bool) -> Bool { d.object(forKey: k) as? Bool ?? def }
    static func int(_ k: String, _ def: Int) -> Int { d.object(forKey: k) as? Int ?? def }
    static func string(_ k: String) -> String? { d.string(forKey: k) }
    static func strings(_ k: String) -> [String] { d.stringArray(forKey: k) ?? [] }
    static func set(_ k: String, _ v: Any?) { d.set(v, forKey: k) }
}

// ================================================================== tailles de dossiers

final class DirSize {
    var bytes: Int64 = 0
    var files = 0
    var folders = 0
    var complete = false
    var errors = 0
}

final class CancelToken {
    var cancelled = false
}

/// Calcul (en arrière-plan) et cache partagé des tailles de dossiers.
enum DirSizes {
    private static var cache: [String: DirSize] = [:]
    private static let lock = NSLock()

    private static func norm(_ p: String) -> String { p.count > 1 && p.hasSuffix("/") ? String(p.dropLast()) : p }

    static func get(_ path: String) -> DirSize? {
        lock.lock(); defer { lock.unlock() }
        return cache[norm(path)]
    }

    /// Oublie la taille de ce chemin, de ses parents et de ses enfants.
    static func invalidate(_ path: String) {
        guard !path.isEmpty else { return }
        let p = norm(path)
        lock.lock(); defer { lock.unlock() }
        for key in cache.keys where p == key || p.hasPrefix(key == "/" ? "/" : key + "/") || key.hasPrefix(p == "/" ? "/" : p + "/") {
            cache[key] = nil
        }
    }

    static func compute(folders: [String], force: Bool, token: CancelToken, progress: (String, DirSize) -> Void) {
        for folder in folders {
            if token.cancelled { return }
            let key = norm(folder)
            lock.lock(); let existing = cache[key]; lock.unlock()
            if !force, let e = existing, e.complete { progress(folder, e); continue }

            let size = DirSize()
            lock.lock(); cache[key] = size; lock.unlock()
            var last = Date()
            let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey]
            // Le parcours ne suit pas les liens symboliques (boucles, doubles comptes)
            let en = FileManager.default.enumerator(at: URL(fileURLWithPath: folder), includingPropertiesForKeys: keys, options: [], errorHandler: { _, _ in size.errors += 1; return true })
            while let url = en?.nextObject() as? URL {
                if token.cancelled {
                    lock.lock(); cache[key] = nil; lock.unlock()
                    return
                }
                guard let v = try? url.resourceValues(forKeys: Set(keys)) else { size.errors += 1; continue }
                if v.isDirectory == true { size.folders += 1 }
                else {
                    size.files += 1
                    if v.isSymbolicLink != true { size.bytes += Int64(v.fileSize ?? 0) }
                }
                if Date().timeIntervalSince(last) > 0.25 { last = Date(); progress(folder, size) }
            }
            size.complete = true
            progress(folder, size)
        }
    }
}
