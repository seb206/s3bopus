import Foundation

enum JobKind { case copy, move }
enum JobState { case waiting, scanning, running, done, doneWithErrors, canceled, failed }
enum ConflictChoice { case ask, replace, skip, keepBoth, replaceIfNewer, cancelJob }

final class ConflictInfo {
    var source = "", destination = ""
    var sourceSize: Int64 = 0, destSize: Int64 = 0
    var sourceDate = Date.distantPast, destDate = Date.distantPast
    var applyToAll = false
}

final class TransferJob {
    private static var ids = 0
    let id: Int
    let kind: JobKind
    let sources: [String]
    let destDir: String
    var state = JobState.waiting
    var totalBytes: Int64 = 0, doneBytes: Int64 = 0
    var totalFiles = 0, doneFiles = 0
    var current = ""
    var started = Date(), finished = Date()
    private(set) var errors: [String] = []
    var conflicts = ConflictChoice.ask
    var cancelRequested = false
    // Statistiques d'affichage
    var speed: Double = 0
    var lastBytes: Int64 = 0
    var lastTick = Date()
    private let errLock = NSLock()

    init(kind: JobKind, sources: [String], destDir: String) {
        TransferJob.ids += 1
        id = TransferJob.ids
        self.kind = kind; self.sources = sources; self.destDir = destDir
    }

    func addError(_ m: String) { errLock.lock(); errors.append(m); errLock.unlock() }

    var isFinished: Bool { [.done, .doneWithErrors, .canceled, .failed].contains(state) }
    var percent: Int { totalBytes > 0 ? Int(min(100, doneBytes * 100 / totalBytes)) : (isFinished ? 100 : 0) }
    var kindText: String { kind == .copy ? Lang.T("Copie", "Copy") : Lang.T("Déplacement", "Move") }
    var sourceText: String {
        sources.count == 1 ? sources[0] : "\(sources.count)" + Lang.T(" éléments de ", " items from ") + sources[0].parentPath
    }

    func stateText(paused: Bool) -> String {
        switch state {
        case .waiting: return Lang.T("En attente", "Waiting")
        case .scanning: return Lang.T("Analyse…", "Scanning…")
        case .running: return paused ? Lang.T("En pause", "Paused") : Lang.T("En cours", "Running")
        case .done: return Lang.T("Terminé", "Done")
        case .doneWithErrors: return Lang.T("Terminé (", "Done (") + "\(errors.count)" + Lang.T(" erreur", " error") + (errors.count > 1 ? "s" : "") + ")"
        case .canceled: return Lang.T("Annulé", "Canceled")
        case .failed: return Lang.T("Échec", "Failed")
        }
    }
}

/// File de transferts : les tâches (copie / déplacement) sont exécutées une par une,
/// dans un thread dédié, avec pause, annulation et gestion des conflits.
final class TransferQueue {
    private var jobs: [TransferJob] = []
    private let lock = NSLock()
    private let signal = DispatchSemaphore(value: 0)
    private let gate = NSCondition()
    private(set) var paused = false

    /// Appelé (thread de travail) quand l'état d'une tâche change.
    var onJobChanged: ((TransferJob) -> Void)?
    /// Résolution des conflits : appelé depuis le thread de travail, doit être synchrone.
    var resolveConflict: ((TransferJob, ConflictInfo) -> ConflictChoice)?

    init() {
        let t = Thread { [unowned self] in self.worker() }
        t.name = "S3bopus transferts"
        t.qualityOfService = .userInitiated
        t.start()
    }

    var allJobs: [TransferJob] { lock.lock(); defer { lock.unlock() }; return jobs }

    @discardableResult
    func enqueue(_ kind: JobKind, _ sources: [String], to destDir: String) -> TransferJob {
        let job = TransferJob(kind: kind, sources: sources, destDir: destDir)
        lock.lock(); jobs.append(job); lock.unlock()
        signal.signal()
        onJobChanged?(job)
        return job
    }

    func cancel(_ job: TransferJob) {
        job.cancelRequested = true
        if job.state == .waiting { job.state = .canceled; onJobChanged?(job) }
        gate.lock(); gate.broadcast(); gate.unlock()
    }

    func remove(_ job: TransferJob) {
        guard job.isFinished else { return }
        lock.lock(); jobs.removeAll { $0 === job }; lock.unlock()
    }

    func clearFinished() { lock.lock(); jobs.removeAll { $0.isFinished }; lock.unlock() }

    func retry(_ job: TransferJob) {
        guard job.isFinished else { return }
        remove(job)
        enqueue(job.kind, job.sources.filter { Sys.exists($0) }, to: job.destDir)
    }

    func moveJob(_ job: TransferJob, by delta: Int) {
        lock.lock(); defer { lock.unlock() }
        guard let i = jobs.firstIndex(where: { $0 === job }), jobs.indices.contains(i + delta) else { return }
        jobs.swapAt(i, i + delta)
    }

