# Backblaze contest decision brief

- Backblaze B2 will become MutualGPU's future primary writer through a later, explicitly reviewed opt-in; production writes remain on `aws-primary` until activation.
- Each artifact is read only from its recorded AWS or B2 target. Missing or unhealthy targets fail explicitly; there is no implicit cross-provider fallback.
- File/object-storage providers hold only input and result bytes. All SDK uploads enter through the MutualGPU API as multipart payloads for authentication, validation, hashing, and provider-pinned writing; SDKs receive no object-store upload credentials or upload URLs.
- After authorizing the requestor or assigned provider, MutualGPU vends a short-lived provider-native presigned URL for direct download of result and input resources from the recorded target.

Existing permissive API CORS and disabled storage-CORS synchronization remain unchanged unless separately reviewed.
