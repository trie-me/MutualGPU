import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2

/// Native TLS HTTP/2 gRPC implementation backed by the generated provider.proto
/// client. This is the production Swift control-plane adapter; ProviderTransport
/// remains public so application tests may use a deterministic fake.
@available(macOS 15.0, iOS 18.0, *)
public actor NativeGrpcProviderTransport: ProviderTransport {
    public struct Endpoint: Sendable {
        public let host: String
        public let port: Int

        public init(httpsURL: URL) throws {
            guard httpsURL.scheme?.lowercased() == "https", let host = httpsURL.host else {
                throw NativeGrpcProviderTransportError.httpsRequired
            }
            self.host = host
            self.port = httpsURL.port ?? 443
        }
    }

    private let endpoint: Endpoint
    private let presharedKey: String
    private var writer: RPCWriter<Mutualgpu_V1_ProviderMessage>?
    private var sessionTask: Task<Void, Never>?
    private var connectedContinuation: CheckedContinuation<Void, Error>?
    private var sessionEndContinuation: CheckedContinuation<Void, Never>?
    private var inputWaiters: [CheckedContinuation<URL, Error>] = []
    private var uploadWaiters: [CheckedContinuation<String, Error>] = []
    private var completionWaiters: [CheckedContinuation<Void, Error>] = []

    public init(endpoint: URL, presharedKey: String) throws {
        self.endpoint = try Endpoint(httpsURL: endpoint)
        self.presharedKey = presharedKey
    }

    public func enroll(definitionJSON: Data) async throws {
        let transport = try HTTP2ClientTransport.Posix(
            target: .dns(host: endpoint.host, port: endpoint.port),
            transportSecurity: .tls)
        var request = Mutualgpu_V1_EnrollRequest()
        request.definition = definitionJSON
        _ = try await withGRPCClient(transport: transport) { client in
            let provider = Mutualgpu_V1_ProviderControl.Client(wrapping: client)
            return try await provider.enroll(request, metadata: self.authorizationMetadata())
        }
    }

    public func connect(onAssignment: @escaping @Sendable (Assignment) async -> Void) async throws {
        guard sessionTask == nil else { throw NativeGrpcProviderTransportError.alreadyConnected }
        try await withCheckedThrowingContinuation { continuation in
            connectedContinuation = continuation
            sessionTask = Task { [endpoint, presharedKey] in
                await Self.runSession(endpoint: endpoint, presharedKey: presharedKey, owner: self, onAssignment: onAssignment)
            }
        }
    }

    public func accept(_ assignment: Assignment) async throws {
        var message = Mutualgpu_V1_TaskAccepted()
        self.populate(&message, from: assignment)
        try await send(.accepted(message))
    }

    public func reject(_ assignment: Assignment, reason: String?) async throws {
        var message = Mutualgpu_V1_TaskRejected()
        self.populate(&message, from: assignment)
        message.reason = reason ?? ""
        try await send(.rejected(message))
    }

    public func reportProgress(_ assignment: Assignment, sequenceNumber: UInt64, payload: Data) async throws {
        var message = Mutualgpu_V1_ProgressUpdate()
        self.populate(&message, from: assignment)
        message.sequenceNumber = sequenceNumber
        if let json = try? JSONSerialization.jsonObject(with: payload) as? [String: Any] {
            message.phase = json["phase"] as? String ?? ""
            message.percent = json["percent"] as? Double ?? 0
            message.message = json["message"] as? String ?? ""
        } else {
            message.message = String(data: payload, encoding: .utf8) ?? ""
        }
        try await send(.progress(message))
    }

    public func refreshInputDownload(_ assignment: Assignment) async throws -> URL {
        var message = Mutualgpu_V1_InputDownloadRequest()
        self.populate(&message, from: assignment)
        return try await withCheckedThrowingContinuation { continuation in
            inputWaiters.append(continuation)
            Task {
                do {
                    try await self.send(.inputDownload(message))
                } catch {
                    await self.failNextInputWaiter(error)
                }
            }
        }
    }

    public func requestResultUpload(_ assignment: Assignment) async throws -> String {
        var message = Mutualgpu_V1_ResultUploadRequest()
        self.populate(&message, from: assignment)
        return try await withCheckedThrowingContinuation { continuation in
            uploadWaiters.append(continuation)
            Task {
                do {
                    try await self.send(.resultUpload(message))
                } catch {
                    await self.failNextUploadWaiter(error)
                }
            }
        }
    }

    public func complete(_ assignment: Assignment, receipt: String) async throws {
        var message = Mutualgpu_V1_TaskCompleted()
        self.populate(&message, from: assignment)
        message.receipt = receipt
        try await withCheckedThrowingContinuation { continuation in
            completionWaiters.append(continuation)
            Task {
                do {
                    try await self.send(.completed(message))
                } catch {
                    await self.failNextCompletionWaiter(error)
                }
            }
        }
    }

    public func fail(_ assignment: Assignment, step: String, reason: String?) async throws {
        var message = Mutualgpu_V1_TaskFailed()
        self.populate(&message, from: assignment)
        message.step = step
        message.reason = reason ?? ""
        try await send(.failed(message))
    }

    public func close() {
        endSession()
    }

    private static func runSession(
        endpoint: Endpoint,
        presharedKey: String,
        owner: NativeGrpcProviderTransport,
        onAssignment: @escaping @Sendable (Assignment) async -> Void) async
    {
        do {
            let transport = try HTTP2ClientTransport.Posix(
                target: .dns(host: endpoint.host, port: endpoint.port),
                transportSecurity: .tls)
            try await withGRPCClient(transport: transport) { client in
                let provider = Mutualgpu_V1_ProviderControl.Client(wrapping: client)
                var metadata = Metadata()
                metadata.addString("Bearer \(presharedKey)", forKey: "authorization")
                try await provider.connect(
                    metadata: metadata,
                    requestProducer: { writer in
                        await owner.bind(writer)
                        var connect = Mutualgpu_V1_ConnectRequest()
                        connect.protocolVersion = 1
                        var envelope = Mutualgpu_V1_ProviderMessage()
                        envelope.body = .connect(connect)
                        try await writer.write(envelope)
                        await owner.waitForSessionEnd()
                    },
                    onResponse: { response in
                        for try await message in response.messages {
                            try await owner.receive(message, onAssignment: onAssignment)
                        }
                        await owner.endSession()
                    })
            }
            await owner.finishSession(nil)
        } catch {
            await owner.finishSession(error)
        }
    }

    private func authorizationMetadata() -> Metadata {
        var metadata = Metadata()
        metadata.addString("Bearer \(presharedKey)", forKey: "authorization")
        return metadata
    }

    private func bind(_ writer: RPCWriter<Mutualgpu_V1_ProviderMessage>) {
        self.writer = writer
    }

    private func waitForSessionEnd() async {
        await withCheckedContinuation { continuation in
            sessionEndContinuation = continuation
        }
    }

    private func endSession() {
        sessionEndContinuation?.resume()
        sessionEndContinuation = nil
    }

    private func finishSession(_ error: (any Error)?) {
        writer = nil
        sessionTask = nil
        if let error { connectedContinuation?.resume(throwing: error) } else { connectedContinuation?.resume() }
        connectedContinuation = nil
        for continuation in inputWaiters { continuation.resume(throwing: error ?? NativeGrpcProviderTransportError.sessionClosed) }
        for continuation in uploadWaiters { continuation.resume(throwing: error ?? NativeGrpcProviderTransportError.sessionClosed) }
        for continuation in completionWaiters { continuation.resume(throwing: error ?? NativeGrpcProviderTransportError.sessionClosed) }
        inputWaiters.removeAll(); uploadWaiters.removeAll(); completionWaiters.removeAll()
    }

    private func receive(_ envelope: Mutualgpu_V1_ServerMessage, onAssignment: @escaping @Sendable (Assignment) async -> Void) async throws {
        switch envelope.body {
        case .connected:
            connectedContinuation?.resume()
            connectedContinuation = nil
        case .assignment(let value):
            guard let taskID = UUID(uuidString: value.taskID), let attemptID = UUID(uuidString: value.attemptID) else {
                throw NativeGrpcProviderTransportError.invalidAssignmentIdentity
            }
            let input = value.hasInput && URL(string: value.input.url) != nil
                ? InputArtifact(url: URL(string: value.input.url)!, contentType: value.input.contentType, length: value.input.length, sha256: value.input.sha256)
                : nil
            await onAssignment(Assignment(taskID: taskID, attemptID: attemptID, taskHandle: value.taskHandle, scalars: value.scalars, input: input))
        case .inputDownload(let value):
            guard let url = URL(string: value.url) else {
                inputWaiters.removeFirstOrNil()?.resume(throwing: NativeGrpcProviderTransportError.invalidInputURL)
                return
            }
            inputWaiters.removeFirstOrNil()?.resume(returning: url)
        case .resultUpload(let value):
            uploadWaiters.removeFirstOrNil()?.resume(returning: value.uploadToken)
        case .completion:
            completionWaiters.removeFirstOrNil()?.resume()
        case .error(let value):
            throw NativeGrpcProviderTransportError.protocolError(value.message)
        case nil:
            throw NativeGrpcProviderTransportError.protocolError("The provider server sent an empty envelope.")
        }
    }

    private func send(_ body: Mutualgpu_V1_ProviderMessage.OneOf_Body) async throws {
        guard let writer else { throw NativeGrpcProviderTransportError.notConnected }
        var envelope = Mutualgpu_V1_ProviderMessage()
        envelope.body = body
        try await writer.write(envelope)
    }

    private func failNextInputWaiter(_ error: any Error) {
        inputWaiters.removeFirstOrNil()?.resume(throwing: error)
    }

    private func failNextUploadWaiter(_ error: any Error) {
        uploadWaiters.removeFirstOrNil()?.resume(throwing: error)
    }

    private func failNextCompletionWaiter(_ error: any Error) {
        completionWaiters.removeFirstOrNil()?.resume(throwing: error)
    }

    private func populate(_ message: inout Mutualgpu_V1_TaskAccepted, from assignment: Assignment) { message.taskID = assignment.taskID.uuidString; message.attemptID = assignment.attemptID.uuidString; message.taskHandle = assignment.taskHandle }
    private func populate(_ message: inout Mutualgpu_V1_TaskRejected, from assignment: Assignment) { message.taskID = assignment.taskID.uuidString; message.attemptID = assignment.attemptID.uuidString; message.taskHandle = assignment.taskHandle }
    private func populate(_ message: inout Mutualgpu_V1_ProgressUpdate, from assignment: Assignment) { message.taskID = assignment.taskID.uuidString; message.attemptID = assignment.attemptID.uuidString; message.taskHandle = assignment.taskHandle }
    private func populate(_ message: inout Mutualgpu_V1_InputDownloadRequest, from assignment: Assignment) { message.taskID = assignment.taskID.uuidString; message.attemptID = assignment.attemptID.uuidString; message.taskHandle = assignment.taskHandle }
    private func populate(_ message: inout Mutualgpu_V1_ResultUploadRequest, from assignment: Assignment) { message.taskID = assignment.taskID.uuidString; message.attemptID = assignment.attemptID.uuidString; message.taskHandle = assignment.taskHandle }
    private func populate(_ message: inout Mutualgpu_V1_TaskCompleted, from assignment: Assignment) { message.taskID = assignment.taskID.uuidString; message.attemptID = assignment.attemptID.uuidString; message.taskHandle = assignment.taskHandle }
    private func populate(_ message: inout Mutualgpu_V1_TaskFailed, from assignment: Assignment) { message.taskID = assignment.taskID.uuidString; message.attemptID = assignment.attemptID.uuidString; message.taskHandle = assignment.taskHandle }
}

@available(macOS 15.0, iOS 18.0, *)
public enum NativeGrpcProviderTransportError: Error, Sendable {
    case httpsRequired
    case alreadyConnected
    case notConnected
    case sessionClosed
    case invalidAssignmentIdentity
    case invalidInputURL
    case protocolError(String)
}

private extension Array {
    mutating func removeFirstOrNil() -> Element? {
        isEmpty ? nil : removeFirst()
    }
}
