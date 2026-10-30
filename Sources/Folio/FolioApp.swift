import SwiftUI

@main
struct FolioApp: App {
    init() {
        AppSettings.migrateFromPreviousName()
        SelfTest.runIfRequested()
    }

    var body: some Scene {
        DocumentGroup(newDocument: { PDFFileDocument() }) { file in
            ContentView(document: file.document)
        }
        .commands {
            ViewerCommands()
            ImportFromDevicesCommands()
        }

        Settings {
            SettingsView()
        }
    }
}
