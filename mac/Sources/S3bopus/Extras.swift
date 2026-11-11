import AppKit

/// Dossiers favoris : ceux ajoutés à la main (réglage « Favorites ») et les « favoris automatiques »,
/// c'est-à-dire les dossiers les plus souvent ouverts (réglage « Visits » : chemin → nombre d'ouvertures).
enum Favorites {
    private static let maxTracked = 200

    static var manual: [String] { Settings.strings("Favorites") }
    static func isManual(_ path: String) -> Bool { manual.contains(path) }

    /// Ajoute le dossier aux favoris, ou le retire s'il y est déjà.
    static func toggle(_ path: String) {
        var list = manual
        if let i = list.firstIndex(of: path) { list.remove(at: i) } else { list.append(path) }
        Settings.set("Favorites", list)
    }

    private static var visits: [String: Int] {
        get { (UserDefaults.standard.dictionary(forKey: "Visits") as? [String: Int]) ?? [:] }
        set { UserDefaults.standard.set(newValue, forKey: "Visits") }
    }

    /// Compte une ouverture du dossier.
    static func visit(_ path: String) {
        guard !path.isEmpty else { return }
        var d = visits
        d[path, default: 0] += 1
        // Pas de vieillissement : un dossier très ouvert autrefois reste en tête ; « Effacer » remet à zéro.
        if d.count > maxTracked {
            let keep = d.sorted { $0.value > $1.value }.prefix(maxTracked / 2)
            d = Dictionary(uniqueKeysWithValues: keep.map { ($0.key, $0.value) })
            if d[path] == nil { d[path] = 1 }
        }
        visits = d
    }

    /// Les n dossiers les plus ouverts, avec leur nombre d'ouvertures.
    static func top(_ n: Int) -> [(path: String, count: Int)] {
        visits.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }.prefix(n).map { ($0.key, $0.value) }
    }

    static func clearSmart() { UserDefaults.standard.removeObject(forKey: "Visits") }
}

/// Vérification des mises à jour : le site publie version.txt (lignes « windows=1.0.5 », « linux=… », « mac=… »).
/// S3bopus ne télécharge et n'installe rien : il propose seulement d'ouvrir la page du site.
enum UpdateCheck {
    static let page = "https://s3btify.com/s3bopus/"

    /// Valeur de la ligne « key=a.b.c » ; nil si elle manque ou n'est pas un numéro de version.
    static func parse(_ text: String, key: String) -> String? {
        for line in text.split(whereSeparator: \.isNewline) {
            let l = line.trimmingCharacters(in: .whitespaces)
            guard l.hasPrefix(key + "=") else { continue }
            let v = String(l.dropFirst(key.count + 1))
            if components(v) != nil { return v }
        }
        return nil
    }

    private static func components(_ v: String) -> [Int]? {
        let parts = v.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        return parts.count == 3 && !parts.contains(where: { $0 == nil }) ? parts.map { $0! } : nil
    }

    static func isNewer(_ latest: String, than current: String) -> Bool {
        guard let a = components(latest), let b = components(current) else { return false }
        return a.lexicographicallyPrecedes(b) ? false : a != b
    }

    /// Demande la version Mac publiée, hors du fil principal ; `done` est appelé sur le fil principal (nil : hors ligne).
    static func latest(done: @escaping (String?) -> Void) {
        guard let url = URL(string: page + "version.txt") else { return done(nil) }
        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 5)
        req.setValue("S3bopus", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { data, resp, _ in
            var v: String?
            if let d = data?.prefix(2000), (resp as? HTTPURLResponse)?.statusCode == 200, let t = String(data: d, encoding: .utf8) { v = parse(t, key: "mac") }
            DispatchQueue.main.async { done(v) }
        }.resume()
    }

    /// Au démarrage : propose la page de téléchargement si une version plus récente existe (une seule fois par version).
    static func checkAtStartup() {
        guard Settings.bool("UpdateCheck", true) else { return }
        latest { v in
            guard let v = v, isNewer(v, than: AppInfo.version), Settings.string("UpdateSkipped") != v else { return }
            if Dialogs.confirm(Lang.T("Mise à jour", "Update"),
                               Lang.T("S3bopus \(v) est disponible (vous avez la \(AppInfo.version)).\n\nOuvrir la page de téléchargement ?",
                                      "S3bopus \(v) is available (you have \(AppInfo.version)).\n\nOpen the download page?"),
                               ok: Lang.T("Ouvrir", "Open")) {
                if let u = URL(string: page) { NSWorkspace.shared.open(u) }
            } else { Settings.set("UpdateSkipped", v) }
        }
    }
}

