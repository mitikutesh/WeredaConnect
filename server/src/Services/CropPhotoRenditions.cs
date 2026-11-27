using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace WeredaConnect.Server.Services;

/// <summary>The two renditions of one crop photo, or why the original was refused.</summary>
public sealed record CropPhotoRenditionResult
{
    /// <summary>320 px WebP; null when refused.</summary>
    public byte[]? Thumb { get; init; }

    /// <summary>1024 px WebP; null when refused.</summary>
    public byte[]? Full { get; init; }

    /// <summary>Why the original was refused (for the log); null on success.</summary>
    public string? Refusal { get; init; }

    public bool IsRefused => Refusal != null;

    internal static CropPhotoRenditionResult Refused(string reason) => new() { Refusal = reason };
}

/// <summary>
/// The image work of the crop photo processor (crop-listing-api 1.6.0, Photo Renditions), with no
/// S3 in it so it is tested on its own:
/// 1. Reads only the header first and refuses an image over 40 megapixels or a format other than
///    JPEG, PNG or WebP (the decoder configuration knows no other format).
/// 2. Decodes the first frame, applies the EXIF orientation, then drops every metadata profile
///    (EXIF with GPS, ICC, XMP, IPTC) so the farm's location never reaches a buyer.
/// 3. Encodes a 320 px thumb (WebP q60) and a 1024 px full (WebP q75, re-encoded at q60 when over
///    150 KB), both lossy, fitted to the box and never upscaled.
/// </summary>
public static class CropPhotoRenditions
{
    public const int ThumbEdge = 320;
    public const int FullEdge = 1024;
    public const int ThumbQuality = 60;
    public const int FullQuality = 75;
    public const int FullFallbackQuality = 60;
    public const int FullMaxBytes = 150 * 1024;

    /// <summary>Largest decoded image (decompression bombs): 40 megapixels.</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>
    /// Only the three accepted formats are registered, so any other one (GIF, BMP, TIFF, ...) is
    /// an unknown format and never reaches its decoder.
    /// </summary>
    private static readonly Configuration DecoderConfiguration = new(
        new JpegConfigurationModule(),
        new PngConfigurationModule(),
        new WebpConfigurationModule());

    private static readonly DecoderOptions Decoder = new()
    {
        Configuration = DecoderConfiguration,
        // An animated WebP or PNG keeps its first frame only.
        MaxFrames = 1,
    };

    /// <summary>
    /// Build both renditions from an original's bytes. Never throws for bad input: a refused or
    /// undecodable image comes back with <see cref="CropPhotoRenditionResult.Refusal"/> set.
    /// </summary>
    public static CropPhotoRenditionResult Render(byte[] original)
    {
        if (original.Length == 0)
        {
            return CropPhotoRenditionResult.Refused("empty object");
        }

        try
        {
            ImageInfo info;
            using (var header = new MemoryStream(original, writable: false))
            {
                info = Image.Identify(Decoder, header);
            }

            if ((long)info.Width * info.Height > MaxPixels)
            {
                return CropPhotoRenditionResult.Refused($"{info.Width}x{info.Height} is over 40 megapixels");
            }

            using var input = new MemoryStream(original, writable: false);
            using var image = Image.Load<Rgba32>(Decoder, input);

            image.Mutate(x => x.AutoOrient());
            StripMetadata(image);

            var full = EncodeFitted(image, FullEdge, FullQuality);
            if (full.Length > FullMaxBytes)
            {
                full = EncodeFitted(image, FullEdge, FullFallbackQuality);
            }

            var thumb = EncodeFitted(image, ThumbEdge, ThumbQuality);
            return new CropPhotoRenditionResult { Thumb = thumb, Full = full };
        }
        catch (UnknownImageFormatException)
        {
            return CropPhotoRenditionResult.Refused("not a JPEG, PNG or WebP image");
        }
        catch (Exception ex)
        {
            // InvalidImageContentException and anything else a malformed file can raise in a
            // decoder: the original is refused, never retried.
            return CropPhotoRenditionResult.Refused($"decode failed [{ex.GetType().Name}]");
        }
    }

    /// <summary>The size that fits <paramref name="width"/> x <paramref name="height"/> in a square box, never larger.</summary>
    public static (int Width, int Height) FitWithin(int width, int height, int box)
    {
        var longest = Math.Max(width, height);
        if (longest <= box)
        {
            return (width, height);
        }

        var scale = (double)box / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Lossy WebP of the image fitted to the box, with no metadata written.</summary>
    public static byte[] EncodeFitted(Image<Rgba32> image, int box, int quality)
    {
        var (width, height) = FitWithin(image.Width, image.Height, box);
        var encoder = new WebpEncoder
        {
            FileFormat = WebpFileFormatType.Lossy,
            Quality = quality,
            SkipMetadata = true,
        };

        using var output = new MemoryStream();
        if (width == image.Width && height == image.Height)
        {
            image.Save(output, encoder);
        }
        else
        {
            using var resized = image.Clone(x => x.Resize(width, height));
            resized.Save(output, encoder);
        }

        return output.ToArray();
    }

    private static void StripMetadata(Image image)
    {
        image.Metadata.ExifProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IptcProfile = null;

        foreach (var frame in image.Frames)
        {
            frame.Metadata.ExifProfile = null;
            frame.Metadata.IccProfile = null;
            frame.Metadata.XmpProfile = null;
            frame.Metadata.IptcProfile = null;
        }
    }
}