    func setPaused(_ p: Bool) {
        gate.lock(); paused = p; gate.broadcast(); gate.unlock()
    }

    private func waitGate(_ job: TransferJob?) {
        gate.lock()
        while paused && !(job?.cancelRequested ?? false) { gate.wait() }
        gate.unlock()
    }

    private func worker() {
        while true {
            lock.lock(); let next = jobs.first { $0.state == .waiting }; lock.unlock()
            guard let job = next else { signal.wait(); continue }
            waitGate(nil)
            if job.state != .waiting { continue }
            do { try run(job) }
            catch {
                job.addError(error.localizedDescription)
                job.state = .failed
            }
            job.finished = Date()
            onJobChanged?(job)
        }
    }

    // ---------------------------------------------------------------------------------

    private enum OpKind { case mkdir, file, quickMove }
    private struct Op {
        var kind: OpKind
        var src: String, dst: String
        var size: Int64 = 0
        var date = Date.distantPast
    }

    private static func normalize(_ p: String) -> String { p.count > 1 && p.hasSuffix("/") ? String(p.dropLast()) : p }

    private func run(_ job: TransferJob) throws {
        job.started = Date()
        job.state = .scanning
        onJobChanged?(job)

        // ---- 1. Analyse : liste des opérations
        var ops: [Op] = []
        var dirsToRemove: [String] = []
        let destDir = Self.normalize(job.destDir)
        for src0 in job.sources {
            if job.cancelRequested { break }
            let src = Self.normalize(src0)
            let name = src.lastName
            if name.isEmpty || src == "/" { job.addError(Lang.T("Impossible de transférer la racine : ", "Can't transfer the root: ") + src0); continue }
            guard let st = Sys.stat(src) else { job.addError(Lang.T("Introuvable : ", "Not found: ") + src); continue }
            let isDir = st.isDir

            let sameFolder = src.parentPath == destDir
            if sameFolder && job.kind == .move { continue } // rien à faire
            let dst = sameFolder ? Sys.unique(dir: destDir, name: name, copySuffix: true) : destDir.joined(name)

            if isDir && (destDir + "/").hasPrefix(src + "/") {
                job.addError(Lang.T("Le dossier de destination est un sous-dossier du dossier source : ", "The destination folder is a subfolder of the source folder: ") + src)
                continue
            }

            if job.kind == .move && !Sys.exists(dst) {
                // Même disque : simple renommage, instantané
                ops.append(Op(kind: .quickMove, src: src, dst: dst, size: isDir ? -1 : 0))
                job.totalFiles += 1
                continue
            }
            if !isDir {
                let len = st.isLink ? 0 : st.size
                ops.append(Op(kind: .file, src: src, dst: dst, size: len, date: st.mtime))
                job.totalBytes += len; job.totalFiles += 1
                continue
            }
            scan(job, src, dst, &ops, &dirsToRemove)
        }

        if job.cancelRequested { job.state = .canceled; return }
        job.state = .running
        onJobChanged?(job)

        // ---- 2. Exécution
        var baseBytes: Int64 = 0
        var k = 0
        while k < ops.count {
            let op = ops[k]
            k += 1
            waitGate(job)
            if job.cancelRequested { break }
            job.current = op.src
            do {
                switch op.kind {
                case .mkdir:
                    try FileManager.default.createDirectory(atPath: op.dst, withIntermediateDirectories: true)
                case .quickMove:
                    if rename(op.src, op.dst) == 0 { job.doneFiles += 1; break }
                    if errno != EXDEV { throw SysError.posix() }
                    // Autre disque : copie puis suppression, comme un déplacement normal
                    job.totalFiles -= 1
                    var extra: [Op] = []
                    if op.size < 0 { scan(job, op.src, op.dst, &extra, &dirsToRemove) }
                    else if let st = Sys.stat(op.src) {
                        let len = st.isLink ? 0 : st.size
                        extra.append(Op(kind: .file, src: op.src, dst: op.dst, size: len, date: st.mtime))
                        job.totalBytes += len; job.totalFiles += 1
                    }
                    ops.insert(contentsOf: extra, at: k)
                case .file:
                    try copyOne(job, op, &baseBytes)
                }
            } catch is CancellationError {
                break
            } catch {
                job.addError(op.src + " : " + error.localizedDescription)
                if op.kind == .file { baseBytes += op.size; job.doneBytes = baseBytes; job.doneFiles += 1 }
            }
        }

        if job.cancelRequested { job.state = .canceled; return }

        // Déplacement inter-disques : suppression des dossiers sources vidés
        for d in dirsToRemove.reversed() { rmdir(d) }

        job.current = ""
        job.doneBytes = job.totalBytes
        job.state = job.errors.isEmpty ? .done : .doneWithErrors
    }

