import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    var controller: MainController!

    func applicationDidFinishLaunching(_ n: Notification) {
        MainController.applyAppearance()
        controller = MainController()
        NSApp.mainMenu = MenuBuilder.build(controller)
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        controller.confirmQuit() ? .terminateNow : .terminateCancel
    }

    func applicationWillTerminate(_ n: Notification) { controller.saveSettings() }
}

if CommandLine.arguments.contains("--selftest") {
    exit(SelfTest.run())
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
