using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AuctionPlatform.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// A Cloudflare R2 bucket, the values from the Cloudflare dashboard (see docs/R2-STORAGE.md).
///
/// The access key pair is a secret and is only ever supplied by the environment (web.config on the
/// host), never by a file in the repository.
/// </summary>
public sealed class R2StorageOptions
{
    /// <summary>The Cloudflare account id, shown on the R2 overview page.</summary>
    public string? AccountId { get; set; }

    /// <summary>The "Access Key ID" of an R2 API token with Object Read &amp; Write on the bucket.</summary>
    public string? AccessKeyId { get; set; }

    /// <summary>The "Secret Access Key" shown once when that token is created.</summary>
    public string? SecretAccessKey { get; set; }

    /// <summary>The bucket pictures are written into.</summary>
    public string? Bucket { get; set; }

    /// <summary>
    /// Where the bucket is publicly readable: a custom domain connected to it
    /// (https://images.dealgosa.com) or its r2.dev address (https://pub-….r2.dev). Stored picture
    /// addresses start with this, so changing it later leaves earlier addresses as they were.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// The S3 endpoint. Empty means R2's own, https://&lt;AccountId&gt;.r2.cloudflarestorage.com;
    /// set only to point the store at another S3-compatible server, as the tests do.
    /// </summary>
    public string? Endpoint { get; set; }

    public string ResolvedEndpoint =>
        string.IsNullOrWhiteSpace(Endpoint)
            ? $"https://{AccountId?.Trim()}.r2.cloudflarestorage.com"
            : Endpoint.Trim();

    /// <summary>The settings that are missing, by the name they have in configuration.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(AccountId) && string.IsNullOrWhiteSpace(Endpoint)) missing.Add("Storage:R2:AccountId");
        if (string.IsNullOrWhiteSpace(AccessKeyId)) missing.Add("Storage:R2:AccessKeyId");
        if (string.IsNullOrWhiteSpace(SecretAccessKey)) missing.Add("Storage:R2:SecretAccessKey");
        if (string.IsNullOrWhiteSpace(Bucket)) missing.Add("Storage:R2:Bucket");
        if (!Uri.TryCreate(PublicBaseUrl?.Trim(), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            missing.Add("Storage:R2:PublicBaseUrl");
        }
        return missing;
    }
}

/// <summary>
/// Keeps uploaded pictures in a Cloudflare R2 bucket, through R2's S3-compatible API.
///
/// Objects are written under "uploads/", so a picture the local store served at /uploads/x.jpg
/// lives at &lt;PublicBaseUrl&gt;/uploads/x.jpg once copied — the same path on a different host,
/// which is what lets old addresses be redirected rather than rewritten.
///
/// The returned address is absolute. The app, the console and the site all leave an address with a
/// scheme exactly as it is, so nothing needs to know that a picture is no longer on our own disk.
/// </summary>
public sealed class R2MediaStorage : IMediaStorage, IDisposable
{
    public const string KeyPrefix = "uploads/";

    private readonly IAmazonS3 _client;
    private readonly R2StorageOptions _options;
    private readonly ILogger<R2MediaStorage> _logger;
    private readonly bool _https;

    public R2MediaStorage(IOptions<StorageOptions> options, ILogger<R2MediaStorage> logger)
    {
        _options = options.Value.R2;
        _logger = logger;

        var missing = _options.Missing();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Storage:Provider is R2 but these settings are empty or invalid: "
                + string.Join(", ", missing) + ". See docs/R2-STORAGE.md.");
        }

        var endpoint = _options.ResolvedEndpoint;
        _https = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        _client = new AmazonS3Client(
            new BasicAWSCredentials(_options.AccessKeyId!.Trim(), _options.SecretAccessKey!.Trim()),
            new AmazonS3Config
            {
                ServiceURL = endpoint,
                // R2 has one region, "auto", and addresses buckets by path.
                AuthenticationRegion = "auto",
                ForcePathStyle = true,
                // R2 does not accept the checksum trailers newer SDKs add to every upload.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
                Timeout = TimeSpan.FromSeconds(60),
            });
    }

    /// <summary>The address a stored name is (or will be) served at.</summary>
    public string PublicUrlFor(string storedName) => PublicUrlForKey(KeyPrefix + storedName);

    /// <summary>The address an object key such as "catalog/x.webp" is served at.</summary>
    public string PublicUrlForKey(string key) =>
        $"{_options.PublicBaseUrl!.Trim().TrimEnd('/')}/{key}";

    /// <summary>The bucket's public address, without a trailing slash.</summary>
    public string PublicBaseUrl => _options.PublicBaseUrl!.Trim().TrimEnd('/');

    public async Task<string> SaveAsync(
        Stream content, string storedName, string contentType, CancellationToken ct = default)
    {
        // The name is a GUID, so these bytes can never change. A year is safe.
        await SaveObjectAsync(content, KeyPrefix + storedName, contentType, "public, max-age=31536000, immutable", ct);
        return PublicUrlFor(storedName);
    }

    /// <summary>Writes an object under any key, e.g. "catalog/x.webp".</summary>
    public async Task SaveObjectAsync(
        Stream content, string key, string contentType, string cacheControl, CancellationToken ct = default)
    {
        // The SDK needs the length up front once chunked signing is off. Uploads are capped at a
        // few megabytes, so holding an unseekable one in memory costs nothing worth avoiding.
        Stream body = content;
        MemoryStream? buffered = null;
        if (!content.CanSeek)
        {
            buffered = new MemoryStream();
            await content.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            body = buffered;
        }

        try
        {
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _options.Bucket!.Trim(),
                Key = key,
                InputStream = body,
                AutoCloseStream = false,
                ContentType = contentType,
                Headers = { CacheControl = cacheControl },
                // R2 does not support the SDK's streaming (chunked) signature. Over HTTPS the
                // payload hash can be skipped, as Cloudflare recommends; plain HTTP (only a local
                // test server) has to sign it.
                UseChunkEncoding = false,
                DisablePayloadSigning = _https,
            }, ct);
        }
        finally
        {
            buffered?.Dispose();
        }

        _logger.LogInformation("Stored {Key} in R2 bucket {Bucket}.", key, _options.Bucket);
    }

    /// <summary>Whether an uploaded picture of this name is already in the bucket.</summary>
    public Task<bool> ExistsAsync(string storedName, CancellationToken ct = default) =>
        ObjectExistsAsync(KeyPrefix + storedName, ct);

    /// <summary>Whether an object with this key is already in the bucket.</summary>
    public async Task<bool> ObjectExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_options.Bucket!.Trim(), key, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
