import AppKit
import UniformTypeIdentifiers

enum TabCommand { case new, close, next, prev }

protocol PaneHost: AnyObject {
    func paneActivated(_ pane: FilePane)
    func switchPane(from pane: FilePane)
    func transfer(_ kind: JobKind, from pane: FilePane)
    func openInOtherPane(_ path: String, from pane: FilePane)
    func enqueue(_ kind: JobKind, _ paths: [String], to dir: String)
    func tabCommand(_ cmd: TabCommand, from pane: FilePane)
}

final class PaneTable: NSTableView {
    weak var pane: FilePane?

    override func keyDown(with event: NSEvent) {
        if pane?.handleKey(event) != true { super.keyDown(with: event) }
    }
    override func mouseDown(with event: NSEvent) { pane?.activate(); super.mouseDown(with: event) }
    override func becomeFirstResponder() -> Bool { pane?.activate(); return super.becomeFirstResponder() }
    override func otherMouseDown(with event: NSEvent) { pane?.otherMouse(event) }
    override func menu(for event: NSEvent) -> NSMenu? {
        let row = self.row(at: convert(event.locationInWindow, from: nil))
        pane?.activate()
        if row >= 0 && !selectedRowIndexes.contains(row) { selectRowIndexes([row], byExtendingSelection: false) }
        else if row < 0 { deselectAll(nil) }
        return super.menu(for: event)
    }
    @objc func copy(_ sender: Any?) { pane?.copySelection(cut: false) }
    @objc func cut(_ sender: Any?) { pane?.copySelection(cut: true) }
    @objc func paste(_ sender: Any?) { pane?.paste() }
}

final class NameCell: NSTableCellView {
    override init(frame: NSRect) {
        super.init(frame: frame)
        identifier = NSUserInterfaceItemIdentifier("name")
        let iv = NSImageView()
        iv.imageScaling = .scaleProportionallyDown
        let tf = NSTextField()
        tf.isBordered = false; tf.drawsBackground = false; tf.isEditable = false
        tf.lineBreakMode = .byTruncatingMiddle
        tf.cell?.usesSingleLineMode = true
        for v in [iv, tf] { v.translatesAutoresizingMaskIntoConstraints = false; addSubview(v) }
        imageView = iv; textField = tf
        NSLayoutConstraint.activate([
            iv.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 2),
            iv.centerYAnchor.constraint(equalTo: centerYAnchor),
            iv.widthAnchor.constraint(equalToConstant: 16), iv.heightAnchor.constraint(equalToConstant: 16),
            tf.leadingAnchor.constraint(equalTo: iv.trailingAnchor, constant: 4),
            tf.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -2),
            tf.centerYAnchor.constraint(equalTo: centerYAnchor)
        ])
    }
    required init?(coder: NSCoder) { fatalError() }
}

final class TextCell: NSTableCellView {
    init(id: String, align: NSTextAlignment) {
        super.init(frame: .zero)
        identifier = NSUserInterfaceItemIdentifier(id)
        let tf = NSTextField(labelWithString: "")
        tf.alignment = align
        tf.lineBreakMode = .byTruncatingTail
        tf.translatesAutoresizingMaskIntoConstraints = false
        addSubview(tf)
        textField = tf
        NSLayoutConstraint.activate([
            tf.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 2),
            tf.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -2),
            tf.centerYAnchor.constraint(equalTo: centerYAnchor)
        ])
    }
    required init?(coder: NSCoder) { fatalError() }
}

final class FilePane: NSView, NSTableViewDataSource, NSTableViewDelegate, NSTextFieldDelegate, NSSearchFieldDelegate, NSComboBoxDelegate, NSMenuDelegate {
    static var showHidden = false
    static var autoSizes = false
    private static var cutToken: Int?

    weak var host: PaneHost?
    var onPathChanged: (() -> Void)?

    private(set) var currentPath = ""
    private var items: [FileItem] = []
    private var shown: [FileItem] = []
    private(set) var sortKey = "name"
    private(set) var sortAscending = true
    private var back: [String] = [], forward: [String] = []
    private var filterText = ""
    private var generation = 0
    private var pendingRename: String?
    private var selectAfterIndex: Int?
    private var sizeToken: CancelToken?
    private var watcher: DispatchSourceFileSystemObject?
    private var reloadWork: DispatchWorkItem?

    private let activeBar = NSView()
    private let table = PaneTable()
    private let addressBox = NSComboBox()
    private let filterField = NSSearchField()
    private let statusLabel = NSTextField(labelWithString: "")
    private let backButton = NSButton(), forwardButton = NSButton(), upButton = NSButton()