    private func scan(_ job: TransferJob, _ src: String, _ dst: String, _ ops: inout [Op], _ dirsToRemove: inout [String]) {
        var stack = [(src, dst)]
        while let (s, d) = stack.popLast(), !job.cancelRequested {
            ops.append(Op(kind: .mkdir, src: s, dst: d))
            if job.kind == .move { dirsToRemove.append(s) }
            do {
                for name in try FileManager.default.contentsOfDirectory(atPath: s).sorted().reversed() {
                    let cs = s.joined(name), cd = d.joined(name)
                    guard let st = Sys.stat(cs) else { continue }
                    if st.isDir { stack.append((cs, cd)) }
                    else {
                        let len = st.isLink ? 0 : st.size
                        ops.append(Op(kind: .file, src: cs, dst: cd, size: len, date: st.mtime))
                        job.totalBytes += len; job.totalFiles += 1
                    }
                }
            } catch { job.addError(s + " : " + error.localizedDescription) }
        }
    }

    private func copyOne(_ job: TransferJob, _ op: Op, _ baseBytes: inout Int64) throws {
        var dst = op.dst
        var replacing = false
        if let ds = Sys.stat(dst) {
            var choice = job.conflicts
            if choice == .ask {
                let info = ConflictInfo()
                info.source = op.src; info.destination = dst
                info.sourceSize = op.size; info.sourceDate = op.date
                info.destSize = ds.isDir ? 0 : ds.size; info.destDate = ds.mtime
                choice = resolveConflict?(job, info) ?? .skip
                if info.applyToAll && choice != .cancelJob { job.conflicts = choice }
            }
            if choice == .replaceIfNewer { choice = op.date > ds.mtime ? .replace : .skip }
            switch choice {
            case .cancelJob:
                job.cancelRequested = true
                throw CancellationError()
            case .skip:
                baseBytes += op.size; job.doneBytes = baseBytes; job.doneFiles += 1
                return
            case .keepBoth:
                dst = Sys.unique(dir: dst.parentPath, name: dst.lastName, copySuffix: false)
            default:
                if ds.isDir { throw SysError(message: Lang.T("La destination est un dossier", "The destination is a folder")) }
                replacing = true
            }
        }

        let src = Sys.stat(op.src)
        if src?.isLink == true {
            // Lien symbolique : recréé tel quel
            var buf = [CChar](repeating: 0, count: Int(PATH_MAX) + 1)
            let n = readlink(op.src, &buf, Int(PATH_MAX))
            if n < 0 { throw SysError.posix() }
            if replacing { unlink(dst) }
            if symlink(String(cString: buf), dst) != 0 { throw SysError.posix() }
            if job.kind == .move { unlink(op.src) }
        } else {
            var done = false
            if job.kind == .move {
                // rename() remplace la destination ; EXDEV = autre disque, il faut copier
                if rename(op.src, dst) == 0 { done = true } else if errno != EXDEV { throw SysError.posix() }
            }
            if !done {
                try copyData(job, op, to: dst, baseBytes: baseBytes)
                if job.kind == .move, unlink(op.src) != 0 {
                    job.addError(Lang.T("Copié mais non supprimé : ", "Copied but not deleted: ") + op.src + " : " + SysError.posix().message)
                }
            }
        }
        baseBytes += op.size
        job.doneBytes = baseBytes
        job.doneFiles += 1
    }

    /// Copie dans un fichier temporaire puis renommage. Clone APFS (instantané) si possible.
    private func copyData(_ job: TransferJob, _ op: Op, to dst: String, baseBytes: Int64) throws {
        let tmp = dst + ".s3bopus-part"
        unlink(tmp)
        if clonefile(op.src, tmp, 0) == 0 {
            if rename(tmp, dst) != 0 { let e = SysError.posix(); unlink(tmp); throw e }
            return
        }
        do {
            let inFd = open(op.src, O_RDONLY)
            if inFd < 0 { throw SysError.posix() }
            defer { close(inFd) }
            let outFd = open(tmp, O_WRONLY | O_CREAT | O_TRUNC, 0o600)
            if outFd < 0 { throw SysError.posix() }
            var outOpen = true
            defer { if outOpen { close(outFd) } }
            let bufSize = 1 << 20
            let buf = UnsafeMutableRawPointer.allocate(byteCount: bufSize, alignment: 16)
            defer { buf.deallocate() }
            var done: Int64 = 0
            while true {
                waitGate(job)
                if job.cancelRequested { throw CancellationError() }
                let n = read(inFd, buf, bufSize)
                if n < 0 { throw SysError.posix() }
                if n == 0 { break }
                var off = 0
                while off < n {
                    let w = write(outFd, buf + off, n - off)
                    if w < 0 { throw SysError.posix() }
                    off += w
                }
                done += Int64(n)
                job.doneBytes = baseBytes + done
            }
            close(outFd); outOpen = false
            // droits, dates, attributs étendus
            copyfile(op.src, tmp, nil, copyfile_flags_t(COPYFILE_METADATA))
            if rename(tmp, dst) != 0 { throw SysError.posix() }
        } catch {
            unlink(tmp)
            throw error
        }
    }
}
