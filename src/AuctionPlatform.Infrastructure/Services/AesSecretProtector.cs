using System.Security.Cryptography;
using System.Text;
using AuctionPlatform.Application.Common;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// AES-256-GCM with a key derived (HKDF-SHA256) from M5azn:EncryptionKey, or from Jwt:Secret when
/// that is not set. Both come from the server's configuration only, so a database copy alone
/// cannot reveal what was protected. Stored as "v1:" + base64(nonce | ciphertext | tag).
///
/// ASP.NET Data Protection is not used because its keys would need a persistent, shared key ring,
/// which the hosting does not give; a derived key survives restarts and works on every instance.
/// </summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("DealGo.SecretProtector.v1");

    private readonly byte[]? _key;

    public AesSecretProtector(IOptions<M5aznOptions> m5azn, IOptions<JwtOptions> jwt)
    {
        var material = !string.IsNullOrWhiteSpace(m5azn.Value.EncryptionKey) ? m5azn.Value.EncryptionKey : jwt.Value.Secret;
        if (!string.IsNullOrWhiteSpace(material))
        {
            _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(material), 32, info: Info);
        }
    }

    public string Protect(string plaintext)
    {
        var key = _key ?? throw new InvalidOperationException(
            "No key to protect secrets with: set Jwt:Secret (or M5azn:EncryptionKey) on the server.");

        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + data.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize, data.Length), output.AsSpan(NonceSize + data.Length, TagSize));
        return Prefix + Convert.ToBase64String(output);
    }

    public string? Unprotect(string protectedValue)
    {
        if (_key is null || string.IsNullOrEmpty(protectedValue) || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var input = Convert.FromBase64String(protectedValue[Prefix.Length..]);
            if (input.Length < NonceSize + TagSize) return null;

            var length = input.Length - NonceSize - TagSize;
            var plain = new byte[length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, length),
                input.AsSpan(NonceSize + length, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