    // ======================================================================= construction

    override init(frame: NSRect) {
        super.init(frame: frame)
        build()
    }
    required init?(coder: NSCoder) { fatalError() }

    private func symbolButton(_ symbol: String, _ tip: String, _ action: Selector) -> NSButton {
        let b = NSButton()
        b.image = NSImage(systemSymbolName: symbol, accessibilityDescription: tip)
        b.bezelStyle = .texturedRounded
        b.isBordered = false
        b.toolTip = tip
        b.target = self; b.action = action
        b.translatesAutoresizingMaskIntoConstraints = false
        b.widthAnchor.constraint(equalToConstant: 24).isActive = true
        return b
    }

    private func build() {
        activeBar.wantsLayer = true
        activeBar.translatesAutoresizingMaskIntoConstraints = false
        activeBar.heightAnchor.constraint(equalToConstant: 2).isActive = true

        let b = symbolButton("chevron.left", Lang.T("Précédent (⌘[)", "Back (⌘[)"), #selector(goBack))
        let f = symbolButton("chevron.right", Lang.T("Suivant (⌘])", "Forward (⌘])"), #selector(goForward))
        let u = symbolButton("chevron.up", Lang.T("Dossier parent (⌘↑)", "Parent folder (⌘↑)"), #selector(goUp))
        addressBox.completes = false
        addressBox.numberOfVisibleItems = 12
        addressBox.target = self; addressBox.action = #selector(addressEntered)
        addressBox.delegate = self
        addressBox.font = .systemFont(ofSize: 12)
        addressBox.addItems(withObjectValues: Settings.strings("History"))
        let addressRow = NSStackView(views: [b, f, u, addressBox])
        addressRow.spacing = 2
        addressRow.edgeInsets = NSEdgeInsets(top: 0, left: 4, bottom: 0, right: 4)

        filterField.placeholderString = Lang.T("Filtrer (* et ?)", "Filter (* and ?)")
        filterField.delegate = self
        filterField.isHidden = true

        table.pane = self
        table.style = .fullWidth
        table.usesAlternatingRowBackgroundColors = true
        table.allowsMultipleSelection = true
        table.allowsColumnReordering = false
        table.rowHeight = 20
        table.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        table.autosaveName = "S3bopusPane"
        table.autosaveTableColumns = true
        func col(_ id: String, _ title: String, _ w: CGFloat, _ min: CGFloat) {
            let c = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            c.title = title; c.width = w; c.minWidth = min
            c.sortDescriptorPrototype = NSSortDescriptor(key: id, ascending: true)
            table.addTableColumn(c)
        }
        col("name", Lang.T("Nom", "Name"), 280, 100)
        col("size", Lang.T("Taille", "Size"), 90, 50)
        col("date", Lang.T("Modifié", "Modified"), 120, 80)
        col("kind", Lang.T("Type", "Type"), 110, 50)
        table.sortDescriptors = [NSSortDescriptor(key: "name", ascending: true)]
        table.dataSource = self
        table.delegate = self
        table.target = self
        table.doubleAction = #selector(doubleClicked)
        table.registerForDraggedTypes([.fileURL])
        table.setDraggingSourceOperationMask([.copy, .move], forLocal: true)
        table.setDraggingSourceOperationMask([.copy], forLocal: false)
        let menu = NSMenu()
        menu.delegate = self
        table.menu = menu

        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.borderType = .noBorder

        statusLabel.font = .systemFont(ofSize: 11)
        statusLabel.textColor = .secondaryLabelColor
        statusLabel.lineBreakMode = .byTruncatingTail

        let head = NSStackView(views: [activeBar, addressRow, filterField])
        head.orientation = .vertical
        head.alignment = .leading
        head.spacing = 3
        for v in [head, scroll, statusLabel] as [NSView] { v.translatesAutoresizingMaskIntoConstraints = false; addSubview(v) }
        NSLayoutConstraint.activate([
            head.topAnchor.constraint(equalTo: topAnchor),
            head.leadingAnchor.constraint(equalTo: leadingAnchor), head.trailingAnchor.constraint(equalTo: trailingAnchor),
            activeBar.leadingAnchor.constraint(equalTo: head.leadingAnchor), activeBar.trailingAnchor.constraint(equalTo: head.trailingAnchor),
            addressRow.leadingAnchor.constraint(equalTo: head.leadingAnchor), addressRow.trailingAnchor.constraint(equalTo: head.trailingAnchor),
            filterField.leadingAnchor.constraint(equalTo: head.leadingAnchor, constant: 4), filterField.trailingAnchor.constraint(equalTo: head.trailingAnchor, constant: -4),
            scroll.topAnchor.constraint(equalTo: head.bottomAnchor, constant: 3),
            scroll.leadingAnchor.constraint(equalTo: leadingAnchor), scroll.trailingAnchor.constraint(equalTo: trailingAnchor),
            scroll.bottomAnchor.constraint(equalTo: statusLabel.topAnchor, constant: -3),
            statusLabel.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 6), statusLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -6),
            statusLabel.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -3)
        ])
        addressBox.setContentHuggingPriority(.defaultLow, for: .horizontal)
        setActive(false)
    }

