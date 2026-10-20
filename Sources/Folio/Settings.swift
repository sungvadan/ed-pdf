import AppKit
import SwiftUI

enum AppSettings {
    static let authorNameKey = "authorName"

    /// Name written in new annotations; nil when the user cleared it.
    static var authorName: String? {
        let name = UserDefaults.standard.string(forKey: authorNameKey) ?? NSFullUserName()
        return name.trimmingCharacters(in: .whitespaces).isEmpty ? nil : name
    }
}

struct SettingsView: View {
    @AppStorage(AppSettings.authorNameKey) private var authorName = NSFullUserName()

    var body: some View {
        Form {
            TextField("Nom d'auteur des annotations", text: $authorName)
            Text("Inscrit dans chaque nouvelle annotation et visible par les destinataires du PDF. Laissez vide pour ne pas inscrire de nom.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(20)
        .frame(width: 460)
    }
}
