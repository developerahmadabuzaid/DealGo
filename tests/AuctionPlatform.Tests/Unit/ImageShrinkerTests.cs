using AuctionPlatform.Application.Common;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace AuctionPlatform.Tests.Unit;

/// <summary>Uploaded pictures are made fit for phones: smaller, upright, without camera metadata.</summary>
public class ImageShrinkerTests
{
    private static readonly ImageShrinker Shrinker = new(NullLogger<ImageShrinker>.Instance);

    /// <summary>A photo-like picture (noise compresses badly, like a real photo), as a high-quality JPEG.</summary>
    internal static byte[] Photo(int width, int height, ushort? orientation = null)
    {
        using var image = new Image<Rgb24>(width, height);
        var random = new Random(7);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgb24((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)random.Next(256));
                }
            }
        });

        if (orientation is { } o)
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, o);
            image.Metadata.ExifProfile.SetValue(ExifTag.Model, "Camera that knows where you are");
        }

        using var output = new MemoryStream();
        image.SaveAsJpeg(output, new JpegEncoder { Quality = 95 });
        return output.ToArray();
    }

    [Fact]
    public void A_big_photo_becomes_a_small_webp_no_wider_than_a_phone_needs()
    {
        var original = Photo(3000, 2000);
        original.Length.Should().BeGreaterThan(1_000_000, "the test needs a really big picture");

        var shrunk = Shrinker.Shrink(original);

        shrunk.Should().NotBeNull();
        shrunk!.Extension.Should().Be(".webp");
        shrunk.ContentType.Should().Be("image/webp");
        (shrunk.Width, shrunk.Height).Should().Be((1600, 1067));
        shrunk.Bytes.Length.Should().BeLessThan(original.Length / 3);

        using var reread = Image.Load(shrunk.Bytes);
        (reread.Width, reread.Height).Should().Be((1600, 1067));
    }

    [Fact]
    public void A_sideways_phone_photo_is_turned_upright_and_loses_its_camera_details()
    {
        // Orientation 6: stored landscape, to be shown rotated 90 degrees, i.e. portrait.
        var original = Photo(2400, 1600, orientation: 6);

        var shrunk = Shrinker.Shrink(original);

        shrunk.Should().NotBeNull();
        (shrunk!.Width, shrunk.Height).Should().Be((1067, 1600));

        using var reread = Image.Load(shrunk.Bytes);
        reread.Metadata.ExifProfile.Should().BeNull();
    }

    [Fact]
    public void A_picture_already_small_is_kept_as_it_was()
    {
        Shrinker.Shrink(Photo(300, 300)).Should().BeNull();
    }

    [Fact]
    public void An_animation_is_kept_as_it_was()
    {
        using var animation = new Image<Rgba32>(1800, 1800);
        animation.Frames.AddFrame(animation.Frames.RootFrame);
        using var output = new MemoryStream();
        animation.SaveAsGif(output, new GifEncoder());

        Shrinker.Shrink(output.ToArray()).Should().BeNull();
    }

    [Fact]
    public void Something_that_is_not_a_picture_is_kept_as_it_was()
    {
        Shrinker.Shrink("<html>not a picture</html>"u8.ToArray()).Should().BeNull();
        Shrinker.Shrink([]).Should().BeNull();
    }

    [Fact]
    public void The_largest_side_kept_is_1600()
    {
        IImageShrinker.MaxSide.Should().Be(1600);
    }
}