    func shutdown() {
        watcher?.cancel(); watcher = nil
        sizeToken?.cancelled = true
        reloadWork?.cancel()
        generation += 1
    }

    // ======================================================================= état

    var isActive = false
    func setActive(_ a: Bool) {
        isActive = a
        activeBar.layer?.backgroundColor = a ? NSColor.controlAccentColor.cgColor : NSColor.clear.cgColor
    }

    override func viewDidChangeEffectiveAppearance() { setActive(isActive) }

    func activate() { host?.paneActivated(self) }
    func focusList() { window?.makeFirstResponder(table) }
    func focusAddress() { window?.makeFirstResponder(addressBox); addressBox.selectText(nil) }
    func focusFilter() { filterField.isHidden = false; window?.makeFirstResponder(filterField) }
    var isEditingText: Bool { table.editedRow >= 0 || window?.firstResponder is NSText }

    func setSort(key: String, ascending: Bool) {
        sortKey = key; sortAscending = ascending
        table.sortDescriptors = [NSSortDescriptor(key: key, ascending: ascending)]
    }

    var selectedItems: [FileItem] { table.selectedRowIndexes.compactMap { $0 < shown.count ? shown[$0] : nil } }
    var selectedPaths: [String] { selectedItems.map { $0.path } }

    // ======================================================================= navigation

    private static func normalize(_ path: String) -> String {
        var p = (path.trimmingCharacters(in: .whitespaces) as NSString).expandingTildeInPath
        if !p.hasPrefix("/") { p = "/" + p }
        p = (p as NSString).standardizingPath
        return p
    }

    func navigate(_ path: String, record: Bool = true, select: [String] = []) {
        let p = FilePane.normalize(path)
        guard Sys.isDirectory(p) else { NSSound.beep(); addressBox.stringValue = currentPath; return }
        let previous = currentPath
        if record, !previous.isEmpty, p != previous { back.append(previous); forward.removeAll() }
        currentPath = p
        addressBox.stringValue = p
        sizeToken?.cancelled = true
        filterText = ""; filterField.stringValue = ""; filterField.isHidden = true
        watch(p)
        load(p, select: select, revertTo: previous.isEmpty ? nil : previous)
        onPathChanged?()
        updateNavButtons()
    }

    func reload() { load(currentPath, select: selectedItems.map { $0.name }, revertTo: nil) }

    private func updateNavButtons() {
        // (les boutons restent actifs : l'action ignore simplement un historique vide)
    }

    @objc func goBack() {
        guard let p = back.popLast() else { NSSound.beep(); return }
        forward.append(currentPath); navigate(p, record: false, select: [currentPath.lastName])
    }
    @objc func goForward() {
        guard let p = forward.popLast() else { NSSound.beep(); return }
        back.append(currentPath); navigate(p, record: false)
    }
    @objc func goUp() {
        let parent = currentPath.parentPath
        if parent != currentPath { navigate(parent, select: [currentPath.lastName]) }
    }

    @objc private func addressEntered() {
        let text = addressBox.stringValue
        navigate(text)
        if Sys.isDirectory(currentPath) { remember(currentPath) }
        focusList()
    }

    private func remember(_ path: String) {
        var h = Settings.strings("History").filter { $0 != path }
        h.insert(path, at: 0)
        h = Array(h.prefix(30))
        Settings.set("History", h)
        addressBox.removeAllItems()
        addressBox.addItems(withObjectValues: h)
        addressBox.stringValue = path
    }

    func comboBoxSelectionDidChange(_ notification: Notification) {
        DispatchQueue.main.async { [weak self] in
            guard let self = self, let s = self.addressBox.objectValueOfSelectedItem as? String else { return }
            self.navigate(s); self.focusList()
        }
    }

    // ======================================================================= chargement

