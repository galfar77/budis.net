// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "BudisCommander",
    platforms: [.macOS(.v14)],
    dependencies: [
        // vestavěný terminál
        .package(url: "https://github.com/migueldeicaza/SwiftTerm.git", exact: "1.2.5"),
    ],
    targets: [
        .executableTarget(
            name: "BudisCommander",
            dependencies: [.product(name: "SwiftTerm", package: "SwiftTerm")],
            path: "Sources/BudisCommander"
        ),
        .testTarget(
            name: "BudisCommanderTests",
            dependencies: ["BudisCommander"],
            path: "Tests/BudisCommanderTests"
        )
    ]
)
