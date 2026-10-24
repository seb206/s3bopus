import AppKit

/// Disposition à la main : barre de commandes, panneaux (qui prennent tout le reste), barre des touches de fonction.
final class RootView: NSView {
    let top: NSView, middle: NSView, bottom: NSView
    init(top: NSView, middle: NSView, bottom: NSView) {
        self.top = top; self.middle = middle; self.bottom = bottom
        super.init(frame: .zero)
        for v in [top, middle, bottom] { addSubview(v) }
    }
    required init?(coder: NSCoder) { fatalError() }
    override func layout() {
        super.layout()
        let th = top.fittingSize.height
        let bh: CGFloat = bottom.isHidden ? 0 : bottom.fittingSize.height
        top.frame = NSRect(x: 0, y: bounds.height - th, width: bounds.width, height: th)
        bottom.frame = NSRect(x: 0, y: 0, width: bounds.width, height: bh)
        middle.frame = NSRect(x: 0, y: bh, width: bounds.width, height: max(0, bounds.height - th - bh))
    }
}

final class MainController: NSObject, NSWindowDelegate, PaneHost, NSMenuItemValidation {
    let window: NSWindow
    private let leftTabs: PaneTabs, rightTabs: PaneTabs
    private let queue = TransferQueue()
    private let queuePanel: QueuePanel
    private let panesSplit = NSSplitView()
    private let outerSplit = NSSplitView()
    private var fkeyBar: NSView!
    private var active: FilePane!
    private var timer: Timer?
    private var queueHeight: CGFloat = 200

    var showHiddenItem: NSMenuItem?, autoSizesItem: NSMenuItem?, queueItem: NSMenuItem?, fkeysItem: NSMenuItem?

    override init() {
        FilePane.showHidden = Settings.bool("ShowHidden", false)
        FilePane.autoSizes = Settings.bool("AutoSizes", false)
        queuePanel = QueuePanel(queue: queue)
        var leftRef: PaneTabs?, rightRef: PaneTabs?
        var hostRef: PaneHost?
        func make(_ side: String) -> FilePane {
            let p = FilePane()
            p.host = hostRef
            let model = (side == "Left" ? leftRef : rightRef)?.current
            p.setSort(key: model?.sortKey ?? Settings.string(side + "Sort") ?? "name", ascending: model?.sortAscending ?? Settings.bool(side + "Asc", true))
            return p
        }
        leftTabs = PaneTabs(makePane: { make("Left") })
        rightTabs = PaneTabs(makePane: { make("Right") })
        leftRef = leftTabs; rightRef = rightTabs
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 780),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        hostRef = self

        window.title = "S3bopus"
        window.minSize = NSSize(width: 640, height: 400)
        window.delegate = self
        window.setFrameAutosaveName("S3bopusMain")
        if !window.setFrameUsingName("S3bopusMain") { window.center() }

        leftTabs.onCurrentChanged = { [weak self] p in self?.setActive(p) }
        rightTabs.onCurrentChanged = { [weak self] p in self?.setActive(p) }

        panesSplit.isVertical = true
        panesSplit.dividerStyle = .thin
        panesSplit.addArrangedSubview(leftTabs)
        panesSplit.addArrangedSubview(rightTabs)
        panesSplit.autosaveName = "S3bopusPanes"

        queuePanel.onClose = { [weak self] in self?.showQueue(false) }
        queuePanel.onOpenFolder = { [weak self] p in self?.active.navigate(p); self?.active.focusList() }
        queuePanel.isHidden = true
        outerSplit.isVertical = false
        outerSplit.dividerStyle = .thin
        outerSplit.addArrangedSubview(panesSplit)

        let top = commandBar(), bottom = functionBar()
        let root = RootView(top: top, middle: outerSplit, bottom: bottom)
        window.contentView = root
        fkeyBar = bottom
        fkeyBar.isHidden = !Settings.bool("FKeys", true)
        root.needsLayout = true