    private func load(_ path: String, select names: [String], revertTo: String?) {
        generation += 1
        let gen = generation
        DispatchQueue.global(qos: .userInitiated).async {
            let list = FileItem.list(path)
            DispatchQueue.main.async { [weak self] in
                guard let self = self, gen == self.generation else { return }
                guard let list = list else {
                    NSSound.beep()
                    self.statusLabel.stringValue = Lang.T("Accès refusé : ", "Access denied: ") + path
                    if let r = revertTo { self.currentPath = r; self.addressBox.stringValue = r; self.watch(r); self.onPathChanged?() }
                    return
                }
                self.items = list
                self.refilter(select: names)
                if FilePane.autoSizes { self.computeSizes(force: false) }
            }
        }
    }

    private func visible(_ it: FileItem) -> Bool {
        if it.isHidden && !FilePane.showHidden { return false }
        if filterText.isEmpty { return true }
        if filterText.contains("*") || filterText.contains("?") {
            return NSPredicate(format: "SELF LIKE[cd] %@", filterText).evaluate(with: it.name)
        }
        return it.name.localizedCaseInsensitiveContains(filterText)
    }

    private func sizeOf(_ it: FileItem) -> Int64 {
        it.hasFolderSize ? (DirSizes.get(it.path).map { $0.bytes } ?? -1) : it.size
    }

    private func sorted(_ list: [FileItem]) -> [FileItem] {
        func byName(_ a: FileItem, _ b: FileItem) -> ComparisonResult { a.name.localizedStandardCompare(b.name) }
        return list.sorted { x, y in
            if x.isDir != y.isDir { return x.isDir }
            var r: ComparisonResult
            switch sortKey {
            case "size":
                let a = sizeOf(x), b = sizeOf(y)
                r = a == b ? byName(x, y) : (a < b ? .orderedAscending : .orderedDescending)
            case "date":
                r = x.date == y.date ? byName(x, y) : (x.date < y.date ? .orderedAscending : .orderedDescending)
            case "kind":
                r = x.kind == y.kind ? byName(x, y) : x.kind.localizedStandardCompare(y.kind)
            default:
                r = byName(x, y)
            }
            return sortAscending ? r == .orderedAscending : r == .orderedDescending
        }
    }

    func refilter(select names: [String]) {
        shown = sorted(items.filter(visible))
        table.reloadData()
        var idx = IndexSet()
        if let p = pendingRename, let i = shown.firstIndex(where: { $0.name == p }) { idx.insert(i) }
        else { for (i, it) in shown.enumerated() where names.contains(it.name) { idx.insert(i) } }
        if idx.isEmpty, let k = selectAfterIndex, !shown.isEmpty { idx.insert(min(k, shown.count - 1)) }
        selectAfterIndex = nil
        if !idx.isEmpty { table.selectRowIndexes(idx, byExtendingSelection: false); table.scrollRowToVisible(idx.first!) }
        updateStatus()
        if pendingRename != nil { pendingRename = nil; DispatchQueue.main.async { [weak self] in self?.beginRename() } }
    }

    func refreshSizesDisplay() {
        table.reloadData()
        updateStatus()
    }

    private func updateStatus() {
        let sel = selectedItems
        if sel.isEmpty {
            let d = shown.filter { $0.isDir }.count
            statusLabel.stringValue = Fmt.count(d, Lang.T("dossier", "folder"), Lang.T("dossiers", "folders")) + ", "
                + Fmt.count(shown.count - d, Lang.T("fichier", "file"), Lang.T("fichiers", "files"))
            return
        }
        var total: Int64 = 0
        var unknown = false
        for it in sel { let s = sizeOf(it); if s >= 0 { total += s } else { unknown = true } }
        var text = Fmt.count(sel.count, Lang.T("sélectionné", "selected"), Lang.T("sélectionnés", "selected")) + " — " + Fmt.size(total) + (unknown ? "+" : "")
        if sel.count == 1, sel[0].hasFolderSize, let d = DirSizes.get(sel[0].path), d.complete {
            text += " — " + Fmt.count(d.files, Lang.T("fichier", "file"), Lang.T("fichiers", "files")) + ", " + Fmt.count(d.folders, Lang.T("dossier", "folder"), Lang.T("dossiers", "folders"))
        }
        statusLabel.stringValue = text
    }

    // ======================================================================= tailles de dossiers

