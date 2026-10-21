import AppKit
import FolioText
import PDFKit

/// `Folio --selftest-edit <in.pdf> <find> <replacement> <out.pdf>`: drives the real in-place text editor
/// (offscreen window, synthetic click, typing, commit, undo) and exits. Used to test without the UI.
enum SelfTest {
    @MainActor
    static func runIfRequested() {
        let arguments = CommandLine.arguments
        guard let flag = arguments.firstIndex(of: "--selftest-edit"), arguments.count >= flag + 5 else { return }
        let (input, find, replacement, output) = (arguments[flag + 1], arguments[flag + 2], arguments[flag + 3], arguments[flag + 4])
        func fail(_ message: String) -> Never {
            print("SELFTEST FAIL:", message)
            exit(1)
        }

        guard let document = PDFDocument(url: URL(fileURLWithPath: input)) else { fail("cannot open \(input)") }
        document.delegate = AnnotationClassProvider.shared
        let viewer = ViewerModel()
        let undo = UndoManager()
        viewer.undoManager = undo
        let window = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 900, height: 1200), styleMask: [.titled], backing: .buffered, defer: false)
        viewer.pdfView.frame = window.contentView!.bounds
        window.contentView!.addSubview(viewer.pdfView)
        viewer.attach(document)
        viewer.pdfView.layoutDocumentView()
        viewer.tool = .editText

        guard let page = document.page(at: 0), let layout = viewer.textLayout(for: page),
              let block = layout.blocks.first(where: { TextEditor.normalized($0.text).contains(find) }) else { fail("paragraph containing \"\(find)\" not found") }

        // Click in the middle of the paragraph.
        let viewPoint = viewer.pdfView.convert(CGPoint(x: block.bounds.midX, y: block.bounds.midY), from: page)
        let windowPoint = viewer.pdfView.convert(viewPoint, to: nil)
        guard let click = NSEvent.mouseEvent(with: .leftMouseDown, location: windowPoint, modifierFlags: [], timestamp: 0,
                                             windowNumber: window.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1) else { fail("event") }
        viewer.pdfView.mouseDown(with: click)
        guard let session = viewer.pdfView.textSession else { fail("no editor opened") }
        print("editor:", session.label.stringValue.trimmingCharacters(in: .whitespaces), "| frame", session.textView.frame.integral)
        if let path = ProcessInfo.processInfo.environment["FOLIO_SNAPSHOT"] {
            // Page with the editor on top, to check that the editor text sits on the original text.
            let content = window.contentView!
            content.layoutSubtreeIfNeeded()
            if let rep = content.bitmapImageRepForCachingDisplay(in: content.bounds) {
                content.cacheDisplay(in: content.bounds, to: rep)
                try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
            }
        }

        // Type the replacement over the found text, as a user would.
        let range = (session.textView.string as NSString).range(of: find)
        guard range.location != NSNotFound else { fail("\"\(find)\" not in editor text") }
        session.textView.setSelectedRange(range)
        session.textView.insertText(replacement, replacementRange: range)

        viewer.pdfView.endTextEditing(commit: true)
        // Ligatures and spaces are folded: PDFKit may return "ﬃ" for "ffi".
        let edited = TextEditor.compact(document.page(at: 0)?.string ?? "")
        guard edited.contains(TextEditor.compact(replacement)) else { fail("page text after edit: \(edited.prefix(300))") }
        print("edit: OK")
        guard let data = document.dataRepresentation() else { fail("save") }
        try? data.write(to: URL(fileURLWithPath: output))

        undo.undo()
        let undone = TextEditor.compact(document.page(at: 0)?.string ?? "")
        guard undone.contains(TextEditor.compact(find)), !undone.contains(TextEditor.compact(replacement)) || find.contains(replacement) else {
            fail("undo did not restore the text")
        }
        print("undo: OK")
        exit(0)
    }
}
