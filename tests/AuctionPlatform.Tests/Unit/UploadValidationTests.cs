using System.Security.Claims;
using System.Text;
using AuctionPlatform.Api.Controllers.Admin;
using AuctionPlatform.Api.Services;
using AuctionPlatform.Domain.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuctionPlatform.Tests.Unit;

/// <summary>
/// Uploaded images are served back from the same origin as the console, so what the upload accepts
/// is a security boundary. These tests pin the rule that the decision is made by reading the file,
/// never by trusting the name or the Content-Type the browser sent.
/// </summary>
public class UploadValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "auction-upload-tests", Guid.NewGuid().ToString("N"));

    private AdminUploadsController BuildController()
    {
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));

        var environment = new TestWebHostEnvironment
        {
            WebRootPath = Path.Combine(_root, "wwwroot"),
            ContentRootPath = _root,
        };

        // The controller no longer writes files itself: it hands them to storage, which here is the
        // same folder-based one the API uses while developing.
        var storage = new LocalMediaStorage(environment);

        var controller = new AdminUploadsController(storage, NullLogger<AdminUploadsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "test")),
                },
            },
        };

        return controller;
    }

    /// <summary>A real 1x1 PNG, built here so the bytes are genuine rather than a stand-in.</summary>
    private static byte[] RealPng()
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            var crc = System.IO.Hashing.Crc32.Hash(body);
            Array.Reverse(crc);

            return BitConverter.GetBytes(data.Length).Reverse()
                .Concat(body).Concat(crc).ToArray();
        }

        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0 };

        using var raw = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            deflate.Write(new byte[] { 0x00, 0xFF, 0x00, 0x00 });
        }

        return header
            .Concat(Chunk("IHDR", ihdr))
            .Concat(Chunk("IDAT", raw.ToArray()))
            .Concat(Chunk("IEND", Array.Empty<byte>()))
            .ToArray();
    }

    private static IFormFile FileFrom(byte[] content, string name, string contentType)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    [Fact]
    public async Task A_real_image_is_stored_under_a_name_the_server_chose()
    {
        var controller = BuildController();
        var bytes = RealPng();

        var result = await controller.UploadImage(
            FileFrom(bytes, "my photo.png", "image/png"), CancellationToken.None);

        var dto = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AdminUploadsController.UploadedFileDto>().Subject;

        dto.Url.Should().StartWith("/uploads/").And.EndWith(".png");
        dto.SizeBytes.Should().Be(bytes.Length);

        // The caller's name reaches neither the address nor the disk.
        dto.FileName.Should().NotContain("my photo");
        dto.FileName.Should().MatchRegex("^[0-9a-f]{32}\\.png$");

        File.Exists(Path.Combine(_root, "wwwroot", "uploads", dto.FileName)).Should().BeTrue();
    }

    /// <summary>
    /// The important one. A page claiming to be an image would otherwise be served back from the
    /// platform's own origin, which is stored cross-site scripting.
    /// </summary>
    [Fact]
    public async Task Html_wearing_an_image_name_is_refused()
    {
        var controller = BuildController();
        var html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");

        var act = async () => await controller.UploadImage(
            FileFrom(html, "innocent.png", "image/png"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.ValidationFailed);

        Directory.Exists(Path.Combine(_root, "wwwroot", "uploads"))
            .Should().BeFalse("nothing may be written before the content is accepted");
    }

    /// <summary>The header of a WebP file: a RIFF container whose form type is WEBP.</summary>
    private static byte[] Riff(string formType) =>
        Encoding.ASCII.GetBytes("RIFF").Concat(BitConverter.GetBytes(64))
            .Concat(Encoding.ASCII.GetBytes(formType + "VP8 ")).Concat(new byte[52]).ToArray();

    [Fact]
    public async Task A_webp_photo_is_accepted()
    {
        var controller = BuildController();

        var result = await controller.UploadImage(
            FileFrom(Riff("WEBP"), "iphone.jpg", "image/jpeg"), CancellationToken.None);

        var dto = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AdminUploadsController.UploadedFileDto>().Subject;

        // Saved by what the bytes are, not by the name it arrived with.
        dto.Url.Should().EndWith(".webp");
    }

    /// <summary>RIFF also wraps audio and video; only the WEBP form type is an image.</summary>
    [Fact]
    public async Task Other_riff_files_are_refused()
    {
        var controller = BuildController();

        var act = async () => await controller.UploadImage(
            FileFrom(Riff("WAVE"), "song.webp", "image/webp"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        var controller = BuildController();

        var act = async () => await controller.UploadImage(
            FileFrom(Array.Empty<byte>(), "empty.png", "image/png"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task A_missing_file_is_refused()
    {
        var controller = BuildController();

        var act = async () => await controller.UploadImage(null, CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
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
}