    func computeSizes(force: Bool) {
        let sel = selectedItems.filter { $0.hasFolderSize }
        let dirs = (sel.isEmpty ? shown.filter { $0.hasFolderSize } : sel).map { $0.path }
        guard !dirs.isEmpty else { return }
        sizeToken?.cancelled = true
        let token = CancelToken()
        sizeToken = token
        DispatchQueue.global(qos: .utility).async { [weak self] in
            DirSizes.compute(folders: dirs, force: force, token: token) { path, size in
                let complete = size.complete
                DispatchQueue.main.async { self?.sizeProgress(path, complete: complete, last: path == dirs.last, token: token) }
            }
        }
    }

    private func sizeProgress(_ path: String, complete: Bool, last: Bool, token: CancelToken) {
        guard !token.cancelled else { return }
        if complete && last && sortKey == "size" { refilter(select: selectedItems.map { $0.name }); return }
        if let i = shown.firstIndex(where: { $0.path == path }) {
            table.reloadData(forRowIndexes: [i], columnIndexes: [1])
        }
        updateStatus()
    }

    // ======================================================================= table

    func numberOfRows(in tableView: NSTableView) -> Int { shown.count }

    func tableView(_ tv: NSTableView, viewFor column: NSTableColumn?, row: Int) -> NSView? {
        guard let id = column?.identifier.rawValue, row < shown.count else { return nil }
        let it = shown[row]
        if id == "name" {
            let cell = tv.makeView(withIdentifier: NSUserInterfaceItemIdentifier("name"), owner: nil) as? NameCell ?? NameCell()
            cell.textField?.stringValue = it.name
            cell.textField?.delegate = self
            cell.textField?.textColor = it.isHidden ? .secondaryLabelColor : .labelColor
            cell.imageView?.image = Icons.icon(for: it)
            return cell
        }
        let cell = tv.makeView(withIdentifier: NSUserInterfaceItemIdentifier(id), owner: nil) as? TextCell ?? TextCell(id: id, align: id == "size" ? .right : .left)
        switch id {
        case "size":
            if it.hasFolderSize {
                if let d = DirSizes.get(it.path) { cell.textField?.stringValue = d.complete ? Fmt.size(d.bytes) : "…" } else { cell.textField?.stringValue = "" }
            } else { cell.textField?.stringValue = Fmt.size(it.size) }
        case "date": cell.textField?.stringValue = Fmt.date(it.date)
        default: cell.textField?.stringValue = it.kind
        }
        cell.textField?.textColor = it.isHidden ? .secondaryLabelColor : .labelColor
        return cell
    }

    func tableView(_ tv: NSTableView, sortDescriptorsDidChange old: [NSSortDescriptor]) {
        guard let d = tv.sortDescriptors.first, let k = d.key else { return }
        sortKey = k; sortAscending = d.ascending
        refilter(select: selectedItems.map { $0.name })
    }

    func tableViewSelectionDidChange(_ notification: Notification) { updateStatus() }

    func tableView(_ tv: NSTableView, typeSelectStringFor column: NSTableColumn?, row: Int) -> String? { shown[row].name }

    @objc private func doubleClicked() {
        let r = table.clickedRow
        guard r >= 0, r < shown.count else { return }
        open(shown[r])
    }

    func open(_ it: FileItem) {
        if it.isDir { navigate(it.path) } else { Sys.open(it.path) }
    }

    func openSelected() { if let it = selectedItems.first { selectedItems.count == 1 ? open(it) : selectedItems.filter { !$0.isDir }.forEach { open($0) } } }

    func handleKey(_ e: NSEvent) -> Bool {
        let m = e.modifierFlags.intersection([.command, .shift, .option, .control])
        switch e.keyCode {
        case 36, 76: if m.isEmpty { openSelected(); return true }
        case 51: if m.isEmpty { goUp(); return true }
        case 117: deleteSelected(permanent: m.contains(.shift)); return true
        case 48:
            if m.isEmpty { host?.switchPane(from: self); return true }
            if m == .control { host?.tabCommand(.next, from: self); return true }
            if m == [.control, .shift] { host?.tabCommand(.prev, from: self); return true }
        case 120: beginRename(); return true
        case 99: computeSizes(force: true); return true
        case 118: focusAddress(); return true
        case 96: host?.transfer(.copy, from: self); return true
        case 97: host?.transfer(.move, from: self); return true
        case 98: newFolder(); return true
        case 100: deleteSelected(permanent: m.contains(.shift)); return true
        default: break
        }
        return false
    }

    func otherMouse(_ e: NSEvent) {
        switch e.buttonNumber {
        case 2:
            let r = table.row(at: table.convert(e.locationInWindow, from: nil))
            if r >= 0, r < shown.count, shown[r].isDir { host?.openInOtherPane(shown[r].path, from: self) }
        case 3: goBack()
        case 4: goForward()
        default: break
        }
    }

