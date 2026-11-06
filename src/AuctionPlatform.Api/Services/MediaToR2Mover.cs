using System.Text.RegularExpressions;
using AuctionPlatform.Infrastructure.Services;

namespace AuctionPlatform.Api.Services;

/// <summary>
/// Moves the site's own pictures to the R2 bucket, once, when the site switches to R2: the
/// pictures administrators uploaded (wwwroot/uploads) and the built-in catalog pictures
/// (wwwroot/catalog).
///
/// Two steps. Each file is copied to the same path in the bucket, skipping any already there. Then
/// every database address pointing at a picture now in the bucket ("/uploads/x.jpg") is rewritten
/// to the bucket's address ("https://images.dealgosa.com/uploads/x.jpg"). Nothing is deleted from
/// the host, and running it again only does what is still left to do.
/// </summary>
public sealed partial class MediaToR2Mover
{
    /// <summary>The wwwroot folders that hold pictures, each copied to the same path in the bucket.</summary>
    public static readonly string[] Folders = [LocalMediaStorage.FolderName, "catalog"];

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    /// <summary>The names the upload endpoint gives files, plus the plainer names of the catalog.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.(jpg|jpeg|png|gif|webp)$", RegexOptions.IgnoreCase)]
    public static partial Regex FileNamePattern();

    private readonly R2MediaStorage _r2;
    private readonly MediaAddressRewriter _rewriter;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<MediaToR2Mover> _logger;

    public MediaToR2Mover(
        R2MediaStorage r2, MediaAddressRewriter rewriter, IWebHostEnvironment environment, ILogger<MediaToR2Mover> logger)
    {
        _r2 = r2;
        _rewriter = rewriter;
        _environment = environment;
        _logger = logger;
    }

    public sealed record MoveResult(
        int Copied, int AlreadyThere, int Skipped, IReadOnlyList<string> Failed, int AddressesUpdated);

    public async Task<MoveResult> MoveAsync(CancellationToken ct = default)
    {
        var root = string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;

        int copied = 0, alreadyThere = 0, skipped = 0;
        var failed = new List<string>();
        var inBucket = new List<string>();

        foreach (var folderName in Folders)
        {
            var folder = Path.Combine(root, folderName);
            if (!Directory.Exists(folder)) continue;

            // Catalog pictures keep their names when a new version replaces one, so they are
            // cached for a day; uploads are named after a fresh GUID and can never change.
            var cacheControl = folderName == LocalMediaStorage.FolderName
                ? "public, max-age=31536000, immutable"
                : "public, max-age=86400";

            foreach (var path in Directory.EnumerateFiles(folder))
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(path);
                var key = $"{folderName}/{name}";

                if (!FileNamePattern().IsMatch(name)
                    || !ContentTypes.TryGetValue(Path.GetExtension(name), out var contentType))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    if (await _r2.ObjectExistsAsync(key, ct))
                    {
                        alreadyThere++;
                    }
                    else
                    {
                        await using var stream = File.OpenRead(path);
                        await _r2.SaveObjectAsync(stream, key, contentType, cacheControl, ct);
                        copied++;
                    }

                    inBucket.Add("/" + key);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogError(e, "Could not copy {Key} to R2.", key);
                    failed.Add(key);
                }
            }
        }

        // Only pictures confirmed in the bucket get their addresses changed; a failed copy keeps
        // its old address, which still works, and is picked up by the next run.
        var updated = await _rewriter.RewriteAsync(inBucket, _r2.PublicBaseUrl, ct);

        _logger.LogInformation(
            "Moved pictures to R2: {Copied} copied, {AlreadyThere} already there, {Skipped} skipped, " +
            "{Failed} failed, {Updated} database addresses updated.",
            copied, alreadyThere, skipped, failed.Count, updated);

        return new MoveResult(copied, alreadyThere, skipped, failed, updated);
    }
}
