// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "S3bopus",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "S3bopus", path: "Sources/S3bopus")
    ]
)
