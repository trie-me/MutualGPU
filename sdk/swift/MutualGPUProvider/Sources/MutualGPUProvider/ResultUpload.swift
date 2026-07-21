import CryptoKit
import Foundation

public struct ResultFile: Sendable {
    public let data: Data
    public let contentType: String
    public let fileName: String

    public init(data: Data, contentType: String, fileName: String) {
        self.data = data
        self.contentType = contentType
        self.fileName = fileName
    }
}

public struct ProviderResult: Sendable {
    public let resultZip: Data
    public let metadata: Data?
    public let thumbnail: ResultFile?
    public let preview: ResultFile?
    public let logs: Data?

    public init(resultZip: Data, metadata: Data? = nil, thumbnail: ResultFile? = nil, preview: ResultFile? = nil, logs: Data? = nil) {
        self.resultZip = resultZip
        self.metadata = metadata
        self.thumbnail = thumbnail
        self.preview = preview
        self.logs = logs
    }
}

public struct PublishedResult: Sendable {
    public let receipt: String
    public let sha256: String
}

public enum ResultUploadError: Error, Sendable {
    case invalidAPIBaseURL
    case invalidResponse
    case rejected(status: Int, body: String)
}

/// Shared Swift data-plane client. The control-plane transport supplies the single-use
/// token; this type owns checksum calculation and authenticated multipart construction.
public struct ProviderResultUploader: Sendable {
    private let apiBaseURL: URL
    private let presharedKey: String
    private let session: URLSession

    public init(apiBaseURL: URL, presharedKey: String, session: URLSession = .shared) {
        self.apiBaseURL = apiBaseURL
        self.presharedKey = presharedKey
        self.session = session
    }

    public func upload(assignment: Assignment, token: String, result: ProviderResult) async throws -> PublishedResult {
        guard let endpoint = URL(string: "provider/tasks/\(assignment.taskID.uuidString)/attempts/\(assignment.attemptID.uuidString)/result", relativeTo: apiBaseURL) else {
            throw ResultUploadError.invalidAPIBaseURL
        }
        let boundary = "MutualGPU-\(UUID().uuidString)"
        let sha256 = SHA256.hash(data: result.resultZip).map { String(format: "%02x", $0) }.joined()
        var request = URLRequest(url: endpoint)
        request.httpMethod = "POST"
        request.setValue("Bearer \(presharedKey)", forHTTPHeaderField: "Authorization")
        request.setValue(assignment.taskHandle, forHTTPHeaderField: "X-MutualGPU-Task-Handle")
        request.setValue(token, forHTTPHeaderField: "X-MutualGPU-Upload-Token")
        request.setValue(sha256, forHTTPHeaderField: "X-MutualGPU-Sha256")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        let body = Self.makeMultipartBody(boundary: boundary, result: result)

        let (data, response) = try await session.upload(for: request, from: body)
        guard let http = response as? HTTPURLResponse else { throw ResultUploadError.invalidResponse }
        let responseBody = String(data: data, encoding: .utf8) ?? ""
        guard (200..<300).contains(http.statusCode) else { throw ResultUploadError.rejected(status: http.statusCode, body: responseBody) }
        guard
            let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
            let receipt = object["receipt"] as? String,
            !receipt.isEmpty
        else { throw ResultUploadError.invalidResponse }
        return PublishedResult(receipt: receipt, sha256: sha256)
    }

    static func makeMultipartBody(boundary: String, result: ProviderResult) -> Data {
        var body = Data()
        appendFile(to: &body, boundary: boundary, name: "result", file: ResultFile(data: result.resultZip, contentType: "application/zip", fileName: "result.zip"))
        if let metadata = result.metadata {
            appendFile(to: &body, boundary: boundary, name: "metadata", file: ResultFile(data: metadata, contentType: "application/json", fileName: "metadata.json"))
        }
        if let thumbnail = result.thumbnail { appendFile(to: &body, boundary: boundary, name: "thumbnail", file: thumbnail) }
        if let preview = result.preview { appendFile(to: &body, boundary: boundary, name: "preview", file: preview) }
        if let logs = result.logs {
            appendFile(to: &body, boundary: boundary, name: "logs", file: ResultFile(data: logs, contentType: "text/plain", fileName: "logs.txt"))
        }
        body.append("--\(boundary)--\r\n".data(using: .utf8)!)
        return body
    }

    private static func appendFile(to body: inout Data, boundary: String, name: String, file: ResultFile) {
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"\(name)\"; filename=\"\(file.fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: \(file.contentType)\r\n\r\n".data(using: .utf8)!)
        body.append(file.data)
        body.append("\r\n".data(using: .utf8)!)
    }
}
