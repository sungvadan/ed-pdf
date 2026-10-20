// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "Folio",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Folio",
            path: "Sources/Folio"
        )
    ]
)
