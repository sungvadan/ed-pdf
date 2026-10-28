import SwiftUI

struct ViewerKey: FocusedValueKey {
    typealias Value = ViewerModel
}

extension FocusedValues {
    var viewer: ViewerModel? {
        get { self[ViewerKey.self] }
        set { self[ViewerKey.self] = newValue }
    }
}

/// Menu bar commands acting on the focused window's viewer.
struct ViewerCommands: Commands {
    @FocusedValue(\.viewer) private var viewer

    var body: some Commands {
        CommandGroup(after: .importExport) {
            Group {
                Button("Exporter pour envoi…") { viewer?.showsSendExport = true }
                    .keyboardShortcut("e", modifiers: [.command, .shift])
                Button("Exporter en images…") { viewer?.showsImageExport = true }
                Button("Réduire la taille…") { viewer?.reduceFileSize() }
            }
            .disabled(viewer == nil)
        }

        CommandGroup(after: .toolbar) {
            Divider()
            Group {
                Button("Zoom avant") { viewer?.zoomIn() }
                    .keyboardShortcut("+")
                Button("Zoom arrière") { viewer?.zoomOut() }
                    .keyboardShortcut("-")
                Button("Taille réelle") { viewer?.actualSize() }
                    .keyboardShortcut("0")
                Button("Ajuster à la fenêtre") { viewer?.zoomToFit() }
                    .keyboardShortcut("9")
                Divider()
                ForEach(PageLayout.allCases) { layout in
                    Button(tr(layout.rawValue)) { viewer?.layout = layout }
                }
            }
            .disabled(viewer == nil)
        }

        CommandMenu("Pages") {
            Group {
                Button("Pivoter à gauche") { viewer?.rotate(viewer?.targetPages ?? [], by: -90) }
                    .keyboardShortcut("l")
                Button("Pivoter à droite") { viewer?.rotate(viewer?.targetPages ?? [], by: 90) }
                    .keyboardShortcut("r")
                Divider()
                Button("Dupliquer") { viewer?.duplicatePages(viewer?.targetPages ?? []) }
                    .keyboardShortcut("d")
                Button("Insérer une page vierge") { viewer?.insertBlankPage() }
                    .keyboardShortcut("n", modifiers: [.command, .option])
                Button("Insérer depuis un fichier…") { viewer?.chooseFilesToInsert() }
                    .keyboardShortcut("i", modifiers: [.command, .option])
                Button("Extraire…") { viewer?.extractPages() }
                    .keyboardShortcut("e", modifiers: [.command, .option])
                Divider()
                Button("Aplatir les annotations des pages sélectionnées") { viewer?.flattenAnnotations(of: viewer?.targetPages ?? []) }
                Button("Aplatir tout le document") { viewer?.flattenAnnotations(of: viewer?.pages ?? []) }
                Divider()
                Button("Supprimer") { viewer?.deletePages(viewer?.targetPages ?? []) }
                    .keyboardShortcut(.delete, modifiers: [.command, .option])
            }
            .disabled(viewer == nil)
        }

        CommandMenu("Aller") {
            Group {
                Button("Page précédente") { viewer?.previousPage() }
                    .keyboardShortcut(.upArrow, modifiers: [.command, .option])
                Button("Page suivante") { viewer?.nextPage() }
                    .keyboardShortcut(.downArrow, modifiers: [.command, .option])
                Button("Première page") { viewer?.firstPage() }
                    .keyboardShortcut(.upArrow, modifiers: .command)
                Button("Dernière page") { viewer?.lastPage() }
                    .keyboardShortcut(.downArrow, modifiers: .command)
                Divider()
                Button("Ajouter un signet") { viewer?.addBookmark() }
                    .keyboardShortcut("b")
                Divider()
                Button("Résultat suivant") { viewer?.nextResult() }
                    .keyboardShortcut("g")
                Button("Résultat précédent") { viewer?.previousResult() }
                    .keyboardShortcut("g", modifiers: [.command, .shift])
            }
            .disabled(viewer == nil)
        }
    }
}
