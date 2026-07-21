import Foundation

public struct Assignment: Sendable {
    public let taskID: UUID
    public let attemptID: UUID
    public let taskHandle: String
    public let scalars: [String: String]
    public let input: InputArtifact?

    public init(taskID: UUID, attemptID: UUID, taskHandle: String, scalars: [String: String], input: InputArtifact? = nil) {
        self.taskID = taskID
        self.attemptID = attemptID
        self.taskHandle = taskHandle
        self.scalars = scalars
        self.input = input
    }
}

public struct InputArtifact: Sendable {
    public let url: URL
    public let contentType: String
    public let length: UInt64
    public let sha256: String

    public init(url: URL, contentType: String, length: UInt64, sha256: String) {
        self.url = url
        self.contentType = contentType
        self.length = length
        self.sha256 = sha256
    }
}

public protocol ProviderTransport: Sendable {
    func enroll(definitionJSON: Data) async throws
    func connect(onAssignment: @escaping @Sendable (Assignment) async -> Void) async throws
    func accept(_ assignment: Assignment) async throws
    func reject(_ assignment: Assignment, reason: String?) async throws
    func reportProgress(_ assignment: Assignment, sequenceNumber: UInt64, payload: Data) async throws
    func refreshInputDownload(_ assignment: Assignment) async throws -> URL
    func requestResultUpload(_ assignment: Assignment) async throws -> String
    func complete(_ assignment: Assignment, receipt: String) async throws
    func fail(_ assignment: Assignment, step: String, reason: String?) async throws
}

public enum ProviderClientError: Error, Sendable {
    case noActiveAssignment
    case resultUploaderNotConfigured
}

/// Public async lifecycle. Concrete gRPC/protobuf generation plugs into ProviderTransport.
public actor ProviderClient {
    private let transport: any ProviderTransport
    private let resultUploader: ProviderResultUploader?
    private var activeHandle: String?
    private var progressSequence: UInt64 = 0
    private var lastProgress: ContinuousClock.Instant?

    public init(transport: any ProviderTransport, resultUploader: ProviderResultUploader? = nil) {
        self.transport = transport
        self.resultUploader = resultUploader
    }
    public func enroll(definitionJSON: Data) async throws { try await transport.enroll(definitionJSON: definitionJSON) }
    public func connect(handler: @escaping @Sendable (Assignment, ProviderClient) async throws -> Void) async throws {
        try await transport.connect { [weak self] assignment in
            guard let self else { return }
            await self.receive(assignment, handler: handler)
        }
    }
    private func receive(_ assignment: Assignment, handler: @escaping @Sendable (Assignment, ProviderClient) async throws -> Void) async {
        guard activeHandle == nil else { try? await transport.reject(assignment, reason: "provider already owns an active task"); return }
        activeHandle = assignment.taskHandle; progressSequence = 0; lastProgress = nil
        do { try await transport.accept(assignment); try await handler(assignment, self) }
        catch { try? await transport.fail(assignment, step: "execution", reason: String(describing: error)) }
        activeHandle = nil
    }
    public func reportProgress(for assignment: Assignment, payload: Data) async throws -> Bool {
        guard activeHandle == assignment.taskHandle else { return false }
        let now = ContinuousClock.now
        if let lastProgress, lastProgress.duration(to: now) < .seconds(1) { return false }
        lastProgress = now; progressSequence += 1
        try await transport.reportProgress(assignment, sequenceNumber: progressSequence, payload: payload)
        return true
    }
    public func refreshInputDownload(for assignment: Assignment) async throws -> URL { try await transport.refreshInputDownload(assignment) }
    public func requestResultUpload(for assignment: Assignment) async throws -> String { try await transport.requestResultUpload(assignment) }
    public func uploadResult(for assignment: Assignment, result: ProviderResult) async throws -> PublishedResult {
        guard activeHandle == assignment.taskHandle else { throw ProviderClientError.noActiveAssignment }
        guard let resultUploader else { throw ProviderClientError.resultUploaderNotConfigured }
        let token = try await transport.requestResultUpload(assignment)
        return try await resultUploader.upload(assignment: assignment, token: token, result: result)
    }
    public func complete(_ assignment: Assignment, receipt: String) async throws { try await transport.complete(assignment, receipt: receipt) }
}
