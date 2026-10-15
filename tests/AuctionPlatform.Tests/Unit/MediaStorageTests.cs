using System.Text;
using AuctionPlatform.Api.Services;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Tests.Unit;

/// <summary>
/// Where uploaded pictures go.
///
/// The local store is exercised for real; the blob store is only checked for the decision that
/// matters without an account — that a misconfigured production refuses to start rather than
/// quietly writing pictures onto a disk that the next deployment deletes.
/// </summary>
public class MediaStorageTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "auction-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_local_store_writes_the_file_and_returns_the_address_it_is_served_at()
    {
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));

        var storage = new LocalMediaStorage(new TestWebHostEnvironment
        {
            WebRootPath = Path.Combine(_root, "wwwroot"),
            ContentRootPath = _root,
        });

        var bytes = Encoding.UTF8.GetBytes("a picture, as far as this test is concerned");
        await using var content = new MemoryStream(bytes);

        var url = await storage.SaveAsync(content, "abc123.png", "image/png");

        url.Should().Be("/uploads/abc123.png");
        File.ReadAllBytes(Path.Combine(_root, "wwwroot", "uploads", "abc123.png"))
            .Should().Equal(bytes);
    }

    [Fact]
    public void The_blob_store_refuses_to_start_without_a_connection_string()
    {
        var options = Options.Create(new StorageOptions { Provider = "AzureBlob" });

        var act = () => new BlobMediaStorage(
            options, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlobMediaStorage>.Instance);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("AzureBlob", true)]
    [InlineData("azureblob", true)]
    [InlineData("Local", false)]
    [InlineData("", false)]
    public void The_provider_is_read_without_caring_about_capitals(string provider, bool expected)
    {
        new StorageOptions { Provider = provider }.UsesAzureBlob.Should().Be(expected);
    }

    /// <summary>Just enough of the hosting environment for the local store to find its folder.</summary>
    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
