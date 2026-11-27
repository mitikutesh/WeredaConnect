using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// crop-listing-api 1.6.0, Photo Renditions: what the processor keeps of an original (sizes,
/// format, orientation, metadata) and what it refuses.
/// </summary>
public class CropPhotoRenditionsTests
{
    /// <summary>A JPEG of the given size with a gradient, so it has real content to encode.</summary>
    internal static byte[] Jpeg(int width, int height, Action<Image<Rgba32>>? edit = null)
    {
        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32((byte)(x * 255 / width), (byte)(y * 255 / height), 90);
                }
            }
        });
        edit?.Invoke(image);
        using var output = new MemoryStream();
        image.Save(output, new JpegEncoder { Quality = 90 });
        return output.ToArray();
    }

    private static Image Decode(byte[] bytes) => Image.Load(bytes);

    [Fact]
    public void Render_LargePhoto_FitsBothBoxesAsWebp()
    {
        var result = CropPhotoRenditions.Render(Jpeg(2000, 1500));

        result.IsRefused.Should().BeFalse();
        using var full = Decode(result.Full!);
        using var thumb = Decode(result.Thumb!);
        full.Metadata.DecodedImageFormat.Should().Be(WebpFormat.Instance);
        thumb.Metadata.DecodedImageFormat.Should().Be(WebpFormat.Instance);
        (full.Width, full.Height).Should().Be((1024, 768));
        (thumb.Width, thumb.Height).Should().Be((320, 240));
        result.Full!.Length.Should().BeLessThanOrEqualTo(CropPhotoRenditions.FullMaxBytes);
        result.Thumb!.Length.Should().BeLessThan(result.Full.Length);
    }

    [Fact]
    public void Render_SmallPhoto_IsNeverUpscaled()
    {
        var result = CropPhotoRenditions.Render(Jpeg(200, 100));

        using var full = Decode(result.Full!);
        using var thumb = Decode(result.Thumb!);
        (full.Width, full.Height).Should().Be((200, 100));
        (thumb.Width, thumb.Height).Should().Be((200, 100));
    }

    [Fact]
    public void Render_ExifRotated_IsTurnedUpright()
    {
        // Orientation 6: the camera held upright, the pixels stored sideways.
        var original = Jpeg(400, 200, image =>
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        });

        var result = CropPhotoRenditions.Render(original);

        using var full = Decode(result.Full!);
        (full.Width, full.Height).Should().Be((200, 400));
    }

    [Fact]
    public void Render_StripsGpsAndEveryOtherProfile()
    {
        var original = Jpeg(600, 400, image =>
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.GPSLatitudeRef, "N");
            image.Metadata.ExifProfile.SetValue(
                ExifTag.GPSLatitude, new[] { new Rational(9, 1), new Rational(1, 1), new Rational(0, 1) });
            image.Metadata.ExifProfile.SetValue(ExifTag.Make, "TestPhone");
        });

        var result = CropPhotoRenditions.Render(original);

        foreach (var rendition in new[] { result.Full!, result.Thumb! })
        {
            using var decoded = Decode(rendition);
            decoded.Metadata.ExifProfile.Should().BeNull();
            decoded.Metadata.IccProfile.Should().BeNull();
            decoded.Metadata.XmpProfile.Should().BeNull();
        }
    }

    [Fact]
    public void Render_PngOriginal_IsAccepted()
    {
        using var image = new Image<Rgba32>(500, 500, new Rgba32(30, 120, 40));
        using var output = new MemoryStream();
        image.Save(output, new PngEncoder());

        var result = CropPhotoRenditions.Render(output.ToArray());

        result.IsRefused.Should().BeFalse();
        using var thumb = Decode(result.Thumb!);
        (thumb.Width, thumb.Height).Should().Be((320, 320));
    }

    [Fact]
    public void Render_Gif_IsRefusedAsAnUnknownFormat()
    {
        using var image = new Image<Rgba32>(50, 50);
        using var output = new MemoryStream();
        image.Save(output, new GifEncoder());

        var result = CropPhotoRenditions.Render(output.ToArray());

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("not a JPEG, PNG or WebP");
        result.Full.Should().BeNull();
        result.Thumb.Should().BeNull();
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 })] // a JPEG that stops
    public void Render_EmptyGarbageOrTruncated_IsRefused(byte[] original)
    {
        var result = CropPhotoRenditions.Render(original);

        result.IsRefused.Should().BeTrue();
        result.Full.Should().BeNull();
    }

    [Fact]
    public void Render_HeaderOver40Megapixels_IsRefusedBeforeDecoding()
    {
        // A real 10x10 PNG whose header claims 10000 x 5000 (50 MP): refused from the header
        // alone, so no 200 MB pixel buffer is ever allocated.
        using var image = new Image<Rgba32>(10, 10);
        using var output = new MemoryStream();
        image.Save(output, new PngEncoder());
        var png = PatchPngSize(output.ToArray(), 10_000, 5_000);

        var result = CropPhotoRenditions.Render(png);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("40 megapixels");
    }

    [Theory]
    [InlineData(4000, 3000, 1024, 1024, 768)]
    [InlineData(3000, 4000, 1024, 768, 1024)]
    [InlineData(1024, 1024, 1024, 1024, 1024)]
    [InlineData(800, 600, 1024, 800, 600)]
    [InlineData(5000, 10, 320, 320, 1)]
    public void FitWithin_KeepsTheAspectAndNeverGrows(int width, int height, int box, int expectedWidth, int expectedHeight)
    {
        CropPhotoRenditions.FitWithin(width, height, box).Should().Be((expectedWidth, expectedHeight));
    }

    /// <summary>Rewrite a PNG's IHDR width and height and fix the chunk's CRC.</summary>
    private static byte[] PatchPngSize(byte[] png, int width, int height)
    {
        // Signature (8) + length (4) + "IHDR" (4), then width and height (4 each, big-endian).
        const int ihdrType = 12;
        const int ihdrData = 16;
        WriteBigEndian(png, ihdrData, width);
        WriteBigEndian(png, ihdrData + 4, height);
        var crc = Crc32(png.AsSpan(ihdrType, 4 + 13));
        WriteBigEndian(png, ihdrData + 13, unchecked((int)crc));
        return png;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
