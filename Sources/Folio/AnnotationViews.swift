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

            InsertMenus(viewer: viewer)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(.bar)
        .overlay(alignment: .bottom) { Divider() }
    }
}

/// Signature, stamp and image insertion.
private struct InsertMenus: View {
    let viewer: ViewerModel
    private let store = SignatureStore.shared
    private let stampStore = CustomStampStore.shared

    var body: some View {
        HStack(spacing: 8) {
            Menu {
                ForEach(store.signatures) { signature in
                    Button {
                        viewer.placeSignature(signature)
                    } label: {
                        Image(nsImage: signature.preview())
                    }
                }
                if !store.signatures.isEmpty { Divider() }
                Button("Nouvelle signature…") { viewer.showsSignaturePad = true }
                Button("Depuis le papier (caméra ou image)…") { viewer.showsSignatureCapture = true }
                if !store.signatures.isEmpty {
                    Menu("Supprimer une signature") {
                        ForEach(store.signatures) { signature in
                            Button { store.remove(signature) } label: { Image(nsImage: signature.preview()) }
                        }
                    }
                }
            } label: {
                Label("Signer", systemImage: "signature")
            }
            .help("Signature")

            Menu {
                ForEach(Stamp.allCases) { stamp in
                    Button(stamp.label) { viewer.placeStamp(stamp) }
                }
                if !stampStore.stamps.isEmpty {
                    Divider()
                    ForEach(stampStore.stamps) { stamp in
                        Button(stamp.text) { viewer.placeStamp(text: stamp.text, color: stamp.color) }
                    }
                }
                Divider()
                Button("Nouveau tampon…") { viewer.showsStampEditor = true }
                if !stampStore.stamps.isEmpty {
                    Menu("Supprimer un tampon") {
                        ForEach(stampStore.stamps) { stamp in
                            Button(stamp.text) { stampStore.remove(stamp) }
                        }
                    }
                }
            } label: {
                Label("Tampon", systemImage: "seal")
            }
            .help("Tampon")

            Button {
                viewer.chooseImageToPlace()
            } label: {
                Label("Image", systemImage: "photo")
            }
            .help("Placer une image")
        }
        .fixedSize()
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

            }

            if annotation.isMovable {
                SizeSection(viewer: viewer, annotation: annotation)
            }

            if let textBox = annotation as? FreeTextAnnotation {
                TextFormatSection(viewer: viewer, textBox: textBox)
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

    private var contents: Binding<String> {
        Binding {
            annotation.contents ?? ""
        } set: { newValue in
            viewer.update(annotation, actionName: "Modifier le texte") { $0.contents = newValue }
        }
    }
}

/// Font, emphasis, alignment and background of a free text box.
private struct TextFormatSection: View {
    let viewer: ViewerModel
    let textBox: FreeTextAnnotation

    var body: some View {
        let _ = viewer.revision
        Section("Mise en forme") {
            Picker("Police", selection: family) {
                ForEach(families, id: \.self) { Text($0).tag($0) }
            }

            Stepper("Taille : \(Int(fontSize.wrappedValue)) pt", value: fontSize, in: 6...96)

            HStack {
                Toggle(isOn: trait(.boldFontMask, isOn: textBox.isBold, name: "Gras")) {
                    Image(systemName: "bold")
                }
                .help("Gras")
                Toggle(isOn: trait(.italicFontMask, isOn: textBox.isItalic, name: "Italique")) {
                    Image(systemName: "italic")
                }
                .help("Italique")
                Toggle(isOn: decoration(\.isUnderlined, name: "Souligné")) {
                    Image(systemName: "underline")
                }
                .help("Souligné")
                Toggle(isOn: decoration(\.isStruckThrough, name: "Barré")) {
                    Image(systemName: "strikethrough")
                }
                .help("Barré")
            }
            .toggleStyle(.button)

            Picker("Alignement", selection: alignment) {
                Image(systemName: "text.alignleft").tag(NSTextAlignment.left)
                Image(systemName: "text.aligncenter").tag(NSTextAlignment.center)
                Image(systemName: "text.alignright").tag(NSTextAlignment.right)
            }
            .pickerStyle(.segmented)

            Toggle("Fond", isOn: hasBackground)
            if hasBackground.wrappedValue {
                ColorPicker("Couleur du fond", selection: background, supportsOpacity: true)
            }
        }
    }

    private var currentFont: NSFont { textBox.font ?? .systemFont(ofSize: 14) }