        queue.resolveConflict = { [weak self] job, info in
            DispatchQueue.main.sync { self?.showQueue(true); return Dialogs.conflict(job, info) }
        }
        queue.onJobChanged = { [weak self] job in DispatchQueue.main.async { self?.jobChanged(job) } }

        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in self?.tick() }

        restoreTabs()
        queueHeight = CGFloat(Settings.int("QueueHeight", 200))
        window.makeKeyAndOrderFront(nil)
        root.layoutSubtreeIfNeeded()
        if UserDefaults.standard.object(forKey: "NSSplitView Subview Frames S3bopusPanes") == nil {
            panesSplit.setPosition(panesSplit.bounds.width / 2, ofDividerAt: 0)
        }
        leftTabs.current.activate()
        leftTabs.current.focusList()
    }

    // ======================================================================= construction

    private func commandBar() -> NSView {
        func cmd(_ symbol: String, _ title: String, _ tip: String, _ sel: Selector) -> NSButton {
            let b = NSButton(title: title, target: self, action: sel)
            b.image = NSImage(systemSymbolName: symbol, accessibilityDescription: tip)
            b.imagePosition = title.isEmpty ? .imageOnly : .imageLeading
            b.bezelStyle = .texturedRounded
            b.toolTip = tip
            return b
        }
        let views: [NSView] = [
            cmd("folder.badge.plus", Lang.T("Nouveau dossier", "New folder"), Lang.T("Nouveau dossier (F7 / ⇧⌘N)", "New folder (F7 / ⇧⌘N)"), #selector(newFolder)),
            cmd("pencil", "", Lang.T("Renommer (F2)", "Rename (F2)"), #selector(rename)),
            cmd("trash", "", Lang.T("Mettre à la corbeille (⌘⌫ / F8) — ⇧ pour supprimer définitivement", "Move to Trash (⌘⌫ / F8) — ⇧ to delete permanently"), #selector(trash)),
            cmd("doc.on.doc", Lang.T("Copier vers", "Copy to"), Lang.T("Copier vers l'autre panneau (F5 / ⌥⌘C)", "Copy to the other pane (F5 / ⌥⌘C)"), #selector(copyToOther)),
            cmd("arrow.right.doc.on.clipboard", Lang.T("Déplacer vers", "Move to"), Lang.T("Déplacer vers l'autre panneau (F6 / ⌥⌘X)", "Move to the other pane (F6 / ⌥⌘X)"), #selector(moveToOther)),
            cmd("sum", Lang.T("Tailles", "Sizes"), Lang.T("Taille des dossiers (F3 / ⌥⌘I)", "Folder sizes (F3 / ⌥⌘I)"), #selector(computeSizes)),
            cmd("arrow.left.arrow.right", "", Lang.T("Échanger les panneaux (⌘U)", "Swap panes (⌘U)"), #selector(swapPanes)),
            cmd("equal", "", Lang.T("Même dossier dans l'autre panneau (⌘=)", "Same folder in the other pane (⌘=)"), #selector(syncOther)),
            cmd("terminal", "", Lang.T("Ouvrir un terminal ici", "Open a terminal here"), #selector(openTerminal)),
            cmd("folder", "", Lang.T("Afficher dans le Finder", "Show in Finder"), #selector(revealFolder))
        ]
        let bar = NSStackView(views: views)
        bar.spacing = 4
        bar.edgeInsets = NSEdgeInsets(top: 4, left: 8, bottom: 4, right: 8)
        return bar
    }

    private func functionBar() -> NSView {
        func fk(_ key: String, _ label: String, _ sel: Selector) -> NSButton {
            let b = NSButton(title: key + "  " + label, target: self, action: sel)
            b.isBordered = false
            b.font = .systemFont(ofSize: 11)
            return b
        }
        let bar = NSStackView(views: [
            fk("F2", Lang.T("Renommer", "Rename"), #selector(rename)),
            fk("F3", Lang.T("Tailles", "Sizes"), #selector(computeSizes)),
            fk("F5", Lang.T("Copier →", "Copy →"), #selector(copyToOther)),
            fk("F6", Lang.T("Déplacer →", "Move →"), #selector(moveToOther)),
            fk("F7", Lang.T("Nouveau dossier", "New folder"), #selector(newFolder)),
            fk("F8", Lang.T("Corbeille", "Trash"), #selector(trash)),
            fk("Tab", Lang.T("Autre panneau", "Other pane"), #selector(switchPaneAction)),
            fk("⌘J", Lang.T("File de transferts", "Transfer queue"), #selector(toggleQueue))
        ])
        bar.spacing = 6
        bar.edgeInsets = NSEdgeInsets(top: 1, left: 6, bottom: 1, right: 6)
        return bar
    }

    private func restoreTabs() {
        let home = NSHomeDirectory()
        let args = Array(CommandLine.arguments.dropFirst()).filter { !$0.hasPrefix("-") && Sys.isDirectory($0) }
        for (side, tabs, idx) in [("Left", leftTabs, 0), ("Right", rightTabs, 1)] {
            var saved = Settings.strings(side + "Tabs").filter { Sys.isDirectory($0) }
            var cur = Settings.int(side + "Tab", 0)
            if saved.isEmpty { saved = [home]; cur = 0 }
            cur = max(0, min(cur, saved.count - 1))
            if idx < args.count { saved[cur] = args[idx] }
            for p in saved { tabs.newTab(p) }
            tabs.select(tabs.panes[cur])
        }
    }

    // ======================================================================= état

    private var activeTabs: PaneTabs { leftTabs.panes.contains { $0 === active } ? leftTabs : rightTabs }
    private var other: FilePane { activeTabs === leftTabs ? rightTabs.current : leftTabs.current }
    private var allPanes: [FilePane] { leftTabs.panes + rightTabs.panes }

    private func setActive(_ p: FilePane) {
        active = p
        for q in allPanes { q.setActive(q === p) }
    }

    func paneActivated(_ pane: FilePane) {
        if pane === leftTabs.current || pane === rightTabs.current { setActive(pane) }
    }

    func switchPane(from pane: FilePane) { switchPaneAction() }
    func openInOtherPane(_ path: String, from pane: FilePane) { other.navigate(path) }
    func enqueue(_ kind: JobKind, _ paths: [String], to dir: String) { queue.enqueue(kind, paths, to: dir) }
    func tabCommand(_ cmd: TabCommand, from pane: FilePane) {
        switch cmd {
        case .new: newTab()
        case .close: closeTab()
        case .next: activeTabs.cycle(1)
        case .prev: activeTabs.cycle(-1)
        }
    }

    func transfer(_ kind: JobKind, from pane: FilePane) {
        let dst = other
        let paths = pane.selectedPaths
        if paths.isEmpty || dst.currentPath.isEmpty { return }
        if pane.currentPath == dst.currentPath && kind == .move { return }
        queue.enqueue(kind, paths, to: dst.currentPath)
    }

    private func jobChanged(_ job: TransferJob) {
        if job.state == .waiting && queuePanel.isHidden { showQueue(true) }
        queuePanel.refresh()
        if job.isFinished || job.state == .running {
            DirSizes.invalidate(job.destDir)
            job.sources.forEach { DirSizes.invalidate($0) }
            allPanes.forEach { $0.refreshSizesDisplay() }
        }
        if job.isFinished {
            for pane in allPanes where pane.currentPath == job.destDir || job.sources.contains(where: { $0.parentPath == pane.currentPath }) { pane.reload() }
            if job.state == .doneWithErrors || job.state == .failed { showQueue(true) }
        }
    }

    private func tick() {
        if !queuePanel.isHidden { queuePanel.refresh() }
        let pct = queuePanel.overallPercent()
        window.title = pct >= 0 ? "S3bopus — \(pct) %" : "S3bopus"
        NSApp.dockTile.badgeLabel = pct >= 0 ? "\(pct) %" : nil
    }

    func showQueue(_ show: Bool) {
        guard show == queuePanel.isHidden else { return }
        if !show { queueHeight = queuePanel.frame.height }
        queuePanel.isHidden = !show
        if !show {
            outerSplit.removeArrangedSubview(queuePanel)
            queuePanel.removeFromSuperview()
        } else {
            outerSplit.addArrangedSubview(queuePanel)
            outerSplit.setHoldingPriority(.defaultHigh, forSubviewAt: 1)
            outerSplit.layoutSubtreeIfNeeded()
            let h = max(120, min(queueHeight, outerSplit.bounds.height / 2))
            outerSplit.setPosition(outerSplit.bounds.height - h, ofDividerAt: 0)
        }
        queueItem?.state = show ? .on : .off
    }

    // ======================================================================= actions (menus, boutons)

    @objc func newFolder() { active.newFolder() }
    @objc func rename() { active.beginRename() }
    @objc func trash() { active.deleteSelected(permanent: NSEvent.modifierFlags.contains(.shift)) }
    @objc func deletePermanently() { active.deleteSelected(permanent: true) }
    @objc func copyToOther() { transfer(.copy, from: active) }
    @objc func moveToOther() { transfer(.move, from: active) }
    @objc func computeSizes() { active.computeSizes(force: true) }
    @objc func swapPanes() {
        let a = leftTabs.current.currentPath, b = rightTabs.current.currentPath
        leftTabs.current.navigate(b); rightTabs.current.navigate(a)
    }
    @objc func syncOther() { other.navigate(active.currentPath) }
    @objc func openTerminal() { Sys.openTerminal(at: active.currentPath) }
    @objc func revealFolder() { Sys.open(active.currentPath) }
    @objc func switchPaneAction() { let o = other; setActive(o); o.focusList() }
    @objc func toggleQueue() { showQueue(queuePanel.isHidden) }
    @objc func newTab() { activeTabs.newTab(active.currentPath).activate(); active.focusList() }
    @objc func closeTab() { activeTabs.closeTab(active); active.focusList() }
    @objc func nextTab() { activeTabs.cycle(1) }
    @objc func prevTab() { activeTabs.cycle(-1) }
    @objc func openSelection() { active.openSelected() }
    @objc func goUp() { active.goUp() }
    @objc func goBack() { active.goBack() }
    @objc func goForward() { active.goForward() }
    @objc func focusAddress() { active.focusAddress() }
    @objc func focusFilter() { active.focusFilter() }
    @objc func reload() { active.reload() }
    @objc func invertSelection() { active.invertSelection() }
    @objc func reveal() { Sys.reveal(active.selectedPaths.isEmpty ? [active.currentPath] : active.selectedPaths) }
    @objc func getInfo() { active.selectedPaths.forEach { Sys.getInfo($0) } }
    @objc func goHome(_ s: NSMenuItem) { if let p = s.representedObject as? String { active.navigate(p) } }

    @objc func toggleHidden() {
        FilePane.showHidden.toggle()
        showHiddenItem?.state = FilePane.showHidden ? .on : .off
        allPanes.forEach { $0.reload() }
    }
    @objc func toggleAutoSizes() {
        FilePane.autoSizes.toggle()
        autoSizesItem?.state = FilePane.autoSizes ? .on : .off
        if FilePane.autoSizes { leftTabs.current.computeSizes(force: false); rightTabs.current.computeSizes(force: false) }
    }
    @objc func toggleFKeys() {
        fkeyBar.isHidden.toggle()
        window.contentView?.needsLayout = true
        fkeysItem?.state = fkeyBar.isHidden ? .off : .on
    }
    @objc func setAppearance(_ s: NSMenuItem) {
        let v = s.tag
        Settings.set("Appearance", v)
        MainController.applyAppearance()
        s.menu?.items.forEach { $0.state = $0.tag == v ? .on : .off }
    }
    static func applyAppearance() {
        switch Settings.int("Appearance", 0) {
        case 1: NSApp.appearance = NSAppearance(named: .aqua)
        case 2: NSApp.appearance = NSAppearance(named: .darkAqua)
        default: NSApp.appearance = nil
        }
    }
    @objc func setLanguage(_ s: NSMenuItem) {
        let en = s.tag == 1
        guard en != Lang.en else { return }
        Settings.set("English", en)
        if Dialogs.confirm(Lang.T("Redémarrer pour appliquer la langue ?", "Restart to apply the language?"), "", ok: Lang.T("Redémarrer", "Restart")) {
            saveSettings()
            let cfg = NSWorkspace.OpenConfiguration()
            cfg.createsNewApplicationInstance = true
            NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: cfg) { _, _ in
                DispatchQueue.main.async { NSApp.terminate(nil) }
            }
        }
    }

    @objc func about() {
        Dialogs.message("S3bopus \(AppInfo.version) — \(AppInfo.date)", Lang.en
            ? "Lightweight dual-pane file manager (native macOS).\n\nShortcuts:\n  Tab  switch pane\n  F2  rename      F3  folder sizes\n  F5  copy to the other pane      F6  move\n  F7  new folder      F8 / ⌘⌫  trash (⇧: delete permanently)\n  ⌘J  transfer queue      ⇧⌘.  hidden items\n  ⌘L  address bar      ⌘F  filter\n  ⌘U  swap panes      ⌘=  same folder\n  ⌘C / X / V  clipboard (text or image: pasted as a file)\n  ⌘T / ⌘W  new / close tab      ⌃Tab  next tab\n  ⌫  parent folder\n  Middle-click a folder  open it in the other pane"
            : "Explorateur de fichiers léger à deux panneaux (natif macOS).\n\nRaccourcis :\n  Tab  changer de panneau\n  F2  renommer      F3  taille des dossiers\n  F5  copier vers l'autre panneau      F6  déplacer\n  F7  nouveau dossier      F8 / ⌘⌫  corbeille (⇧ : supprimer définitivement)\n  ⌘J  file de transferts      ⇧⌘.  éléments masqués\n  ⌘L  barre d'adresse      ⌘F  filtrer\n  ⌘U  échanger les panneaux      ⌘=  même dossier\n  ⌘C / X / V  presse-papiers (texte ou image : collé comme fichier)\n  ⌘T / ⌘W  nouvel onglet / fermer      ⌃Tab  onglet suivant\n  ⌫  dossier parent\n  Clic molette sur un dossier  l'ouvrir dans l'autre panneau")
    }

    /// Les actions sur les fichiers sont inertes pendant la saisie dans un champ texte.
    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        let editing = window.firstResponder is NSText
        let fileActions: [Selector] = [#selector(trash), #selector(openSelection), #selector(goUp), #selector(rename), #selector(invertSelection)]
        if editing, let a = item.action, fileActions.contains(a) { return false }
        return true
    }

    // ======================================================================= fermeture / réglages

    func confirmQuit() -> Bool {
        guard queue.allJobs.contains(where: { !$0.isFinished }) else { return true }
        return Dialogs.confirm(Lang.T("Des transferts sont en cours ou en attente.", "Transfers are running or pending."),
                               Lang.T("Quitter quand même (ils seront interrompus) ?", "Quit anyway (they will be interrupted)?"), ok: Lang.T("Quitter", "Quit"))
    }

    func saveSettings() {
        queue.allJobs.forEach { queue.cancel($0) }
        Settings.set("ShowHidden", FilePane.showHidden)
        Settings.set("AutoSizes", FilePane.autoSizes)
        Settings.set("FKeys", !fkeyBar.isHidden)
        Settings.set("QueueHeight", Int(queuePanel.isHidden ? queueHeight : queuePanel.frame.height))
        for (side, tabs) in [("Left", leftTabs), ("Right", rightTabs)] {
            Settings.set(side + "Tabs", tabs.panes.map { $0.currentPath })
            Settings.set(side + "Tab", tabs.panes.firstIndex { $0 === tabs.current } ?? 0)
            Settings.set(side + "Sort", tabs.current.sortKey)
            Settings.set(side + "Asc", tabs.current.sortAscending)
        }
        window.saveFrame(usingName: "S3bopusMain")
    }
}
