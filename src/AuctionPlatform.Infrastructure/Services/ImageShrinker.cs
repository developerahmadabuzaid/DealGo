using AuctionPlatform.Application.Common;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// <see cref="IImageShrinker"/> with ImageSharp (pure .NET, so it runs on SmarterASP's IIS and in a
/// container alike, with no native libraries to install).
/// </summary>
public sealed class ImageShrinker : IImageShrinker
{
    /// <summary>Lossy WebP at this quality is indistinguishable from the original at phone sizes.</summary>
    private const int Quality = 80;

    /// <summary>A picture this size or under, and within <see cref="IImageShrinker.MaxSide"/>, is fine as it is.</summary>
    public const int SmallEnough = 200 * 1024;

    /// <summary>A result must save at least this much to be worth a new file.</summary>
    private const double MinimumSaving = 0.10;

    private readonly ILogger<ImageShrinker> _logger;

    public ImageShrinker(ILogger<ImageShrinker> logger) => _logger = logger;

    public ShrunkImage? Shrink(byte[] original)
    {
        if (original.Length == 0) return null;

        try
        {
            using var image = Image.Load(original);

            // An animated GIF or WebP would keep only its first frame.
            if (image.Frames.Count > 1) return null;

            var resized = image.Width > IImageShrinker.MaxSide || image.Height > IImageShrinker.MaxSide;

            // Already small: kept as sent, in its own format (a PNG logo keeps its sharp edges).
            if (!resized && original.Length <= SmallEnough) return null;

            image.Mutate(x =>
            {
                // Phone photos are stored sideways with a flag saying which way is up; the flag is
                // dropped with the metadata below, so the pixels are turned first.
                x.AutoOrient();
                if (resized)
                {
                    x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(IImageShrinker.MaxSide, IImageShrinker.MaxSide),
                        Sampler = KnownResamplers.Lanczos3
                    });
                }
            });

            // Camera details, GPS position and colour profiles are of no use to a shop picture.
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.IccProfile = null;

            using var output = new MemoryStream();
            image.SaveAsWebp(output, new WebpEncoder
            {
                FileFormat = WebpFileFormatType.Lossy,
                Quality = Quality,
                Method = WebpEncodingMethod.Level4
            });

            var bytes = output.ToArray();
            if (!resized && bytes.Length > original.Length * (1 - MinimumSaving)) return null;

            return new ShrunkImage(bytes, ".webp", "image/webp", image.Width, image.Height);
        }
        // ImageFormatException covers unknown formats and damaged content alike: a file the upload
        // accepted by its first bytes can still be one this cannot decode. It is stored as sent.
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            _logger.LogInformation(ex, "A picture could not be read for shrinking; it is kept as it is.");
            return null;
        }
    }
}
