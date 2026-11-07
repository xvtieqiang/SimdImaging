using Sdcb.SimdCodecs.Color.Icc;

namespace SimdImaging.Tests;

public class IccTests
{
    private static Image Ramp(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                span[o] = (byte)(x * 255 / (width - 1));
                span[o + 1] = (byte)(y * 255 / (height - 1));
                span[o + 2] = (byte)((x + y) * 255 / (width + height - 2));
                span[o + 3] = 255;
            }
        }

        return image;
    }

    [Fact]
    public void IccTransform_SRgbToSRgbSurvivesTheRoundTrip()
    {
        using var source = Ramp(32, 32);
        using var converted = source.IccTransform(IccProfiles.SRgbV4.Span);

        var before = source.WritablePixels;
        var after = converted.WritablePixels;
        var worst = 0;

        for (var i = 0; i < before.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
                worst = Math.Max(worst, Math.Abs(before[i + c] - after[i + c]));
        }

        worst.ShouldBeLessThanOrEqualTo(3, $"worst channel error {worst}");
    }

    [Fact]
    public void IccTransform_EmptyProfilesAreACopy()
    {
        using var source = Ramp(8, 8);
        using var converted = source.IccTransform(default, default);

        converted.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void IccTransform_RejectsNonRgbProfiles()
    {
        using var source = Ramp(4, 4);
        var notRgb = new byte[200];

        Should.Throw<NotSupportedException>(() => source.IccTransform(notRgb));
    }

    [Fact]
    public void IccTransform_RejectsTruncatedProfiles()
    {
        using var source = Ramp(4, 4);
        var truncated = new byte[10];

        Should.Throw<InvalidDataException>(() => source.IccTransform(truncated));
    }

    [Fact]
    public void IccTransform_RejectsAProfileWithoutMatrixTags()
    {
        using var source = Ramp(4, 4);
        var empty = new byte[200];
        "RGB "u8.CopyTo(empty.AsSpan(16, 4));
        // Tag table: zero entries, so no rXYZ/gXYZ/bXYZ or TRC.
        empty[131] = 0;

        Should.Throw<NotSupportedException>(() => source.IccTransform(empty));
    }
}