    // ======================================================================= opérations

    func newFolder() {
        guard !currentPath.isEmpty else { return }
        let path = Sys.unique(dir: currentPath, name: Lang.T("Nouveau dossier", "New folder"), copySuffix: false)
        do { try FileManager.default.createDirectory(atPath: path, withIntermediateDirectories: false) }
        catch { Dialogs.message(Lang.T("Création impossible", "Can't create folder"), error.localizedDescription, style: .warning); return }
        pendingRename = path.lastName
        reload()
    }

    func beginRename() {
        guard let row = table.selectedRowIndexes.first, row < shown.count,
              let cell = table.view(atColumn: 0, row: row, makeIfNecessary: true) as? NameCell, let tf = cell.textField else { return }
        tf.isEditable = true
        table.editColumn(0, row: row, with: nil, select: true)
        if let ed = tf.currentEditor() {
            let n = shown[row].name as NSString
            ed.selectedRange = NSRange(location: 0, length: shown[row].isDir ? n.length : (n.deletingPathExtension as NSString).length)
        }
    }

    func selectAll() { table.selectAll(nil) }

    func invertSelection() {
        var inv = IndexSet()
        for i in 0..<shown.count where !table.selectedRowIndexes.contains(i) { inv.insert(i) }
        table.selectRowIndexes(inv, byExtendingSelection: false)
    }

