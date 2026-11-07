using System.Diagnostics;
using AuctionPlatform.Api.Controllers.Admin;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Infrastructure.Services;

namespace AuctionPlatform.Api.Services;

/// <summary>
/// Shrinks the pictures administrators uploaded before uploads were shrunk (some are 1.8 MB
/// posters), once: each is read, made fit for phones (<see cref="IImageShrinker"/>), stored as a new
/// "-s.webp" file next to it, and every database address of it is pointed at the new file.
///
/// Nothing is deleted, so a picture can never go missing: the old file stays where it was. Pictures
/// already small, animations and anything unreadable are left as they are. It works in rounds of
/// about a minute, because the host ends long requests; running it again does only what is left.
/// </summary>
public sealed class ExistingPictureShrinker
{
    public sealed record Result(
        int Checked,
        int Shrunk,
        int KeptAsTheyWere,
        long BytesBefore,
        long BytesAfter,
        int AddressesUpdated,
        int Remaining,
        IReadOnlyList<string> Failed);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(70);

    private readonly IMediaStorage _storage;
    private readonly IImageShrinker _shrinker;
    private readonly MediaAddressRewriter _rewriter;
    private readonly IHttpClientFactory _http;
    private readonly IWebHostEnvironment _environment;
    private readonly R2MediaStorage? _r2;
    private readonly ILogger<ExistingPictureShrinker> _logger;

    public ExistingPictureShrinker(
        IMediaStorage storage,
        IImageShrinker shrinker,
        MediaAddressRewriter rewriter,
        IHttpClientFactory http,
        IWebHostEnvironment environment,
        IServiceProvider services,
        ILogger<ExistingPictureShrinker> logger)
    {
        _storage = storage;
        _shrinker = shrinker;
        _rewriter = rewriter;
        _http = http;
        _environment = environment;
        _r2 = services.GetService<R2MediaStorage>();
        _logger = logger;
    }

    public async Task<Result> ShrinkAsync(CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();

        // Every address of an uploaded picture, by the file it names: the same picture may be kept
        // as "/uploads/x.jpg" in one place and "https://images…/uploads/x.jpg" in another.
        var byFile = (await _rewriter.AddressesInUseAsync(ct))
            .Select(url => (Url: url, File: UploadedFileName(url)))
            .Where(x => x.File is not null && !x.File.EndsWith(AdminUploadsController.ShrunkSuffix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.File!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int checkedCount = 0, shrunk = 0, kept = 0;
        long before = 0, after = 0;
        var failed = new List<string>();
        var newAddressFor = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in byFile)
        {
            if (clock.Elapsed > Budget) break;
            checkedCount++;

            try
            {
                var original = await ReadAsync(file.Key, file.Select(x => x.Url), ct);
                if (original is null)
                {
                    failed.Add($"{file.Key}: not found");
                    continue;
                }

                var small = _shrinker.Shrink(original);
                if (small is null)
                {
                    kept++;
                    continue;
                }

                var name = $"{Path.GetFileNameWithoutExtension(file.Key)}{AdminUploadsController.ShrunkSuffix}";
                using var content = new MemoryStream(small.Bytes, writable: false);
                var url = await _storage.SaveAsync(content, name, small.ContentType, ct);

                foreach (var (old, _) in file) newAddressFor[old] = url;
                shrunk++;
                before += original.Length;
                after += small.Bytes.Length;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not shrink {File}.", file.Key);
                if (failed.Count < 10) failed.Add($"{file.Key}: {ex.Message}");
            }
        }

        var updated = await _rewriter.ReplaceAsync(newAddressFor, ct);
        var result = new Result(checkedCount, shrunk, kept, before, after, updated, byFile.Count - checkedCount, failed);

        _logger.LogInformation(
            "Shrank {Shrunk} of {Checked} uploaded pictures ({Before} -> {After} bytes), {Updated} addresses updated, {Remaining} left.",
            shrunk, checkedCount, before, after, updated, result.Remaining);
        return result;
    }

    /// <summary>The stored file name when the address is one of our uploads, else null.</summary>
    private string? UploadedFileName(string url)
    {
        string? path = null;
        if (url.StartsWith("/uploads/", StringComparison.Ordinal))
        {
            path = url;
        }
        else if (_r2 is not null && url.StartsWith(_r2.PublicBaseUrl + "/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            path = url[_r2.PublicBaseUrl.Length..];
        }

        var name = path?["/uploads/".Length..];
        return name is not null && MediaToR2Mover.FileNamePattern().IsMatch(name) ? name : null;
    }

    /// <summary>The picture's bytes: from this site's folder when it is there, else from the bucket.</summary>
    private async Task<byte[]?> ReadAsync(string fileName, IEnumerable<string> addresses, CancellationToken ct)
    {
        var local = Path.Combine(_environment.WebRootPath ?? "wwwroot", "uploads", fileName);
        if (File.Exists(local)) return await File.ReadAllBytesAsync(local, ct);

        var remote = addresses.FirstOrDefault(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                     ?? (_r2 is null ? null : _r2.PublicUrlFor(fileName));
        if (remote is null) return null;

        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var response = await client.GetAsync(remote, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
    }
}
