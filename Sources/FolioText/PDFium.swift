import CoreGraphics
import CPDFium
import Foundation

/// Thin Swift wrapper over PDFium. PDFium is not thread-safe: use it from one thread (the main thread).
public enum PDFium {
    private static var initialized = false

    static func ensureInitialized() {
        guard !initialized else { return }
        FPDF_InitLibrary()
        initialized = true
    }
}

public final class PDFiumDocument {
    let handle: FPDF_DOCUMENT
    /// PDFium reads the file lazily from this buffer, so it must outlive the document.
    private let buffer: UnsafeMutableRawPointer

    public init?(data: Data, password: String? = nil) {
        PDFium.ensureInitialized()
        buffer = .allocate(byteCount: max(data.count, 1), alignment: 1)
        data.copyBytes(to: buffer.assumingMemoryBound(to: UInt8.self), count: data.count)
        guard let document = FPDF_LoadMemDocument64(buffer, data.count, password) else {
            buffer.deallocate()
            return nil
        }
        handle = document
    }

    deinit {
        FPDF_CloseDocument(handle)
        buffer.deallocate()
    }

    public var pageCount: Int { Int(FPDF_GetPageCount(handle)) }

    public func page(at index: Int) -> PDFiumPage? {
        guard let page = FPDF_LoadPage(handle, Int32(index)) else { return nil }
        return PDFiumPage(document: self, handle: page)
    }

    /// Serializes the document, including edits whose page content was regenerated.
    public func data() -> Data? {
        SaveSink.save(handle, flags: FPDF_NO_INCREMENTAL)
    }

    /// Serializes the document without its encryption (the password has been given to open it).
    public func decryptedData() -> Data? {
        SaveSink.save(handle, flags: FPDF_NO_INCREMENTAL | FPDF_REMOVE_SECURITY)
    }
}

public final class PDFiumPage {
    let document: PDFiumDocument
    let handle: FPDF_PAGE
    private(set) lazy var textPage: FPDF_TEXTPAGE? = FPDFText_LoadPage(handle)
    var fontInfoCache: [OpaquePointer: FontInfo] = [:]

    init(document: PDFiumDocument, handle: FPDF_PAGE) {
        self.document = document
        self.handle = handle
    }

    deinit {
        if let textPage { FPDFText_ClosePage(textPage) }
        FPDF_ClosePage(handle)
    }

    var objectCount: Int { Int(FPDFPage_CountObjects(handle)) }

    /// Rewrites the page content stream after objects were changed, added or removed.
    @discardableResult
    public func generateContent() -> Bool {
        FPDFPage_GenerateContent(handle) != 0
    }
}

/// Collects FPDF_SaveAsCopy output. The C callback only gets the FPDF_FILEWRITE pointer back,
/// so buffers are looked up by that pointer.
private enum SaveSink {
    private static var buffers: [UnsafeMutableRawPointer: NSMutableData] = [:]

    static func save(_ document: FPDF_DOCUMENT, flags: Int32) -> Data? {
        let writer = UnsafeMutablePointer<FPDF_FILEWRITE>.allocate(capacity: 1)
        defer { writer.deallocate() }
        writer.pointee.version = 1
        writer.pointee.WriteBlock = { writer, bytes, size in
            guard let writer, let bytes, let buffer = SaveSink.buffers[UnsafeMutableRawPointer(writer)] else { return 0 }
            buffer.append(bytes, length: Int(size))
            return 1
        }
        let output = NSMutableData()
        buffers[UnsafeMutableRawPointer(writer)] = output
        defer { buffers[UnsafeMutableRawPointer(writer)] = nil }
        guard FPDF_SaveAsCopy(document, writer, FPDF_DWORD(flags)) != 0 else { return nil }
        return output as Data
    }
}

// MARK: - Strings

/// Reads a UTF-16LE string through PDFium's "call twice" buffer convention.
func utf16String(_ fill: (UnsafeMutablePointer<UInt16>?, UInt) -> UInt) -> String {
    let byteCount = Int(fill(nil, 0))
    guard byteCount > 2 else { return "" }
    var buffer = [UInt16](repeating: 0, count: byteCount / 2)
    _ = buffer.withUnsafeMutableBufferPointer { fill($0.baseAddress, UInt(byteCount)) }
    if buffer.last == 0 { buffer.removeLast() }
    return String(decoding: buffer, as: UTF16.self)
}

/// Reads a UTF-8 string through PDFium's "call twice" buffer convention.
func utf8String(_ fill: (UnsafeMutablePointer<CChar>?, Int) -> Int) -> String {
    let byteCount = fill(nil, 0)
    guard byteCount > 1 else { return "" }
    var buffer = [CChar](repeating: 0, count: byteCount)
    _ = buffer.withUnsafeMutableBufferPointer { fill($0.baseAddress, byteCount) }
    return String(cString: buffer)
}

/// PDFium expects NUL-terminated UTF-16 for FPDF_WIDESTRING.
func withWideString<R>(_ string: String, _ body: (UnsafePointer<UInt16>) -> R) -> R {
    let units = Array(string.utf16) + [0]
    return units.withUnsafeBufferPointer { body($0.baseAddress!) }
}

extension CGAffineTransform {
    init(_ m: FS_MATRIX) {
        self.init(a: CGFloat(m.a), b: CGFloat(m.b), c: CGFloat(m.c), d: CGFloat(m.d), tx: CGFloat(m.e), ty: CGFloat(m.f))
    }

    var fsMatrix: FS_MATRIX {
        FS_MATRIX(a: Float(a), b: Float(b), c: Float(c), d: Float(d), e: Float(tx), f: Float(ty))
    }
}

