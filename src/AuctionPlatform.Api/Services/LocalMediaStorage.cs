using AuctionPlatform.Application.Common;

namespace AuctionPlatform.Api.Services;

/// <summary>
/// Keeps uploaded pictures in <c>wwwroot/uploads</c>, served by the same static-file middleware as
/// the console.
///
/// This is the development arrangement, and the one a single long-lived server can use. It is not
/// suitable for a container that is replaced on every deployment — there the pictures go with the
/// old container — so production points <c>Storage:Provider</c> at a storage account instead.
/// </summary>
public sealed class LocalMediaStorage : IMediaStorage
{
    public const string FolderName = "uploads";

    private readonly IWebHostEnvironment _environment;

    public LocalMediaStorage(IWebHostEnvironment environment) => _environment = environment;

    public async Task<string> SaveAsync(
        Stream content, string storedName, string contentType, CancellationToken ct = default)
    {
        var root = _environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(_environment.ContentRootPath, "wwwroot");
        }

        var folder = Path.Combine(root, FolderName);
        Directory.CreateDirectory(folder);

        await using (var destination = File.Create(Path.Combine(folder, storedName)))
        {
            await content.CopyToAsync(destination, ct);
        }

        return $"/{FolderName}/{storedName}";
    }
}
