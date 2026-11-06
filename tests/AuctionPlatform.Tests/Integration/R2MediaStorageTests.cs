using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AuctionPlatform.Api.Services;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The R2 store against a real S3-compatible server. R2 itself cannot run in a test, but it speaks
/// the S3 API, and S3Mock stands in for it: what is checked here is that objects land under
/// the right key, with the right headers, and come back at the public address the store returns.
/// </summary>
public sealed class R2MediaStorageTests : IAsyncLifetime
{
    private const string Bucket = "dealgo-test";
    private const string PublicBase = "https://images.example.test";

    // Adobe S3Mock: a small S3-compatible server that accepts any key pair.
    private const string AccessKey = "test-access-key";
    private const string SecretKey = "test-secret-key";

    private readonly IContainer _s3Server = new ContainerBuilder("adobe/s3mock:4.7.0")
        .WithPortBinding(9090, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/")))
        .Build();

    private string Endpoint => $"http://{_s3Server.Hostname}:{_s3Server.GetMappedPublicPort(9090)}";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "auction-r2-tests", Guid.NewGuid().ToString("N"));

    private AmazonS3Client _s3 = null!;

    public async Task InitializeAsync()
    {
        await _s3Server.StartAsync();
        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config { ServiceURL = Endpoint, ForcePathStyle = true, AuthenticationRegion = "auto" });
        await _s3.PutBucketAsync(Bucket);
    }

    private R2MediaStorage CreateStorage() => new(
        Options.Create(new StorageOptions
        {
            Provider = "R2",
            R2 = new R2StorageOptions
            {
                AccountId = "unused-with-endpoint",
                AccessKeyId = AccessKey,
                SecretAccessKey = SecretKey,
                Bucket = Bucket,
                PublicBaseUrl = PublicBase + "/",
                Endpoint = Endpoint,
            },
        }),
        NullLogger<R2MediaStorage>.Instance);

    [Fact]
    public async Task A_saved_picture_is_in_the_bucket_under_uploads_and_its_public_address_is_returned()
    {
        using var storage = CreateStorage();
        var bytes = Encoding.UTF8.GetBytes("a picture, as far as this test is concerned");

        await using var content = new MemoryStream(bytes);
        var url = await storage.SaveAsync(content, "abc123.png", "image/png");

        url.Should().Be($"{PublicBase}/uploads/abc123.png");

        using var stored = await _s3.GetObjectAsync(Bucket, "uploads/abc123.png");
        stored.Headers.ContentType.Should().Be("image/png");
        stored.Headers.CacheControl.Should().Contain("immutable");
        using var read = new MemoryStream();
        await stored.ResponseStream.CopyToAsync(read);
        read.ToArray().Should().Equal(bytes);

        (await storage.ExistsAsync("abc123.png")).Should().BeTrue();
        (await storage.ExistsAsync("missing.png")).Should().BeFalse();
    }

    [Fact]
    public async Task A_stream_that_cannot_seek_is_still_stored()
    {
        using var storage = CreateStorage();
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 };

        await using var content = new ForwardOnlyStream(bytes);
        await storage.SaveAsync(content, "forward.jpg", "image/jpeg");

        (await storage.ExistsAsync("forward.jpg")).Should().BeTrue();
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
    }

    public async Task DisposeAsync()
    {
        _s3?.Dispose();
        await _s3Server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
