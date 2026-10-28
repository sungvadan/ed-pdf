import Foundation

/// Translated UI text. The French source text is the key; translations live in
/// `Resources/Localizations/<lang>.lproj/Localizable.strings` (checked by `scripts/l10n.py`).
/// Use `%d`, `%@`… for values: never interpolate, the key would no longer match.
/// SwiftUI literals (`Text("…")`, `Button("…")`, `.help("…")`) are translated by SwiftUI itself.
func tr(_ key: String, _ arguments: CVarArg...) -> String {
    let format = Bundle.main.localizedString(forKey: key, value: nil, table: nil)
    return arguments.isEmpty ? format : String(format: format, arguments: arguments)
}

enum AppLanguage {
    /// Languages offered in the settings; the first one is the source language.
    static let codes = ["fr", "en", "de", "es", "it", "pt-BR", "ja", "zh-Hans"]

    /// The name of a language in that language ("Deutsch", "日本語").
    static func name(_ code: String) -> String {
        (Locale(identifier: code).localizedString(forIdentifier: code) ?? code).capitalized(with: Locale(identifier: code))
    }

    /// The language chosen in the settings, or nil to follow the system; applied at next launch.
    static var override: String? {
        // The app's own domain only: `UserDefaults.standard` would also return the system list.
        get {
            let domain = Bundle.main.bundleIdentifier.flatMap(UserDefaults.standard.persistentDomain(forName:))
            return (domain?["AppleLanguages"] as? [String])?.first
        }
        set {
            if let newValue {
                UserDefaults.standard.set([newValue], forKey: "AppleLanguages")
            } else {
                UserDefaults.standard.removeObject(forKey: "AppleLanguages")
            }
        }
    }
}
