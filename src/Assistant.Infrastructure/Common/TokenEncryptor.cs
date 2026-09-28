using System.Security.Cryptography;
using Assistant.Application.Common;

namespace Assistant.Infrastructure.Common;

// AES-256-GCM, key from TOKEN_ENCRYPTION_KEY (base64, 32 raw bytes). Layout of the stored
// ciphertext: [12-byte nonce][ciphertext][16-byte tag], concatenated — chosen so a single byte[]
// column (bytea) holds everything needed to decrypt, with no separate nonce/tag columns.
public class TokenEncryptor : ITokenEncryptor
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;

    public TokenEncryptor(string keyBase64)
    {
        var key = Convert.FromBase64String(keyBase64);
        if (key.Length != 32)
        {
            throw new ArgumentException("TOKEN_ENCRYPTION_KEY must decode to exactly 32 bytes (AES-256).", nameof(keyBase64));
        }

        _key = key;
    }

    public byte[] Encrypt(string plainToken)
    {
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plainToken);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plainBytes, ciphertext, tag);
        CryptographicOperations.ZeroMemory(plainBytes);

        var result = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSizeBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSizeBytes + ciphertext.Length, TagSizeBytes);
        return result;
    }

    public string Decrypt(byte[] cipherToken)
    {
        var nonce = cipherToken[..NonceSizeBytes];
        var tag = cipherToken[^TagSizeBytes..];
        var ciphertext = cipherToken[NonceSizeBytes..^TagSizeBytes];
        var plainBytes = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plainBytes);

        // Zero only after the string is materialized, so the return value isn't corrupted.
        var plainToken = System.Text.Encoding.UTF8.GetString(plainBytes);
        CryptographicOperations.ZeroMemory(plainBytes);
        return plainToken;
    }
}
