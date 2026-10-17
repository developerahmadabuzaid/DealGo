using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// Accepts an image from an administrator's machine and returns the address it will be served at.
///
/// Where the bytes end up is decided by <see cref="IMediaStorage"/> — a folder while developing,
/// a storage account in production. What is accepted is decided here, and that is a security
/// boundary rather than a convenience:
///
///   * The file type is decided by reading the first bytes of the content, never by trusting the
///     name or the Content-Type header the browser sent. A .jpg holding HTML would otherwise be
///     served back as a page on our own origin, which is stored cross-site scripting.
///   * The stored name is generated here. A caller-supplied name could carry path separators and
///     write outside the folder, or end in an extension the server treats as executable.
///   * Size is capped before anything is written to disk.
/// </summary>
[Route("api/v1/admin/uploads")]
[Authorize]
public sealed class AdminUploadsController : ApiControllerBase
{
    /// <summary>Generous for a photograph, small enough that a mistake cannot fill the disk.</summary>
    private const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>
    /// The only formats accepted, each identified by bytes a real file of that type carries at a
    /// fixed place near its start. The extension on the right is what the file is saved as.
    ///
    /// WebP is a RIFF container: "RIFF", four bytes of length, then "WEBP". Checking only "RIFF"
    /// would also let through audio and video, so both markers must be there. Product photos saved
    /// from the web are very often WebP.
    /// </summary>
    private static readonly (Func<byte[], bool> Matches, string Extension, string ContentType)[] AllowedImages =
    {
        (h => StartsWith(h, 0, 0xFF, 0xD8, 0xFF), ".jpg", "image/jpeg"),
        (h => StartsWith(h, 0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), ".png", "image/png"),
        (h => StartsWith(h, 0, 0x47, 0x49, 0x46, 0x38), ".gif", "image/gif"),
        (h => StartsWith(h, 0, (byte)'R', (byte)'I', (byte)'F', (byte)'F') &&
              StartsWith(h, 8, (byte)'W', (byte)'E', (byte)'B', (byte)'P'), ".webp", "image/webp"),
    };

    private static bool StartsWith(byte[] header, int offset, params byte[] magic) =>
        header.Length >= offset + magic.Length && header.AsSpan(offset, magic.Length).SequenceEqual(magic);

    private readonly IMediaStorage _storage;
    private readonly ILogger<AdminUploadsController> _logger;

    public AdminUploadsController(IMediaStorage storage, ILogger<AdminUploadsController> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public sealed record UploadedFileDto(string Url, string FileName, long SizeBytes);

    [HttpPost("image")]
    [Authorize(Policy = Permissions.AuctionCreate)]
    [RequestSizeLimit(MaxBytes + 8192)]
    public async Task<ActionResult<UploadedFileDto>> UploadImage(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "No file was received.");
        }

        if (file.Length > MaxBytes)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed,
                $"The image is larger than the {MaxBytes / (1024 * 1024)} MB limit.");
        }

        // Read the signature from the content itself. Everything the browser claimed is ignored.
        var header = new byte[12];
        await using (var probe = file.OpenReadStream())
        {
            var read = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            if (read < 3)
            {
                throw new DomainException(ErrorCodes.ValidationFailed, "The file is not a readable image.");
            }
        }

        var match = AllowedImages.FirstOrDefault(a => a.Matches(header));
        if (match.Extension is null)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed,
                "Only JPEG, PNG, WebP and GIF images are accepted.");
        }

        // The name is ours. Nothing the caller sent reaches the filesystem or the container.
        var storedName = $"{Guid.NewGuid():N}{match.Extension}";

        string url;
        await using (var source = file.OpenReadStream())
        {
            url = await _storage.SaveAsync(source, storedName, match.ContentType, ct);
        }

        _logger.LogInformation(
            "Administrator {UserId} uploaded {StoredName} ({Bytes} bytes) to {Url}.",
            CurrentUserId, storedName, file.Length, url);

        return Ok(new UploadedFileDto(url, storedName, file.Length));
    }
}
