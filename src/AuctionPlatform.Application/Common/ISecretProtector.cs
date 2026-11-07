namespace AuctionPlatform.Application.Common;

/// <summary>
/// Encrypts a secret an administrator types into the dashboard (such as a supplier password)
/// before it is stored, and reads it back. The key lives only in the server's configuration.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Null when the value cannot be read, for example after the key changed.</summary>
    string? Unprotect(string protectedValue);
}
