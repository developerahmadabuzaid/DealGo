using Amazon.Runtime;
using Amazon.S3;
using AuctionPlatform.Api.Services;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The one-off move of the site's own pictures to R2: the files go to the same path in the bucket,
/// and the database's addresses for them change to the bucket's — but only for pictures that are
/// actually there, and without touching addresses that belong to anything else.
/// </summary>
[Collection("database")]
public sealed class MediaToR2MoverTests : IAsyncLifetime
{
    private const string Bucket = "dealgo-move-test";
    private const string PublicBase = "https://images.example.test";

    private readonly DatabaseFixture _fixture;

    private readonly IContainer _s3Server = new ContainerBuilder("adobe/s3mock:4.7.0")
        .WithPortBinding(9090, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/")))
        .Build();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "auction-move-tests", Guid.NewGuid().ToString("N"));

    // Names unique to this run: the database is shared with every other test.
    private readonly string _upload = $"{Guid.NewGuid():N}.jpg";
    private readonly string _catalog = $"cat-{Guid.NewGuid():N}.webp";

    private string Endpoint => $"http://{_s3Server.Hostname}:{_s3Server.GetMappedPublicPort(9090)}";

    public MediaToR2MoverTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _s3Server.StartAsync();
        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials("k", "s"),
            new AmazonS3Config { ServiceURL = Endpoint, ForcePathStyle = true, AuthenticationRegion = "auto" });
        await s3.PutBucketAsync(Bucket);

        Directory.CreateDirectory(Path.Combine(_root, "wwwroot", "uploads"));
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot", "catalog"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "wwwroot", "uploads", _upload), [0xFF, 0xD8, 0xFF, 1]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "wwwroot", "catalog", _catalog), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(_root, "wwwroot", "uploads", "readme.txt"), "not a picture");
    }

    [Fact]
    public async Task Pictures_are_copied_and_only_their_addresses_point_at_the_bucket()
    {
        var missing = $"/uploads/{Guid.NewGuid():N}.png"; // in the database, but no file anywhere
        var elsewhere = "https://cdn.example.org/photo.jpg";
        Guid productId, imageId, otherImageId, prizeProductId;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await TestData.CreateStoreProductAsync(db, 10m, 5);
            productId = product.Id;

            var image = new StoreProductImage { ProductId = product.Id, Url = $"/catalog/{_catalog}", IsPrimary = true };
            var other = new StoreProductImage { ProductId = product.Id, Url = missing, DisplayOrder = 1 };
            db.StoreProductImages.AddRange(image, other);

            var prize = new Product
            {
                Name = "Prize",
                Price = 1m,
                PrimaryImageUrl = $"/uploads/{_upload}",
                ImageUrls = [$"/uploads/{_upload}", elsewhere],
            };
            db.Products.Add(prize);
            await db.SaveChangesAsync();

            imageId = image.Id;
            otherImageId = other.Id;
            prizeProductId = prize.Id;
        }

        var first = await MoveAsync();
        first.Copied.Should().Be(2);
        first.Skipped.Should().Be(1);
        first.Failed.Should().BeEmpty();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            (await db.StoreProductImages.SingleAsync(i => i.Id == imageId)).Url
                .Should().Be($"{PublicBase}/catalog/{_catalog}");
            (await db.StoreProductImages.SingleAsync(i => i.Id == otherImageId)).Url
                .Should().Be(missing, "a picture that is not in the bucket keeps its address");

            var prize = await db.Products.SingleAsync(p => p.Id == prizeProductId);
            prize.PrimaryImageUrl.Should().Be($"{PublicBase}/uploads/{_upload}");
            prize.ImageUrls.Should().Equal($"{PublicBase}/uploads/{_upload}", elsewhere);
        }

        // Running it again finds everything done and changes nothing.
        var second = await MoveAsync();
        second.Copied.Should().Be(0);
        second.AlreadyThere.Should().Be(2);

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.StoreProductImages.SingleAsync(i => i.Id == imageId)).Url
                .Should().Be($"{PublicBase}/catalog/{_catalog}");
        }
    }

    private async Task<MediaToR2Mover.MoveResult> MoveAsync()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();

        using var storage = new R2MediaStorage(
            Options.Create(new StorageOptions
            {
                Provider = "R2",
                R2 = new R2StorageOptions
                {
                    AccountId = "unused-with-endpoint",
                    AccessKeyId = "k",
                    SecretAccessKey = "s",
                    Bucket = Bucket,
                    PublicBaseUrl = PublicBase,
                    Endpoint = Endpoint,
                },
            }),
            NullLogger<R2MediaStorage>.Instance);

        var mover = new MediaToR2Mover(
            storage,
            new MediaAddressRewriter(db, cache, NullLogger<MediaAddressRewriter>.Instance),
            new TestWebHostEnvironment { WebRootPath = Path.Combine(_root, "wwwroot"), ContentRootPath = _root },
            NullLogger<MediaToR2Mover>.Instance);

        return await mover.MoveAsync();
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
        await _s3Server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