/// Lien avec S3btify (0.15.30A+) : lit le morceau en cours et les suivants, envoie précédent / lecture-pause / suivant.
/// Local uniquement (127.0.0.1:34565). Interface : GET /api/player (JSON) et POST /api/player/{prev|playpause|next}.
final class S3btifyLink {
    static let shared = S3btifyLink()
    static let minVersion = "0.15.30A"

    private(set) var available = false, playing = false
    private(set) var appVersion = "", title = "", artist = "", nextText = ""
    var onChange: (() -> Void)?
    private var timer: Timer?

    private var port: Int { Settings.int("S3btifyPort", 34565) }
    private func url(_ path: String) -> URL { URL(string: "http://127.0.0.1:\(port)\(path)")! }

    /// Clé triable d'une version « a.b.c » ou « a.b.cL » (nil si le format est autre) : 0.15.30 < 0.15.30A < 0.15.30B < 0.15.31.
    static func versionKey(_ v: String) -> String? {
        guard let r = v.range(of: #"^(\d{1,6})\.(\d{1,6})\.(\d{1,6})([A-Za-z]?)$"#, options: .regularExpression) else { return nil }
        var s = String(v[r])
        let letter = s.last!.isLetter ? String(s.removeLast()).uppercased() : ""
        let nums = s.split(separator: ".").map { String($0) }
        return nums.map { String(repeating: "0", count: 6 - $0.count) + $0 }.joined() + letter
    }

    static func supported(_ v: String) -> Bool {
        guard let k = versionKey(v), let m = versionKey(minVersion) else { return false }
        return k >= m
    }

    func start() {
        guard timer == nil else { return }
        poll()
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.poll() }
    }

    private func poll() {
        var req = URLRequest(url: url("/api/player"), timeoutInterval: 0.7)
        req.httpMethod = "GET"
        URLSession.shared.dataTask(with: req) { [weak self] data, _, _ in
            var avail = false, play = false
            var ver = "", t = "", a = "", next = ""
            if let d = data, d.count < 20000, let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
               j["app"] as? String == "S3btify", let v = j["version"] as? String, S3btifyLink.supported(v) {
                avail = true; ver = v
                play = j["playing"] as? Bool ?? false
                t = j["title"] as? String ?? ""; a = j["artist"] as? String ?? ""
                if let list = j["next"] as? [[String: Any]] {
                    next = list.prefix(5).compactMap { o -> String? in
                        let tt = o["title"] as? String ?? "", aa = o["artist"] as? String ?? ""
                        return tt.isEmpty ? nil : (aa.isEmpty ? tt : aa + " – " + tt)
                    }.joined(separator: "  ·  ")
                }
            }
            DispatchQueue.main.async {
                guard let self = self else { return }
                let changed = avail != self.available || play != self.playing || ver != self.appVersion || t != self.title || a != self.artist || next != self.nextText
                self.available = avail; self.playing = play; self.appVersion = ver; self.title = t; self.artist = a; self.nextText = next
                if changed { self.onChange?() }
            }
        }.resume()
    }

    /// command : "prev", "playpause" ou "next".
    func send(_ command: String) {
        var req = URLRequest(url: url("/api/player/" + command), timeoutInterval: 1.5)
        req.httpMethod = "POST"
        URLSession.shared.dataTask(with: req) { [weak self] _, _, _ in DispatchQueue.main.async { self?.poll() } }.resume()
    }

    /// Texte du bandeau : en ligne / hors ligne, morceau en cours et suivants.
    var text: String {
        guard available else { return Lang.T("S3btify hors ligne", "S3btify offline") }
        let now = title.isEmpty ? Lang.T("rien en lecture", "nothing playing")
            : "♪ " + (artist.isEmpty ? "" : artist + " – ") + title + (playing ? "" : Lang.T(" (pause)", " (paused)"))
        return "S3btify \(appVersion)  ·  " + now + (nextText.isEmpty ? "" : "   ·   " + Lang.T("Suivants : ", "Next: ") + nextText)
    }
}
