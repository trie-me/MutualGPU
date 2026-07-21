// swift-tools-version: 6.1
import PackageDescription

let package = Package(
    name: "MutualGPUProvider",
    platforms: [.macOS(.v15), .iOS(.v18)],
    products: [.library(name: "MutualGPUProvider", targets: ["MutualGPUProvider"])],
    dependencies: [
        .package(url: "https://github.com/grpc/grpc-swift-2.git", from: "2.0.0"),
        .package(url: "https://github.com/grpc/grpc-swift-nio-transport.git", from: "2.0.0"),
        .package(url: "https://github.com/grpc/grpc-swift-protobuf.git", from: "2.0.0"),
        .package(url: "https://github.com/apple/swift-protobuf.git", from: "1.29.0"),
    ],
    targets: [
        .target(
            name: "MutualGPUProvider",
            dependencies: [
                .product(name: "GRPCCore", package: "grpc-swift-2"),
                .product(name: "GRPCNIOTransportHTTP2", package: "grpc-swift-nio-transport"),
                .product(name: "GRPCProtobuf", package: "grpc-swift-protobuf"),
            ],
            plugins: [.plugin(name: "GRPCProtobufGenerator", package: "grpc-swift-protobuf")]
        ),
        .testTarget(
            name: "MutualGPUProviderTests",
            dependencies: [
                "MutualGPUProvider",
                .product(name: "SwiftProtobuf", package: "swift-protobuf"),
            ]
        )
    ]
)