    private var families: [String] {
        let current = currentFont.familyName ?? "Helvetica"
        let list = FreeTextAnnotation.fontFamilies
        return list.contains(current) ? list : [current] + list
    }

    private func change(_ name: String, _ body: @escaping (FreeTextAnnotation) -> Void) {
        viewer.update(textBox, actionName: name) { a in
            guard let box = a as? FreeTextAnnotation else { return }
            box.adoptFolioStyle()
            body(box)
        }
    }

    private var family: Binding<String> {
        Binding {
            currentFont.familyName ?? "Helvetica"
        } set: { newValue in
            change("Police") { $0.font = NSFontManager.shared.convert($0.font ?? .systemFont(ofSize: 14), toFamily: newValue) }
        }
    }

    private var fontSize: Binding<CGFloat> {
        Binding {
            currentFont.pointSize
        } set: { newValue in
            change("Taille du texte") { $0.font = ($0.font ?? .systemFont(ofSize: 14)).withSize(newValue) }
        }
    }

    private func trait(_ mask: NSFontTraitMask, isOn: Bool, name: String) -> Binding<Bool> {
        Binding {
            isOn
        } set: { on in
            change(name) { box in
                let font = box.font ?? .systemFont(ofSize: 14)
                box.font = on
                    ? NSFontManager.shared.convert(font, toHaveTrait: mask)
                    : NSFontManager.shared.convert(font, toNotHaveTrait: mask)
            }
        }
    }

    private func decoration(_ keyPath: ReferenceWritableKeyPath<FreeTextAnnotation, Bool>, name: String) -> Binding<Bool> {
        Binding {
            textBox[keyPath: keyPath]
        } set: { on in
            change(name) { $0[keyPath: keyPath] = on }
        }
    }

    private var alignment: Binding<NSTextAlignment> {
        Binding {
            textBox.alignment
        } set: { newValue in
            change("Alignement") { $0.alignment = newValue }
        }
    }

    private var hasBackground: Binding<Bool> {
        Binding {
            textBox.color.alphaComponent > 0
        } set: { on in
            change("Fond") { $0.color = on ? NSColor.systemYellow.withAlphaComponent(0.4) : .clear }
        }
    }

    private var background: Binding<Color> {
        Binding {
            Color(nsColor: textBox.color)
        } set: { newValue in
            change("Couleur du fond") { $0.color = NSColor(newValue) }
        }
    }
}

/// Width and height in points; the top-left corner stays in place.
private struct SizeSection: View {
    let viewer: ViewerModel
    let annotation: PDFAnnotation

    var body: some View {
        let _ = viewer.revision
        Section("Taille") {
            dimension("Largeur", value: annotation.bounds.width) { width in
                let ratio = annotation.bounds.height / max(annotation.bounds.width, 1)
                return CGSize(width: width, height: keepsRatio ? width * ratio : annotation.bounds.height)
            }
            if annotation.isFreeText {
                LabeledContent("Hauteur", value: "\(Int(annotation.bounds.height.rounded())) pt (auto)")
            } else {
                dimension("Hauteur", value: annotation.bounds.height) { height in
                    let ratio = annotation.bounds.width / max(annotation.bounds.height, 1)
                    return CGSize(width: keepsRatio ? height * ratio : annotation.bounds.width, height: height)
                }
            }
            if keepsRatio {
                Text("Les proportions sont conservées.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var keepsRatio: Bool { AnnotationState.keepsAspectRatio(annotation) }

    private func dimension(_ label: String, value: CGFloat, size: @escaping (CGFloat) -> CGSize) -> some View {
        let binding = Binding<Double> {
            Double(value.rounded())
        } set: { newValue in
            resize(to: size(max(CGFloat(newValue), 8)))
        }
        return LabeledContent(label) {
            HStack(spacing: 4) {
                TextField(label, value: binding, format: .number.precision(.fractionLength(0)))
                    .labelsHidden()
                    .multilineTextAlignment(.trailing)
                    .frame(width: 60)
                Text("pt").foregroundStyle(.secondary)
                Stepper(label, value: binding, in: 8...5000, step: 5)
                    .labelsHidden()
            }
        }
    }

    private func resize(to size: CGSize) {
        let old = annotation.bounds
        guard abs(size.width - old.width) > 0.5 || abs(size.height - old.height) > 0.5 else { return }
        viewer.update(annotation, actionName: "Redimensionner") { a in
            AnnotationState(a).apply(resizedTo: CGRect(x: old.minX, y: old.maxY - size.height, width: size.width, height: size.height), to: a)
        }
    }
}
