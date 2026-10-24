import AppKit

enum MenuBuilder {
    static func build(_ c: MainController) -> NSMenu {
        let main = NSMenu()
        func fkey(_ code: Int) -> String { String(UnicodeScalar(code)!) }
        let up = fkey(NSUpArrowFunctionKey), down = fkey(NSDownArrowFunctionKey)

        @discardableResult
        func add(_ m: NSMenu, _ title: String, _ sel: Selector?, _ key: String = "", _ mods: NSEvent.ModifierFlags = [.command], target: AnyObject? = c) -> NSMenuItem {
            let mi = NSMenuItem(title: title, action: sel, keyEquivalent: key)
            mi.keyEquivalentModifierMask = mods
            if sel != nil { mi.target = target }
            m.addItem(mi)
            return mi
        }
        func submenu(_ title: String) -> NSMenu {
            let item = NSMenuItem(); main.addItem(item)
            let m = NSMenu(title: title); item.submenu = m
            return m
        }

        // S3bopus
        let app = submenu("S3bopus")
        add(app, Lang.T("À propos de S3bopus", "About S3bopus"), #selector(MainController.about))
        app.addItem(.separator())
        add(app, Lang.T("Masquer S3bopus", "Hide S3bopus"), #selector(NSApplication.hide(_:)), "h", target: NSApp)
        add(app, Lang.T("Masquer les autres", "Hide Others"), #selector(NSApplication.hideOtherApplications(_:)), "h", [.command, .option], target: NSApp)
        add(app, Lang.T("Tout afficher", "Show All"), #selector(NSApplication.unhideAllApplications(_:)), target: NSApp)
        app.addItem(.separator())
        add(app, Lang.T("Quitter S3bopus", "Quit S3bopus"), #selector(NSApplication.terminate(_:)), "q", target: NSApp)

        // Fichier
        let file = submenu(Lang.T("Fichier", "File"))
        add(file, Lang.T("Nouveau dossier", "New Folder"), #selector(MainController.newFolder), "n", [.command, .shift])
        add(file, Lang.T("Nouvel onglet", "New Tab"), #selector(MainController.newTab), "t")
        add(file, Lang.T("Fermer l'onglet", "Close Tab"), #selector(MainController.closeTab), "w")
        file.addItem(.separator())
        add(file, Lang.T("Ouvrir", "Open"), #selector(MainController.openSelection), down)
        add(file, Lang.T("Renommer", "Rename"), #selector(MainController.rename), "r", [.command, .option])
        add(file, Lang.T("Mettre à la corbeille", "Move to Trash"), #selector(MainController.trash), "\u{8}")
        add(file, Lang.T("Supprimer définitivement…", "Delete Permanently…"), #selector(MainController.deletePermanently), "\u{8}", [.command, .option])
        file.addItem(.separator())
        add(file, Lang.T("Lire les informations", "Get Info"), #selector(MainController.getInfo), "i")
        add(file, Lang.T("Afficher dans le Finder", "Show in Finder"), #selector(MainController.reveal), "r", [.command, .shift])
        add(file, Lang.T("Ouvrir un terminal ici", "Open Terminal Here"), #selector(MainController.openTerminal))

        // Édition
        let edit = submenu(Lang.T("Édition", "Edit"))
        add(edit, Lang.T("Couper", "Cut"), Selector(("cut:")), "x", target: nil)
        add(edit, Lang.T("Copier", "Copy"), Selector(("copy:")), "c", target: nil)
        add(edit, Lang.T("Coller", "Paste"), Selector(("paste:")), "v", target: nil)
        add(edit, Lang.T("Tout sélectionner", "Select All"), Selector(("selectAll:")), "a", target: nil)
        add(edit, Lang.T("Inverser la sélection", "Invert Selection"), #selector(MainController.invertSelection))
        edit.addItem(.separator())
        add(edit, Lang.T("Filtrer le panneau", "Filter Pane"), #selector(MainController.focusFilter), "f")

        // Actions
        let act = submenu(Lang.T("Actions", "Actions"))
        add(act, Lang.T("Copier vers l'autre panneau", "Copy to Other Pane"), #selector(MainController.copyToOther), "c", [.command, .option])
        add(act, Lang.T("Déplacer vers l'autre panneau", "Move to Other Pane"), #selector(MainController.moveToOther), "x", [.command, .option])
        add(act, Lang.T("Calculer la taille des dossiers", "Compute Folder Sizes"), #selector(MainController.computeSizes), "i", [.command, .option])
        act.addItem(.separator())
        add(act, Lang.T("Échanger les panneaux", "Swap Panes"), #selector(MainController.swapPanes), "u")
        add(act, Lang.T("Même dossier dans l'autre panneau", "Same Folder in Other Pane"), #selector(MainController.syncOther), "=")
        add(act, Lang.T("Autre panneau", "Switch Pane"), #selector(MainController.switchPaneAction))

        // Aller
        let go = submenu(Lang.T("Aller", "Go"))
        add(go, Lang.T("Précédent", "Back"), #selector(MainController.goBack), "[")
        add(go, Lang.T("Suivant", "Forward"), #selector(MainController.goForward), "]")
        add(go, Lang.T("Dossier parent", "Enclosing Folder"), #selector(MainController.goUp), up)
        add(go, Lang.T("Barre d'adresse", "Address Bar"), #selector(MainController.focusAddress), "l")
        go.addItem(.separator())
        let places: [(String, String, String, NSEvent.ModifierFlags)] = [
            (Lang.T("Départ", "Home"), NSHomeDirectory(), "h", [.command, .shift]),
            (Lang.T("Bureau", "Desktop"), NSHomeDirectory() + "/Desktop", "d", [.command, .shift]),
            (Lang.T("Documents", "Documents"), NSHomeDirectory() + "/Documents", "o", [.command, .shift]),
            (Lang.T("Téléchargements", "Downloads"), NSHomeDirectory() + "/Downloads", "l", [.command, .option]),
            (Lang.T("Applications", "Applications"), "/Applications", "a", [.command, .shift])
        ]
        for (t, p, k, m) in places { add(go, t, #selector(MainController.goHome(_:)), k, m).representedObject = p }
        go.addItem(.separator())
        add(go, Lang.T("Onglet suivant", "Next Tab"), #selector(MainController.nextTab), "]", [.command, .shift])
        add(go, Lang.T("Onglet précédent", "Previous Tab"), #selector(MainController.prevTab), "[", [.command, .shift])

        // Présentation
        let view = submenu(Lang.T("Présentation", "View"))
        c.showHiddenItem = add(view, Lang.T("Afficher les éléments masqués", "Show Hidden Items"), #selector(MainController.toggleHidden), ".", [.command, .shift])
        c.showHiddenItem?.state = FilePane.showHidden ? .on : .off
        c.autoSizesItem = add(view, Lang.T("Calculer automatiquement la taille des dossiers", "Compute Folder Sizes Automatically"), #selector(MainController.toggleAutoSizes))
        c.autoSizesItem?.state = FilePane.autoSizes ? .on : .off
        c.queueItem = add(view, Lang.T("File de transferts", "Transfer Queue"), #selector(MainController.toggleQueue), "j")
        c.fkeysItem = add(view, Lang.T("Barre des touches de fonction", "Function Key Bar"), #selector(MainController.toggleFKeys))
        c.fkeysItem?.state = Settings.bool("FKeys", true) ? .on : .off
        add(view, Lang.T("Actualiser", "Refresh"), #selector(MainController.reload), "r")
        view.addItem(.separator())
        let app2 = NSMenuItem(title: Lang.T("Apparence", "Appearance"), action: nil, keyEquivalent: ""); view.addItem(app2)
        let am = NSMenu(); app2.submenu = am
        for (i, t) in [Lang.T("Système", "System"), Lang.T("Clair", "Light"), Lang.T("Sombre", "Dark")].enumerated() {
            let mi = add(am, t, #selector(MainController.setAppearance(_:)), "", [])
            mi.tag = i; mi.state = Settings.int("Appearance", 0) == i ? .on : .off
        }
        let lang = NSMenuItem(title: Lang.T("Langue", "Language"), action: nil, keyEquivalent: ""); view.addItem(lang)
        let lm = NSMenu(); lang.submenu = lm
        for (i, t) in ["Français", "English"].enumerated() {
            let mi = add(lm, t, #selector(MainController.setLanguage(_:)), "", [])
            mi.tag = i; mi.state = (Lang.en ? 1 : 0) == i ? .on : .off
        }

        // Fenêtre
        let win = submenu(Lang.T("Fenêtre", "Window"))
        add(win, Lang.T("Placer dans le Dock", "Minimize"), #selector(NSWindow.performMiniaturize(_:)), "m", target: nil)
        add(win, Lang.T("Agrandir", "Zoom"), #selector(NSWindow.performZoom(_:)), target: nil)
        NSApp.windowsMenu = win
        return main
    }
}
