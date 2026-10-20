// swift-tools-version:5.10
import PackageDescription

/// PDFium comes from scripts/fetch-pdfium.sh (Vendor/pdfium). The app bundle ships the dylib in
/// Contents/Frameworks; command-line builds find it in Vendor/pdfium/lib.
let pdfiumLib = Context.packageDirectory + "/Vendor/pdfium/lib"
let pdfiumLinker: [LinkerSetting] = [
    .unsafeFlags([
        "-L", pdfiumLib,
        "-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks",
        "-Xlinker", "-rpath", "-Xlinker", pdfiumLib,
    ]),
]

let package = Package(
    name: "Folio",
    platforms: [.macOS(.v14)],
    targets: [
        .systemLibrary(name: "CPDFium", path: "Sources/CPDFium"),
        .target(
            name: "FolioText",
            dependencies: ["CPDFium"],
            path: "Sources/FolioText"
        ),
        .executableTarget(
            name: "Folio",
            dependencies: ["FolioText"],
            path: "Sources/Folio",
            linkerSettings: pdfiumLinker
        ),
        /// Command-line lab to inspect and edit PDF text without the UI.
        .executableTarget(
            name: "textlab",
            dependencies: ["FolioText"],
            path: "Sources/textlab",
            linkerSettings: pdfiumLinker
        ),
    ]
)
