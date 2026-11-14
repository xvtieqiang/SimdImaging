using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Color.Icc;
using SimdImaging.Processing;

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

    // ---------------------------------------------------------------------------------------------
    // LUT profiles. The fixtures are written byte by byte in SyntheticIccProfiles; every expected
    // value below is either exact by construction or computed from ColorMath, never from the parser.
    // ---------------------------------------------------------------------------------------------

    private static Image Grey16(ushort value)
    {
        var image = Image.Create(4, 3, WorkingFormat.Rgba16);
        var pixels = MemoryMarshal.Cast<byte, ushort>(image.WritablePixels);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 65535;
        }

        return image;
    }

    [Fact]
    public void IccTransform_AXyzLutProfileMatchesTheMatrixPathForTheSameColour()
    {
        // The fixture's CLUT is the identity in u1Fixed15: device (v,v,v) → PCS (v,v,v). Converting a
        // mid-grey through it must land where ColorMath says that PCS lands in sRGB. This is also the
        // test that catches a PCSXYZ decode divided by 65535 instead of 32768 — it would halve the light.
        using var source = Grey16(32768);
        using var converted = source.IccTransform(SyntheticIccProfiles.Lut16XyzIdentity());

        var (x65, y65, z65) = ColorMath.Adapt(0.5f, 0.5f, 0.5f, WhitePoint.D50, WhitePoint.D65);
        var (r, g, b) = ColorMath.FromXyz(x65, y65, z65);

        // Equal XYZ is not equal RGB, so each channel gets its own expected value.
        var expected = new[]
        {
            (ushort)Math.Round(Math.Clamp(r, 0f, 1f) * 65535f),
            (ushort)Math.Round(Math.Clamp(g, 0f, 1f) * 65535f),
            (ushort)Math.Round(Math.Clamp(b, 0f, 1f) * 65535f),
        };

        var pixels = MemoryMarshal.Cast<byte, ushort>(converted.WritablePixels);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                Math.Abs(pixels[i + channel] - expected[channel]).ShouldBeLessThanOrEqualTo(
                    1, $"channel {channel}: {pixels[i + channel]} vs {expected[channel]}");
            }
        }
    }

    [Fact]
    public void IccProfile_Lut16LabDecodesTheLegacyEncoding()
    {
        // lut16Type writes L* 100 as 0xFF00 and a* = 0 as 0x8000 (ICC.1:2010 NOTE 3 + §10.8), not the
        // general 0xFFFF / 0x8080 that mAB uses.
        var profile = IccProfile.Parse(SyntheticIccProfiles.Lut16LabConstant(0xFF00, 0x8000, 0x8000));
        var (x, y, z) = profile.ToXyz(0f, 0f, 0f);

        y.ShouldBe(1.0f, 1e-4f);
        x.ShouldBe(0.9642f, 2e-3f);
        z.ShouldBe(0.8249f, 2e-3f);
    }

    [Fact]
    public void IccProfile_MabDecodesTheGeneralEncoding()
    {
        // mAB writes L* 100 as 0xFFFF and a* = 0 as 0x8080.
        var profile = IccProfile.Parse(SyntheticIccProfiles.MabLabConstant(0xFFFF, 0x8080, 0x8080));
        var (x, y, z) = profile.ToXyz(0f, 0f, 0f);

        y.ShouldBe(1.0f, 1e-4f);
        x.ShouldBe(0.9642f, 2e-3f);
        z.ShouldBe(0.8249f, 2e-3f);
    }

    [Fact]
    public void IccProfile_Lut16MidGreyIsHalfOfFullScale()
    {
        // 0x8000 through the legacy encoding is L* = 50.196, which is the fixture's own arithmetic:
        // 32768 / 65280 * 100. A general-encoding decode would answer 50.0 instead.
        var profile = IccProfile.Parse(SyntheticIccProfiles.Lut16LabConstant(0x8000, 0x8000, 0x8000));
        var (x, y, z) = profile.ToXyz(0f, 0f, 0f);
        var (l, a, b) = ColorMath.XyzToLab(x, y, z, WhitePoint.D50);

        l.ShouldBe(50.196f, 0.01f);
        a.ShouldBe(0f, 0.01f);
        b.ShouldBe(0f, 0.01f);
    }

    [Fact]
    public void IccProfile_MabRunsCurvesThenClutThenMatrixThenCurves()
    {
        // Device 1.0 → A curve quarters it (0.25) → CLUT (identity onto XYZ) → 0.25 → matrix doubles X
        // → (0.5, 0.25, 0.25) → B curve identity → PCS. Any stage running in the wrong order or on the
        // wrong side of the CLUT changes these three numbers.
        var profile = IccProfile.Parse(SyntheticIccProfiles.MabWithEveryStage());
        var (x, y, z) = profile.ToXyz(1f, 1f, 1f);

        x.ShouldBe(0.5f, 2e-3f);
        y.ShouldBe(0.25f, 2e-3f);
        z.ShouldBe(0.25f, 2e-3f);
    }

    [Fact]
    public void IccTransform_AnMbaProfileDrivesTheDestinationDirection()
    {
        // The fixture's B2A0 is a constant CLUT answering every PCS with device (1.0, 0.5, 0.5).
        using var source = Grey16(32768);
        using var converted = source.IccTransform(default, SyntheticIccProfiles.MbaLabConstant(65535, 32768, 32768));

        var pixels = MemoryMarshal.Cast<byte, ushort>(converted.WritablePixels);
        pixels[0].ShouldBeInRange((ushort)65534, (ushort)65535);
        pixels[1].ShouldBeInRange((ushort)32767, (ushort)32768);
        pixels[2].ShouldBeInRange((ushort)32767, (ushort)32768);
    }

    [Fact]
    public void IccProfile_RefusesALut8ProfileWithAnXyzConnectionSpace()
    {
        Should.Throw<NotSupportedException>(
            () => IccProfile.Parse(SyntheticIccProfiles.Lut8WithXyzPcs()));
    }

    [Fact]
    public void IccProfile_RefusesANonRgbDeviceSpace()
    {
        Should.Throw<NotSupportedException>(() => IccProfile.Parse(SyntheticIccProfiles.Cmyk()));
    }

    [Fact]
    public void IccProfile_RefusesAProfileWithOnlyIntentOneTables()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => IccProfile.Parse(SyntheticIccProfiles.OnlyIntentOne()));

        // The message has to say what is missing, or the caller cannot tell a broken profile from an
        // unsupported one.
        exception.Message.ShouldContain("A2B0");
    }

    [Fact]
    public void IccProfile_RefusesAnUnknownLutTagType()
    {
        Should.Throw<NotSupportedException>(() => IccProfile.Parse(SyntheticIccProfiles.UnknownTagType()));
    }

    [Fact]
    public void IccProfile_RejectsATagThatPointsOutsideTheProfile()
    {
        Should.Throw<InvalidDataException>(() => IccProfile.Parse(SyntheticIccProfiles.TagOutsideProfile()));
    }
}