    func controlTextDidChange(_ obj: Notification) {
        guard (obj.object as AnyObject) === filterField else { return }
        filterText = filterField.stringValue
        refilter(select: selectedItems.map { $0.name })
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy sel: Selector) -> Bool {
        guard control === filterField else { return false }
        if sel == #selector(NSResponder.cancelOperation(_:)) {
            filterField.stringValue = ""; filterText = ""; filterField.isHidden = true
            refilter(select: selectedItems.map { $0.name })
            focusList(); return true
        }
        if sel == #selector(NSResponder.insertNewline(_:)) || sel == #selector(NSResponder.insertTab(_:)) { focusList(); return true }
        return false
    }

    func controlTextDidEndEditing(_ obj: Notification) {
        guard let tf = obj.object as? NSTextField, tf.superview is NameCell else { return }
        tf.isEditable = false
        let row = table.row(for: tf)
        guard row >= 0, row < shown.count else { return }
        let it = shown[row]
        let movement = obj.userInfo?["NSTextMovement"] as? Int
        let newName = tf.stringValue.trimmingCharacters(in: .whitespaces)
        if movement == NSTextMovement.cancel.rawValue || newName.isEmpty || newName == it.name { tf.stringValue = it.name; return }
        if newName.contains("/") || newName.contains(":") {
            tf.stringValue = it.name
            Dialogs.message(Lang.T("Nom invalide", "Invalid name"), Lang.T("Le nom ne peut pas contenir « / » ni « : ».", "The name can't contain “/” or “:”."), style: .warning)
            return
        }
        let dest = currentPath.joined(newName)
        if Sys.exists(dest) && dest.lowercased() != it.path.lowercased() {
            tf.stringValue = it.name
            Dialogs.message(Lang.T("Ce nom existe déjà", "That name already exists"), newName, style: .warning)
            return
        }
        if rename(it.path, dest) != 0 {
            tf.stringValue = it.name
            Dialogs.message(Lang.T("Renommage impossible", "Can't rename"), SysError.posix().message, style: .warning)
            return
        }
        DirSizes.invalidate(it.path); DirSizes.invalidate(dest)
        load(currentPath, select: [newName], revertTo: nil)
    }

    func deleteSelected(permanent: Bool) {
        let sel = selectedItems
        guard !sel.isEmpty else { return }
        if permanent {
            let what = sel.count == 1 ? "« \(sel[0].name) »" : "\(sel.count) " + Lang.T("éléments", "items")
            guard Dialogs.confirm(Lang.T("Supprimer définitivement ?", "Delete permanently?"),
                                  Lang.T("\(what) sera supprimé sans passer par la corbeille.", "\(what) will be deleted without going to the Trash."),
                                  ok: Lang.T("Supprimer", "Delete"), destructive: true) else { return }
        }
        selectAfterIndex = table.selectedRowIndexes.first
        let paths = sel.map { $0.path }
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            var errors: [String] = []
            for p in paths {
                do {
                    if permanent { try FileManager.default.removeItem(atPath: p) }
                    else { try FileManager.default.trashItem(at: URL(fileURLWithPath: p), resultingItemURL: nil) }
                } catch { errors.append(p.lastName + " : " + error.localizedDescription) }
                DirSizes.invalidate(p)
            }
            DispatchQueue.main.async {
                self?.reload()
                if !errors.isEmpty { Dialogs.message(Lang.T("Suppression impossible", "Couldn't delete"), errors.joined(separator: "\n"), style: .warning) }
            }
        }
    }

    // ======================================================================= presse-papiers

    func copySelection(cut: Bool) {
        let urls = selectedPaths.map { URL(fileURLWithPath: $0) as NSURL }
        guard !urls.isEmpty else { return }
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.writeObjects(urls)
        FilePane.cutToken = cut ? pb.changeCount : nil
    }

    func paste() {
        guard !currentPath.isEmpty else { return }
        let pb = NSPasteboard.general
        if let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
            let move = FilePane.cutToken == pb.changeCount
            host?.enqueue(move ? .move : .copy, urls.map { $0.path }, to: currentPath)
            if move { FilePane.cutToken = nil }
            return
        }
        var created: String?
        if let text = pb.string(forType: .string), !text.isEmpty {
            let p = Sys.unique(dir: currentPath, name: Lang.T("Nouveau texte.txt", "New text.txt"), copySuffix: false)
            if (try? text.write(toFile: p, atomically: true, encoding: .utf8)) != nil { created = p }
        } else if let img = NSImage(pasteboard: pb), let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
                  let jpg = rep.representation(using: .jpeg, properties: [.compressionFactor: 0.92]) {
            let p = Sys.unique(dir: currentPath, name: Lang.T("Image.jpg", "Image.jpg"), copySuffix: false)
            if (try? jpg.write(to: URL(fileURLWithPath: p))) != nil { created = p }
        }
        if let c = created { pendingRename = c.lastName; reload() } else { NSSound.beep() }
    }

    // ======================================================================= glisser-déposer

    func tableView(_ tv: NSTableView, pasteboardWriterForRow row: Int) -> NSPasteboardWriting? {
        URL(fileURLWithPath: shown[row].path) as NSURL
    }

    private func dropURLs(_ info: NSDraggingInfo) -> [String] {
        let urls = info.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]
        return (urls ?? []).map { $0.path }
    }

    private func dropTarget(_ row: Int, _ op: NSTableView.DropOperation) -> String {
        op == .on && row >= 0 && row < shown.count && shown[row].isDir ? shown[row].path : currentPath
    }

    private func dropKind(_ paths: [String], _ target: String, _ info: NSDraggingInfo) -> JobKind {
        if NSEvent.modifierFlags.contains(.option) || !info.draggingSourceOperationMask.contains(.move) { return .copy }
        guard let s = paths.first.flatMap({ Sys.stat($0) }), let d = Sys.stat(target) else { return .copy }
        return s.dev == d.dev ? .move : .copy
    }

    func tableView(_ tv: NSTableView, validateDrop info: NSDraggingInfo, proposedRow row: Int, proposedDropOperation op: NSTableView.DropOperation) -> NSDragOperation {
        let paths = dropURLs(info)
        guard !paths.isEmpty else { return [] }
        if !(op == .on && row >= 0 && row < shown.count && shown[row].isDir) { tv.setDropRow(-1, dropOperation: .on) }
        let target = dropTarget(tv.selectedRow, .on) == currentPath ? currentPath : dropTarget(row, op)
        let tgt = (op == .on && row >= 0 && row < shown.count && shown[row].isDir) ? shown[row].path : currentPath
        _ = target
        let kind = dropKind(paths, tgt, info)
        if kind == .move && paths.allSatisfy({ $0.parentPath == tgt || $0 == tgt }) { return [] }
        return kind == .move ? .move : .copy
    }

    func tableView(_ tv: NSTableView, acceptDrop info: NSDraggingInfo, row: Int, dropOperation op: NSTableView.DropOperation) -> Bool {
        let paths = dropURLs(info)
        let tgt = dropTarget(row, op)
        guard !paths.isEmpty else { return false }
        host?.enqueue(dropKind(paths, tgt, info), paths, to: tgt)
        return true
    }

    // ======================================================================= menu contextuel

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        func add(_ title: String, _ sel: Selector, key: String = "", to m: NSMenu? = nil) {
            let mi = NSMenuItem(title: title, action: sel, keyEquivalent: key)
            mi.target = self
            (m ?? menu).addItem(mi)
        }
        let sel = selectedItems
        if sel.isEmpty {
            add(Lang.T("Nouveau dossier", "New Folder"), #selector(ctxNewFolder))
            add(Lang.T("Coller", "Paste"), #selector(ctxPaste))
            menu.addItem(.separator())
            add(Lang.T("Actualiser", "Refresh"), #selector(ctxReload))
            add(Lang.T("Ouvrir un terminal ici", "Open Terminal Here"), #selector(ctxTerminal))
            add(Lang.T("Afficher dans le Finder", "Show in Finder"), #selector(ctxRevealFolder))
            return
        }
        add(Lang.T("Ouvrir", "Open"), #selector(ctxOpen))
        if sel.count == 1, !sel[0].isDir {
            let sub = NSMenu()
            let apps = NSWorkspace.shared.urlsForApplications(toOpen: URL(fileURLWithPath: sel[0].path))
            for a in apps.prefix(15) {
                let mi = NSMenuItem(title: FileManager.default.displayName(atPath: a.path), action: #selector(ctxOpenWith(_:)), keyEquivalent: "")
                mi.target = self; mi.representedObject = a
                let icon = NSWorkspace.shared.icon(forFile: a.path); icon.size = NSSize(width: 16, height: 16)
                mi.image = icon
                sub.addItem(mi)
            }
            if !apps.isEmpty {
                let parent = NSMenuItem(title: Lang.T("Ouvrir avec", "Open With"), action: nil, keyEquivalent: "")
                parent.submenu = sub
                menu.addItem(parent)
            }
        }
        if sel.count == 1, sel[0].isDir { add(Lang.T("Ouvrir dans l'autre panneau", "Open in Other Pane"), #selector(ctxOpenOther)) }
        add(Lang.T("Afficher dans le Finder", "Show in Finder"), #selector(ctxReveal))
        add(Lang.T("Lire les informations", "Get Info"), #selector(ctxInfo))
        menu.addItem(.separator())
        add(Lang.T("Copier", "Copy"), #selector(ctxCopy))
        add(Lang.T("Couper", "Cut"), #selector(ctxCut))
        add(Lang.T("Coller", "Paste"), #selector(ctxPaste))
        add(Lang.T("Copier le chemin", "Copy Path"), #selector(ctxCopyPath))
        menu.addItem(.separator())
        add(Lang.T("Renommer", "Rename"), #selector(ctxRename))
        add(Lang.T("Mettre à la corbeille", "Move to Trash"), #selector(ctxTrash))
        if sel.contains(where: { $0.hasFolderSize }) { add(Lang.T("Calculer les tailles", "Compute Sizes"), #selector(ctxSizes)) }
    }

    @objc private func ctxNewFolder() { newFolder() }
    @objc private func ctxPaste() { paste() }
    @objc private func ctxReload() { reload() }
    @objc private func ctxTerminal() { Sys.openTerminal(at: currentPath) }
    @objc private func ctxRevealFolder() { Sys.open(currentPath) }
    @objc private func ctxOpen() { openSelected() }
    @objc private func ctxOpenWith(_ s: NSMenuItem) {
        guard let app = s.representedObject as? URL else { return }
        NSWorkspace.shared.open(selectedPaths.map { URL(fileURLWithPath: $0) }, withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration())
    }
    @objc private func ctxOpenOther() { if let it = selectedItems.first { host?.openInOtherPane(it.path, from: self) } }
    @objc private func ctxReveal() { Sys.reveal(selectedPaths) }
    @objc private func ctxInfo() { selectedPaths.forEach { Sys.getInfo($0) } }
    @objc private func ctxCopy() { copySelection(cut: false) }
    @objc private func ctxCut() { copySelection(cut: true) }
    @objc private func ctxCopyPath() {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(selectedPaths.joined(separator: "\n"), forType: .string)
    }
    @objc private func ctxRename() { beginRename() }
    @objc private func ctxTrash() { deleteSelected(permanent: false) }
    @objc private func ctxSizes() { computeSizes(force: true) }

    // ======================================================================= surveillance du dossier

    private func watch(_ path: String) {
        watcher?.cancel(); watcher = nil
        let fd = Darwin.open(path, O_EVTONLY)
        guard fd >= 0 else { return }
        let src = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .delete, .rename], queue: .main)
        src.setEventHandler { [weak self] in self?.scheduleReload() }
        src.setCancelHandler { close(fd) }
        src.resume()
        watcher = src
    }

    private func scheduleReload() {
        reloadWork?.cancel()
        let w = DispatchWorkItem { [weak self] in
            guard let self = self else { return }
            if self.table.editedRow >= 0 { self.scheduleReload(); return }
            self.reload()
        }
        reloadWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4, execute: w)
    }
}
