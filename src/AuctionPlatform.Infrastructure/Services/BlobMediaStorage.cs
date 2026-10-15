using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AuctionPlatform.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>How uploaded pictures are kept, and where.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>"Local" (a folder beside the API) or "AzureBlob" (a storage account).</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>
    /// The account's address, e.g. https://dealgomedia.blob.core.windows.net.
    ///
    /// Preferred over a connection string: with this set the API signs in as itself — the identity
    /// Azure gave the container app — so there is no key to store, rotate or leak. The identity
    /// needs the "Storage Blob Data Contributor" role on the account.
    /// </summary>
    public string? AccountUrl { get; set; }

    /// <summary>
    /// The storage account's connection string, for a machine with no managed identity. Held as a
    /// secret, never in a file. Ignored when <see cref="AccountUrl"/> is set.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>The container pictures are written into. Created on first use if it is missing.</summary>
    public string Container { get; set; } = "uploads";

    /// <summary>
    /// The address pictures are served from, when it is not the account's own. Set this to a CDN
    /// or a custom domain in front of the container; leave it empty to use the blob's own URL.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public bool UsesAzureBlob =>
        string.Equals(Provider, "AzureBlob", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Keeps uploaded pictures in an Azure storage account.
///
/// The container is created once, readable by anyone who has the address: these are pictures meant
/// to be shown in an app and on a public page, and a signed URL per picture would expire while the
/// address sits in the database. Nothing private is ever written here — the name of every blob is
/// a fresh GUID chosen by the API, so an address cannot be guessed from what it holds.
/// </summary>
public sealed class BlobMediaStorage : IMediaStorage
{
    private readonly BlobContainerClient _container;
    private readonly StorageOptions _options;
    private readonly ILogger<BlobMediaStorage> _logger;
    private readonly SemaphoreSlim _ready = new(1, 1);
    private bool _containerChecked;

    public BlobMediaStorage(IOptions<StorageOptions> options, ILogger<BlobMediaStorage> logger)
    {
        _options = options.Value;
        _logger = logger;

        var service = _options switch
        {
            { AccountUrl: { Length: > 0 } url } => new BlobServiceClient(new Uri(url), new DefaultAzureCredential()),
            { ConnectionString: { Length: > 0 } key } => new BlobServiceClient(key),
            _ => throw new InvalidOperationException(
                "Storage:Provider is AzureBlob but neither Storage:AccountUrl nor "
                + "Storage:ConnectionString is set."),
        };

        _container = service.GetBlobContainerClient(_options.Container);
    }

    public async Task<string> SaveAsync(
        Stream content, string storedName, string contentType, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);

        var blob = _container.GetBlobClient(storedName);

        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType,
                    // The name is a GUID, so these bytes can never change. A year is safe.
                    CacheControl = "public, max-age=31536000, immutable"
                }
            },
            ct);

        _logger.LogInformation("Stored {Name} in container {Container}.", storedName, _options.Container);

        return string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
            ? blob.Uri.ToString()
            : $"{_options.PublicBaseUrl.TrimEnd('/')}/{storedName}";
    }

    /// <summary>
    /// Creates the container the first time something is uploaded.
    ///
    /// Done here rather than at start-up so the API still starts when storage is briefly
    /// unreachable, and guarded by a lock so two simultaneous uploads do not both try.
    /// </summary>
    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerChecked) return;

        await _ready.WaitAsync(ct);
        try
        {
            if (_containerChecked) return;

            await _container.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);
            _containerChecked = true;
        }
        finally
        {
            _ready.Release();
        }
    }
}
