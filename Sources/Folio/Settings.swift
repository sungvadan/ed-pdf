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
    @State private var language = AppLanguage.override ?? ""
    private let launchLanguage = AppLanguage.override ?? ""

    var body: some View {
        Form {
            Picker("Langue", selection: $language) {
                Text("Langue du système").tag("")
                Divider()
                ForEach(AppLanguage.codes, id: \.self) { Text(AppLanguage.name($0)).tag($0) }
            }
            .onChange(of: language) { AppLanguage.override = language.isEmpty ? nil : language }
            if language != launchLanguage {
                Text("La nouvelle langue sera utilisée au prochain lancement de pdf-svd.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Divider()
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
