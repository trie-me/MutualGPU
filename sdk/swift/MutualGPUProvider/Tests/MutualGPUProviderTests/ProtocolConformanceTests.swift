import Foundation
import MutualGPUProvider
import SwiftProtobuf
import XCTest

final class ProtocolConformanceTests: XCTestCase {
    func testConnectEnvelopeUsesTheSharedCanonicalWireFormat() throws {
        var connect = Mutualgpu_V1_ConnectRequest()
        connect.protocolVersion = 1
        connect.presharedKey = "key"

        var envelope = Mutualgpu_V1_ProviderMessage()
        envelope.body = .connect(connect)

        XCTAssertEqual(
            try envelope.serializedData().base64EncodedString(),
            "CgcIARoDa2V5")
    }

    func testServerAssignmentFixtureRoundTripsWithInputDescriptor() throws {
        let encoded = "ElMKBHRhc2sSB2F0dGVtcHQaBmhhbmRsZSIKCgRzZWVkEgI0MiouChdodHRwczovL2lucHV0LmV4YW1wbGUvYRIJaW1hZ2UvcG5nGCoiBmRpZ2VzdA=="
        let data = try XCTUnwrap(Data(base64Encoded: encoded))
        let envelope = try Mutualgpu_V1_ServerMessage(serializedBytes: data)

        guard case .assignment(let assignment)? = envelope.body else {
            return XCTFail("Expected an assignment envelope.")
        }

        XCTAssertEqual(assignment.taskID, "task")
        XCTAssertEqual(assignment.scalars, ["seed": "42"])
        XCTAssertTrue(assignment.hasInput)
        XCTAssertEqual(assignment.input.url, "https://input.example/")
        XCTAssertEqual(assignment.input.contentType, "image/png")
        XCTAssertEqual(assignment.input.length, 42)
        XCTAssertEqual(assignment.input.sha256, "digest")
        XCTAssertEqual(try envelope.serializedData(), data)
    }

    func testNativeTransportRejectsCleartextEndpoint() {
        XCTAssertThrowsError(try NativeGrpcProviderTransport(endpoint: URL(string: "http://localhost:5000")!, presharedKey: "key")) { error in
            XCTAssertEqual(error as? NativeGrpcProviderTransportError, .httpsRequired)
        }
    }
}
