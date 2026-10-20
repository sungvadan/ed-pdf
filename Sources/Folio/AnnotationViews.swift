import PDFKit
import SwiftUI

/// Tool picker shown above the page canvas.
struct AnnotationBar: View {
    @Bindable var viewer: ViewerModel

    var body: some View {
        HStack(spacing: 12) {
            Picker("Outil", selection: $viewer.tool) {
                ForEach(AnnotationTool.allCases) { tool in
                    Image(systemName: tool.symbol)
                        .help(tool.label)
                        .tag(tool)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()

            if viewer.tool != .select {
                ColorPicker("Couleur", selection: $viewer.toolColor, supportsOpacity: false)
                    .labelsHidden()
                    .help("Couleur de l'outil")
            }

            if viewer.tool.usesLineWidth {
                Picker("Épaisseur", selection: $viewer.lineWidth) {
                    ForEach([1, 2, 4, 8] as [CGFloat], id: \.self) { width in
                        Text("\(Int(width)) pt").tag(width)
                    }
                }
                .fixedSize()
                .help("Épaisseur du trait")
            }

            Spacer()

            Text(viewer.tool.label)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(.bar)
        .overlay(alignment: .bottom) { Divider() }
    }
}

/// Properties of the selected annotation.
struct AnnotationInspector: View {
    let viewer: ViewerModel
    let annotation: PDFAnnotation

    var body: some View {
        let _ = viewer.revision
        Form {
            Section(annotation.displayName) {
                ColorPicker("Couleur", selection: color, supportsOpacity: false)

                if annotation.hasStroke {
                    LabeledContent("Épaisseur") {
                        Slider(value: lineWidth, in: 0.5...20)
                    }
                }

                if annotation.isFreeText {
                    Stepper("Taille : \(Int(fontSize.wrappedValue)) pt", value: fontSize, in: 6...96)
                }
            }

            if annotation.hasEditableText {
                Section(annotation.isFreeText ? "Texte" : "Commentaire") {
                    TextEditor(text: contents)
                        .font(.body)
                        .frame(minHeight: 120)
                }
            }

            Section {
                Button("Supprimer l'annotation", role: .destructive) { viewer.deleteSelection() }
            }
        }
        .formStyle(.grouped)
    }

    private var color: Binding<Color> {
        Binding {
            Color(nsColor: annotation.isFreeText ? (annotation.fontColor ?? .black) : annotation.color.withAlphaComponent(1))
        } set: { newValue in
            let color = NSColor(newValue)
            viewer.update(annotation, actionName: "Couleur") { a in
                if a.isFreeText {
                    a.fontColor = color
                } else {
                    a.color = a.subtype == "Highlight" ? color.withAlphaComponent(0.5) : color
                }
            }
        }
    }

    private var lineWidth: Binding<CGFloat> {
        Binding {
            annotation.border?.lineWidth ?? 1
        } set: { newValue in
            viewer.update(annotation, actionName: "Épaisseur") { a in
                guard let border = a.border else { return }
                border.lineWidth = newValue
                a.border = border
            }
        }
    }

    private var fontSize: Binding<CGFloat> {
        Binding {
            annotation.font?.pointSize ?? 14
        } set: { newValue in
            viewer.update(annotation, actionName: "Taille du texte") { a in
                a.font = (a.font ?? .systemFont(ofSize: 14)).withSize(newValue)
            }
        }
    }

    private var contents: Binding<String> {
        Binding {
            annotation.contents ?? ""
        } set: { newValue in
            viewer.update(annotation, actionName: "Modifier le texte") { $0.contents = newValue }
        }
    }
}
