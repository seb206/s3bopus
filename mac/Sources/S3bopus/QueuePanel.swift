import AppKit

final class QueuePanel: NSView, NSTableViewDataSource, NSTableViewDelegate {
    private let queue: TransferQueue
    private let table = NSTableView()
    private var rows: [TransferJob] = []
    private let pauseButton = NSButton()
    private let emptyLabel = NSTextField(labelWithString: Lang.T("Aucun transfert — F5 copie, F6 déplace vers l'autre panneau", "No transfers — F5 copies, F6 moves to the other pane"))
    var onClose: (() -> Void)?
    var onOpenFolder: ((String) -> Void)?

    init(queue: TransferQueue) {
        self.queue = queue
        super.init(frame: .zero)
        func button(_ title: String, _ tip: String, _ sel: Selector) -> NSButton {
            let b = NSButton(title: title, target: self, action: sel)
            b.bezelStyle = .texturedRounded
            b.controlSize = .small
            b.toolTip = tip
            return b
        }
        pauseButton.title = Lang.T("Pause", "Pause")
        pauseButton.target = self; pauseButton.action = #selector(togglePause)
        pauseButton.bezelStyle = .texturedRounded; pauseButton.controlSize = .small
        let title = NSTextField(labelWithString: Lang.T("File de transferts", "Transfer queue"))
        title.font = .boldSystemFont(ofSize: 12)
        let bar = NSStackView(views: [title, NSView(), pauseButton,
                                      button(Lang.T("Annuler", "Cancel"), Lang.T("Annuler la tâche sélectionnée", "Cancel the selected task"), #selector(cancelSel)),
                                      button(Lang.T("Relancer", "Retry"), Lang.T("Relancer la tâche terminée", "Retry the finished task"), #selector(retrySel)),
                                      button("↑", Lang.T("Monter", "Move up"), #selector(upSel)),
                                      button("↓", Lang.T("Descendre", "Move down"), #selector(downSel)),
                                      button(Lang.T("Effacer les terminées", "Clear finished"), "", #selector(clearDone)),
                                      button(Lang.T("Fermer", "Close"), "", #selector(closeClicked))])
        bar.spacing = 4
        bar.edgeInsets = NSEdgeInsets(top: 4, left: 8, bottom: 4, right: 8)

        func col(_ id: String, _ t: String, _ w: CGFloat) {
            let c = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            c.title = t; c.width = w; c.minWidth = 40
            table.addTableColumn(c)
        }
        col("op", Lang.T("Opération", "Operation"), 130)
        col("src", Lang.T("Source", "Source"), 280)
        col("dst", Lang.T("Destination", "Destination"), 220)
        col("prog", Lang.T("Progression", "Progress"), 140)
        col("info", Lang.T("Détails", "Details"), 220)
        table.allowsMultipleSelection = false
        table.rowHeight = 20
        table.usesAlternatingRowBackgroundColors = true
        table.dataSource = self; table.delegate = self
        table.doubleAction = #selector(doubleClicked); table.target = self
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true

        let stack = NSStackView(views: [bar, scroll])
        stack.orientation = .vertical
        stack.spacing = 0
        stack.alignment = .leading
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: leadingAnchor), stack.trailingAnchor.constraint(equalTo: trailingAnchor),
            stack.topAnchor.constraint(equalTo: topAnchor), stack.bottomAnchor.constraint(equalTo: bottomAnchor),
            bar.leadingAnchor.constraint(equalTo: stack.leadingAnchor), bar.trailingAnchor.constraint(equalTo: stack.trailingAnchor),
            scroll.leadingAnchor.constraint(equalTo: stack.leadingAnchor), scroll.trailingAnchor.constraint(equalTo: stack.trailingAnchor)
        ])
        scroll.setContentHuggingPriority(.defaultLow, for: .vertical)

        emptyLabel.textColor = .secondaryLabelColor
        emptyLabel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(emptyLabel)
        NSLayoutConstraint.activate([
            emptyLabel.centerXAnchor.constraint(equalTo: centerXAnchor),
            emptyLabel.centerYAnchor.constraint(equalTo: scroll.centerYAnchor, constant: 8)
        ])
    }
    required init?(coder: NSCoder) { fatalError() }

    private var selected: TransferJob? { table.selectedRow >= 0 && table.selectedRow < rows.count ? rows[table.selectedRow] : nil }

    /// Pourcentage global des tâches actives, -1 s'il n'y en a pas.
    func overallPercent() -> Int {
        let active = queue.allJobs.filter { !$0.isFinished }
        guard !active.isEmpty else { return -1 }
        let total = active.reduce(Int64(0)) { $0 + $1.totalBytes }, done = active.reduce(Int64(0)) { $0 + $1.doneBytes }
        return total > 0 ? Int(done * 100 / total) : 0
    }

    func refresh() {
        let now = Date()
        for j in queue.allJobs where j.state == .running {
            let dt = now.timeIntervalSince(j.lastTick)
            if dt >= 1 {
                let raw = Double(j.doneBytes - j.lastBytes) / dt
                j.speed = j.speed == 0 ? raw : 0.6 * j.speed + 0.4 * raw
                j.lastBytes = j.doneBytes; j.lastTick = now
            }
        }
        let sel = selected
        rows = queue.allJobs
        table.reloadData()
        emptyLabel.isHidden = !rows.isEmpty
        if let s = sel, let i = rows.firstIndex(where: { $0 === s }) { table.selectRowIndexes([i], byExtendingSelection: false) }
        pauseButton.title = queue.paused ? Lang.T("Reprendre", "Resume") : Lang.T("Pause", "Pause")
    }

    func numberOfRows(in tableView: NSTableView) -> Int { rows.count }

    func tableView(_ tv: NSTableView, viewFor column: NSTableColumn?, row: Int) -> NSView? {
        guard let id = column?.identifier.rawValue, row < rows.count else { return nil }
        let j = rows[row]
        if id == "prog" {
            let cell = tv.makeView(withIdentifier: NSUserInterfaceItemIdentifier("prog"), owner: nil) as? ProgressCell ?? ProgressCell()
            cell.bar.doubleValue = Double(j.percent)
            cell.label.stringValue = "\(j.percent) %"
            return cell
        }
        let cell = tv.makeView(withIdentifier: NSUserInterfaceItemIdentifier(id), owner: nil) as? TextCell ?? TextCell(id: id, align: .left)
        var text = ""
        switch id {
        case "op": text = j.kindText + " — " + j.stateText(paused: queue.paused)
        case "src": text = j.sourceText
        case "dst": text = j.destDir
        default:
            if j.state == .running {
                var t = j.speed > 0 ? Fmt.size(Int64(j.speed)) + Lang.T("/s", "/s") : ""
                if j.speed > 0 && j.totalBytes > j.doneBytes { t += " · " + Fmt.duration(Double(j.totalBytes - j.doneBytes) / j.speed) }
                text = t + (j.current.isEmpty ? "" : " · " + j.current.lastName)
            } else if j.isFinished {
                text = Fmt.size(j.totalBytes) + " · " + Fmt.duration(j.finished.timeIntervalSince(j.started))
            }
        }
        cell.textField?.stringValue = text
        cell.toolTip = id == "info" && !j.errors.isEmpty ? j.errors.joined(separator: "\n") : nil
        return cell
    }

    @objc private func togglePause() { queue.setPaused(!queue.paused); refresh() }
    @objc private func cancelSel() { if let j = selected { queue.cancel(j); refresh() } }
    @objc private func retrySel() { if let j = selected { queue.retry(j); refresh() } }
    @objc private func upSel() { if let j = selected { queue.moveJob(j, by: -1); refresh() } }
    @objc private func downSel() { if let j = selected { queue.moveJob(j, by: 1); refresh() } }
    @objc private func clearDone() { queue.clearFinished(); refresh() }
    @objc private func closeClicked() { onClose?() }
    @objc private func doubleClicked() { if let j = selected { onOpenFolder?(j.destDir) } }
}

final class ProgressCell: NSTableCellView {
    let bar = NSProgressIndicator()
    let label = NSTextField(labelWithString: "")
    override init(frame: NSRect) {
        super.init(frame: frame)
        identifier = NSUserInterfaceItemIdentifier("prog")
        bar.style = .bar; bar.isIndeterminate = false; bar.minValue = 0; bar.maxValue = 100
        label.font = .systemFont(ofSize: 11)
        let row = NSStackView(views: [bar, label])
        row.spacing = 6
        row.translatesAutoresizingMaskIntoConstraints = false
        addSubview(row)
        NSLayoutConstraint.activate([
            row.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 2), row.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -2),
            row.centerYAnchor.constraint(equalTo: centerYAnchor), label.widthAnchor.constraint(equalToConstant: 40)
        ])
    }
    required init?(coder: NSCoder) { fatalError() }
}
