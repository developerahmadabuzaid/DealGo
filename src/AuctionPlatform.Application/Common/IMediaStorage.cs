namespace AuctionPlatform.Application.Common;

/// <summary>
/// Where an uploaded picture is kept, and the address it is served at afterwards.
///
/// The platform used to write straight into the web root, which works until the process is
/// replaced: a container has its own disk, so every deployment threw away every picture an
/// administrator had ever uploaded. Uploading is therefore expressed as an interface — a local
/// folder while developing, a storage account in production — and nothing above this line knows
/// which one is in use.
/// </summary>
public interface IMediaStorage
{
    /// <summary>
    /// Stores the content under a name of the platform's own choosing and returns the address the
    /// picture will be read back from. That address may be relative to the API ("/uploads/x.jpg")
    /// or absolute ("https://…/uploads/x.jpg"); callers pass it on unchanged.
    /// </summary>
    /// <param name="content">The bytes, positioned at the start.</param>
    /// <param name="storedName">The file name to store, already sanitised by the caller.</param>
    /// <param name="contentType">What the bytes actually are, decided by inspecting them.</param>
    Task<string> SaveAsync(
        Stream content, string storedName, string contentType, CancellationToken ct = default);
}
