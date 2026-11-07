using AuctionPlatform.Api.Services;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using AuctionPlatform.Tests.Unit;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The one-off shrinking of pictures uploaded before uploads were shrunk: each big one gets a small
/// copy, every address of it is pointed at the copy, and nothing is deleted.
/// </summary>
[Collection("database")]
public class ExistingPictureShrinkerTests : IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "auction-shrink-tests", Guid.NewGuid().ToString("N"));

    public ExistingPictureShrinkerTests(DatabaseFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_big_upload_is_replaced_by_its_small_copy_everywhere_and_kept_on_disk()
    {
        var uploads = Path.Combine(_root, "wwwroot", "uploads");
        Directory.CreateDirectory(uploads);

        var big = $"{Guid.NewGuid():N}.jpg";
        var small = $"{Guid.NewGuid():N}.jpg";
        await File.WriteAllBytesAsync(Path.Combine(uploads, big), ImageShrinkerTests.Photo(3000, 2000));
        await File.WriteAllBytesAsync(Path.Combine(uploads, small), ImageShrinkerTests.Photo(300, 300));

        Guid bigCategory, smallCategory, bigImage;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var a = new StoreCategory { Name = $"Big {big}", Slug = $"big-{big}", ImageUrl = $"/uploads/{big}" };
            var b = new StoreCategory { Name = $"Small {small}", Slug = $"small-{small}", ImageUrl = $"/uploads/{small}" };
            db.StoreCategories.AddRange(a, b);

            var product = await TestData.CreateStoreProductAsync(db, 10m, 1);
            var image = new StoreProductImage { ProductId = product.Id, Url = $"/uploads/{big}", IsPrimary = true };
            db.StoreProductImages.Add(image);
            await db.SaveChangesAsync();
            (bigCategory, smallCategory, bigImage) = (a.Id, b.Id, image.Id);
        }

        var first = await RunAsync();
        first.Shrunk.Should().BeGreaterThanOrEqualTo(1);
        first.BytesAfter.Should().BeLessThan(first.BytesBefore);

        var copy = $"{Path.GetFileNameWithoutExtension(big)}-s.webp";
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.StoreCategories.AsNoTracking().SingleAsync(c => c.Id == bigCategory)).ImageUrl.Should().Be($"/uploads/{copy}");
            (await db.StoreProductImages.AsNoTracking().SingleAsync(i => i.Id == bigImage)).Url.Should().Be($"/uploads/{copy}",
                "every address of the picture is moved, in every table");
            (await db.StoreCategories.AsNoTracking().SingleAsync(c => c.Id == smallCategory)).ImageUrl.Should().Be($"/uploads/{small}",
                "a picture already small is left alone");
        }

        File.Exists(Path.Combine(uploads, copy)).Should().BeTrue();
        File.Exists(Path.Combine(uploads, big)).Should().BeTrue("nothing is deleted");
        new FileInfo(Path.Combine(uploads, copy)).Length.Should().BeLessThan(new FileInfo(Path.Combine(uploads, big)).Length / 3);

        // Again: the copy is never shrunk a second time.
        var second = await RunAsync();
        await using (var scope = _fixture.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().StoreCategories.AsNoTracking()
                .SingleAsync(c => c.Id == bigCategory)).ImageUrl.Should().Be($"/uploads/{copy}");
        }
        File.Exists(Path.Combine(uploads, $"{Path.GetFileNameWithoutExtension(copy)}-s.webp")).Should().BeFalse();
        second.Failed.Should().NotContain(f => f.Contains(big) || f.Contains(small));
    }

    private async Task<ExistingPictureShrinker.Result> RunAsync()
    {
        await using var scope = _fixture.CreateScope();
        var environment = new Environment(Path.Combine(_root, "wwwroot"), _root);
        var shrinker = new ExistingPictureShrinker(
            new LocalMediaStorage(environment),
            new ImageShrinker(NullLogger<ImageShrinker>.Instance),
            new MediaAddressRewriter(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                scope.ServiceProvider.GetRequiredService<ICacheService>(),
                NullLogger<MediaAddressRewriter>.Instance),
            new NoHttp(),
            environment,
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<ExistingPictureShrinker>.Instance);

        return await shrinker.ShrinkAsync();
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No download expected: the files are on disk.");
    }

    private sealed class Environment(string webRoot, string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Testing";
    }
}
