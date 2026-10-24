import Foundation

/// Test de la file de transferts hors interface : `S3bopus --selftest`.
enum SelfTest {
    static func run() -> Int32 {
        var failures = 0
        func check(_ ok: Bool, _ what: String) { print((ok ? "ok   " : "FAIL ") + what); if !ok { failures += 1 } }
        let fm = FileManager.default
        let root = NSTemporaryDirectory() + "s3bopus-selftest-\(getpid())"
        try? fm.removeItem(atPath: root)
        defer { try? fm.removeItem(atPath: root) }
        for d in ["src/sub", "dst", "dst2"] { try! fm.createDirectory(atPath: root.joined(d), withIntermediateDirectories: true) }
        try! "bonjour".write(toFile: root.joined("src/a.txt"), atomically: true, encoding: .utf8)
        try! "salut".write(toFile: root.joined("src/sub/b.txt"), atomically: true, encoding: .utf8)
        try! Data(count: 3 << 20).write(to: URL(fileURLWithPath: root.joined("src/big.bin")))
        symlink("a.txt", root.joined("src/link"))

        let q = TransferQueue()
        var choices: [ConflictChoice] = []
        q.resolveConflict = { _, info in choices.append(.keepBoth); return .keepBoth }
        func wait(_ j: TransferJob) { while !j.isFinished { Thread.sleep(forTimeInterval: 0.02) } }

        // Copie d'un dossier
        let j1 = q.enqueue(.copy, [root.joined("src")], to: root.joined("dst")); wait(j1)
        check(j1.state == .done, "copie dossier terminée (\(j1.state), \(j1.errors))")
        check((try? String(contentsOfFile: root.joined("dst/src/sub/b.txt"))) == "salut", "contenu copié")
        check((try? fm.destinationOfSymbolicLink(atPath: root.joined("dst/src/link"))) == "a.txt", "lien symbolique recréé")
        check(Sys.stat(root.joined("dst/src/big.bin"))?.size == 3 << 20, "gros fichier copié")
        check(Sys.exists(root.joined("src/a.txt")), "source conservée après copie")

        // Conflit : conserver les deux
        let j2 = q.enqueue(.copy, [root.joined("src/a.txt")], to: root.joined("dst/src")); wait(j2)
        check(choices.count == 1 && Sys.exists(root.joined("dst/src/a (2).txt")), "conflit → conserver les deux")

        // Copie dans le même dossier : suffixe « Copie »
        let j3 = q.enqueue(.copy, [root.joined("src/a.txt")], to: root.joined("src")); wait(j3)
        check(Sys.exists(root.joined("src/a - Copie.txt")), "copie dans le même dossier")

        // Déplacement (même disque : renommage)
        let j4 = q.enqueue(.move, [root.joined("src/sub")], to: root.joined("dst2")); wait(j4)
        check(j4.state == .done && Sys.exists(root.joined("dst2/sub/b.txt")) && !Sys.exists(root.joined("src/sub")), "déplacement dossier")

        // Dossier dans lui-même
        let j5 = q.enqueue(.copy, [root.joined("src")], to: root.joined("src/sub2")); wait(j5)
        check(j5.state == .doneWithErrors, "refus dossier dans son sous-dossier")

        // Annulation avant démarrage
        q.setPaused(true)
        let j6 = q.enqueue(.copy, [root.joined("src/a.txt")], to: root.joined("dst2")); q.cancel(j6); q.setPaused(false)
        check(j6.state == .canceled, "annulation")

        // Tailles de dossiers
        let tok = CancelToken(); var final: DirSize?
        DirSizes.compute(folders: [root.joined("src")], force: true, token: tok) { _, s in final = s }
        check(final?.complete == true && (final?.bytes ?? 0) >= 3 << 20, "taille de dossier")

        // Formats
        check(Fmt.size(1536) == (Lang.en ? "1.5 KB" : "1,5 Ko"), "format taille (\(Fmt.size(1536)))")
        print(failures == 0 ? "TOUS LES TESTS PASSENT" : "\(failures) ÉCHEC(S)")
        return failures == 0 ? 0 : 1
    }
}
