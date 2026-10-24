import AppKit

final class TabItemView: NSView {
    private let label = NSTextField(labelWithString: "")
    private let closeButton = NSButton()
    var onSelect: (() -> Void)?
    var onClose: (() -> Void)?
    var isSelected = false { didSet { refresh() } }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.cornerRadius = 5
        label.font = .systemFont(ofSize: 12)
        label.lineBreakMode = .byTruncatingMiddle
        label.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        closeButton.image = NSImage(systemSymbolName: "xmark", accessibilityDescription: Lang.T("Fermer l'onglet", "Close tab"))
        closeButton.isBordered = false
        closeButton.imageScaling = .scaleProportionallyDown
        closeButton.target = self; closeButton.action = #selector(closeClicked)
        closeButton.widthAnchor.constraint(equalToConstant: 12).isActive = true
        closeButton.heightAnchor.constraint(equalToConstant: 12).isActive = true
        let row = NSStackView(views: [label, closeButton])
        row.spacing = 6
        row.edgeInsets = NSEdgeInsets(top: 3, left: 8, bottom: 3, right: 6)
        row.translatesAutoresizingMaskIntoConstraints = false
        addSubview(row)
        NSLayoutConstraint.activate([
            row.leadingAnchor.constraint(equalTo: leadingAnchor), row.trailingAnchor.constraint(equalTo: trailingAnchor),
            row.topAnchor.constraint(equalTo: topAnchor), row.bottomAnchor.constraint(equalTo: bottomAnchor),
            widthAnchor.constraint(lessThanOrEqualToConstant: 200)
        ])
        refresh()
    }
    required init?(coder: NSCoder) { fatalError() }

    var title: String { get { label.stringValue } set { label.stringValue = newValue; toolTip = newValue } }

    private func refresh() {
        layer?.backgroundColor = isSelected ? NSColor.controlAccentColor.withAlphaComponent(0.25).cgColor : NSColor.clear.cgColor
        label.textColor = isSelected ? .labelColor : .secondaryLabelColor
    }
    override func viewDidChangeEffectiveAppearance() { refresh() }
    override func mouseDown(with event: NSEvent) { onSelect?() }
    override func otherMouseDown(with event: NSEvent) { if event.buttonNumber == 2 { onClose?() } }
    @objc private func closeClicked() { onClose?() }
}

/// Un jeu d'onglets par côté ; chaque onglet est un FilePane.
final class PaneTabs: NSView {
    private(set) var panes: [FilePane] = []
    private(set) var current: FilePane!
    private let bar = NSStackView()
    private let holder = NSView()
    private var views: [ObjectIdentifier: TabItemView] = [:]
    private let makePane: () -> FilePane
    var onCurrentChanged: ((FilePane) -> Void)?

    init(makePane: @escaping () -> FilePane) {
        self.makePane = makePane
        super.init(frame: .zero)
        bar.orientation = .horizontal
        bar.spacing = 2
        bar.alignment = .centerY
        bar.edgeInsets = NSEdgeInsets(top: 2, left: 4, bottom: 2, right: 4)
        let plus = NSButton()
        plus.image = NSImage(systemSymbolName: "plus", accessibilityDescription: Lang.T("Nouvel onglet", "New tab"))
        plus.isBordered = false
        plus.toolTip = Lang.T("Nouvel onglet (⌘T)", "New tab (⌘T)")
        plus.target = self; plus.action = #selector(plusClicked)
        let barRow = NSStackView(views: [bar, plus])
        barRow.spacing = 2
        barRow.alignment = .centerY
        for v in [barRow, holder] as [NSView] { v.translatesAutoresizingMaskIntoConstraints = false; addSubview(v) }
        NSLayoutConstraint.activate([
            barRow.topAnchor.constraint(equalTo: topAnchor), barRow.leadingAnchor.constraint(equalTo: leadingAnchor),
            barRow.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor),
            barRow.heightAnchor.constraint(equalToConstant: 26),
            holder.topAnchor.constraint(equalTo: barRow.bottomAnchor),
            holder.leadingAnchor.constraint(equalTo: leadingAnchor), holder.trailingAnchor.constraint(equalTo: trailingAnchor),
            holder.bottomAnchor.constraint(equalTo: bottomAnchor)
        ])
    }
    required init?(coder: NSCoder) { fatalError() }

    @objc private func plusClicked() { newTab(current?.currentPath ?? Sys_home).activate() }
    private var Sys_home: String { NSHomeDirectory() }

    @discardableResult
    func newTab(_ path: String) -> FilePane {
        let pane = makePane()
        let tab = TabItemView()
        tab.onSelect = { [weak self, weak pane] in if let p = pane { self?.select(p); p.activate(); p.focusList() } }
        tab.onClose = { [weak self, weak pane] in if let p = pane { self?.closeTab(p) } }
        panes.append(pane)
        views[ObjectIdentifier(pane)] = tab
        bar.addArrangedSubview(tab)
        pane.onPathChanged = { [weak tab, weak pane] in
            guard let p = pane else { return }
            tab?.title = p.currentPath == "/" ? "/" : p.currentPath.lastName
        }
        pane.navigate(path)
        select(pane)
        return pane
    }

    func select(_ pane: FilePane) {
        guard panes.contains(where: { $0 === pane }) else { return }
        current = pane
        holder.subviews.forEach { $0.removeFromSuperview() }
        pane.translatesAutoresizingMaskIntoConstraints = false
        holder.addSubview(pane)
        NSLayoutConstraint.activate([
            pane.leadingAnchor.constraint(equalTo: holder.leadingAnchor), pane.trailingAnchor.constraint(equalTo: holder.trailingAnchor),
            pane.topAnchor.constraint(equalTo: holder.topAnchor), pane.bottomAnchor.constraint(equalTo: holder.bottomAnchor)
        ])
        for p in panes { views[ObjectIdentifier(p)]?.isSelected = p === pane }
        onCurrentChanged?(pane)
    }

    func closeTab(_ pane: FilePane) {
        guard panes.count > 1, let i = panes.firstIndex(where: { $0 === pane }) else { return }
        panes.remove(at: i)
        if let v = views.removeValue(forKey: ObjectIdentifier(pane)) { bar.removeArrangedSubview(v); v.removeFromSuperview() }
        pane.shutdown()
        if current === pane {
            select(panes[min(i, panes.count - 1)])
            current.activate(); current.focusList()
        }
    }

    func cycle(_ delta: Int) {
        guard panes.count > 1, let i = panes.firstIndex(where: { $0 === current }) else { return }
        let p = panes[(i + delta + panes.count) % panes.count]
        select(p); p.activate(); p.focusList()
    }
}
