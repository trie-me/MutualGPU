using System.Security.Cryptography;
using System.Text;

namespace MutualGPU.Infrastructure;

public sealed class HandleCipher
{
    private const byte FormatVersion = 1;
    private readonly byte[] key;

    public HandleCipher(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        key = ParseKey(options.HandleEncryptionKey);
    }

    public string Digest(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public byte[] Encrypt(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var envelope = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
        envelope[0] = FormatVersion;
        nonce.CopyTo(envelope, 1);
        tag.CopyTo(envelope, 1 + nonce.Length);
        ciphertext.CopyTo(envelope, 1 + nonce.Length + tag.Length);
        CryptographicOperations.ZeroMemory(plaintext);
        return envelope;
    }

    public string Decrypt(byte[] envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length < 30 || envelope[0] != FormatVersion)
        {
            throw new CryptographicException("The task-handle envelope is invalid.");
        }

        var nonce = envelope.AsSpan(1, 12);
        var tag = envelope.AsSpan(13, 16);
        var ciphertext = envelope.AsSpan(29);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static byte[] ParseKey(string? encoded)
    {
        if (String.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException("MutualGPU:Postgres:HandleEncryptionKey must be a base64-encoded 256-bit key.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("MutualGPU:Postgres:HandleEncryptionKey must be valid base64.", exception);
        }

        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("MutualGPU:Postgres:HandleEncryptionKey must decode to exactly 32 bytes.");
        }

        return key;
    }
}
