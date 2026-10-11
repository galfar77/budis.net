// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "BudisCommander",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "BudisCommander",
            path: "Sources/BudisCommander"
        ),
        .testTarget(
            name: "BudisCommanderTests",
            dependencies: ["BudisCommander"],
            path: "Tests/BudisCommanderTests"
        )
    ]
)
