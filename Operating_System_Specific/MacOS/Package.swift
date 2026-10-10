// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "BatCaveMacOS",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(
            name: "BatCaveMacOSAgent",
            targets: ["BatCaveMacOSAgent"]
        )
    ],
    targets: [
        .executableTarget(
            name: "BatCaveMacOSAgent",
            exclude: ["README.md"],
            linkerSettings: [
                .linkedFramework("ApplicationServices"),
                .linkedFramework("AppKit"),
                .linkedFramework("Network")
            ]
        )
    ]
)
