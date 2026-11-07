namespace AuctionPlatform.Application.Common;

/// <summary>A picture made smaller: the bytes to store, and what they are.</summary>
public sealed record ShrunkImage(byte[] Bytes, string Extension, string ContentType, int Width, int Height);

/// <summary>
/// Makes an uploaded picture fit for phones. A photo straight from a camera or a design tool is
/// often 2-5 MB and 4000 px wide, while the app never draws one wider than a screen; every such
/// picture costs seconds on mobile data. This keeps what the eye sees and drops the rest.
/// </summary>
public interface IImageShrinker
{
    /// <summary>The longest side a stored picture keeps, in pixels: a full-screen picture on any phone.</summary>
    const int MaxSide = 1600;

    /// <summary>
    /// The picture, upright, at most <see cref="MaxSide"/> on its longest side, without camera
    /// metadata, as WebP. Null when that would not help: a picture already small (about 200 KB and
    /// within the size), not a picture this can read, an animation (which would lose its frames),
    /// or a result no smaller than what was given.
    /// </summary>
    ShrunkImage? Shrink(byte[] original);
}
