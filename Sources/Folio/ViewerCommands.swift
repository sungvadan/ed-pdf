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
                    Button(layout.rawValue) { viewer?.layout = layout }
                }
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
                Button("Résultat suivant") { viewer?.nextResult() }
                    .keyboardShortcut("g")
                Button("Résultat précédent") { viewer?.previousResult() }
                    .keyboardShortcut("g", modifiers: [.command, .shift])
            }
            .disabled(viewer == nil)
        }
    }
}
