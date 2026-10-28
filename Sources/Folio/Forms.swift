import PDFKit

/// Values of every form field in a document. PDFKit edits fields itself without telling the
/// undo manager, so Folio compares snapshots to make fills undoable and mark the file edited.
struct FormState: Equatable {
    private struct Field: Equatable {
        let annotation: ObjectIdentifier
        let text: String?
        let state: Int
    }

    private let fields: [Field]
    private let annotations: [ObjectIdentifier: PDFAnnotation]

    init(_ document: PDFDocument) {
        let widgets = (0..<document.pageCount)
            .compactMap { document.page(at: $0) }
            .flatMap { $0.annotations }
            .filter { $0.subtype == "Widget" }
        fields = widgets.map {
            Field(annotation: ObjectIdentifier($0), text: $0.widgetStringValue, state: $0.buttonWidgetState.rawValue)
        }
        annotations = Dictionary(uniqueKeysWithValues: widgets.map { (ObjectIdentifier($0), $0) })
    }

    var isEmpty: Bool { fields.isEmpty }

    func apply() {
        for field in fields {
            guard let annotation = annotations[field.annotation] else { continue }
            if annotation.widgetStringValue != field.text { annotation.widgetStringValue = field.text }
            let state = PDFWidgetCellState(rawValue: field.state) ?? .offState
            if annotation.buttonWidgetState != state { annotation.buttonWidgetState = state }
        }
    }

    static func == (lhs: FormState, rhs: FormState) -> Bool { lhs.fields == rhs.fields }
}

extension ViewerModel {
    /// Records a form edit already made by PDFKit, restoring `old` on undo.
    func registerFormChange(from old: FormState, to new: FormState) {
        revision += 1
        undoManager?.registerUndo(withTarget: self) { model in
            MainActor.assumeIsolated {
                old.apply()
                model.pdfView.formSnapshot = old
                model.registerFormChange(from: new, to: old)
            }
        }
        undoManager?.setActionName(tr("Remplir le formulaire"))
    }
}
