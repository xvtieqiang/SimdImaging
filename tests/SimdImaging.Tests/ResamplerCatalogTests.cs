using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// The resampler catalogue. Five members shipped first and their byte values are a persisted contract; the
/// eleven added on 2026-11-15 are appended so nothing that already stored a <see cref="Resampler"/> changes
/// meaning.
/// <para>
/// The kernels are checked by property rather than by golden pixels: a separable filter is fully described by
/// its support and its symmetry, and a wrong B/C pair shows up immediately as an asymmetric or truncated
/// kernel. Byte-level output is covered by the constant-image case, which fails if the weights do not sum to
/// one.
/// </para>
/// </summary>
public class ResamplerCatalogTests
{
    public static TheoryData<Resampler> All { get; } = new(Enum.GetValues<Resampler>());

    [Fact]
    public void Catalog_KeepsTheShippedFiveAtTheirByteValues()
    {
        var values = Enum.GetValues<Resampler>();

        values.Length.ShouldBe(16);
        ((byte)Resampler.Nearest).ShouldBe((byte)0);
        ((byte)Resampler.Box).ShouldBe((byte)1);
        ((byte)Resampler.Bilinear).ShouldBe((byte)2);
        ((byte)Resampler.Bicubic).ShouldBe((byte)3);
        ((byte)Resampler.Lanczos3).ShouldBe((byte)4);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Resize_PreservesAConstantImage(Resampler resampler)
    {
        using var source = Constant(64, 48, 200);

        // Downscale, upscale and the degenerate 1×1 case: the last one exercises the clamped tap budget.
        foreach (var (width, height) in new[] { (23, 17), (128, 96), (1, 1) })
        {
            using var resized = source.Resize(width, height, resampler);
            var span = resized.WritablePixels;
            for (var i = 0; i < span.Length; i++)
                span[i].ShouldBe((byte)200, $"{resampler} {width}x{height} at byte {i}");
        }
    }

    [Fact]
    public void Aliases_BuildTheSameWeightTable()
    {
        foreach (var (alias, original) in new[]
                 {
                     (Resampler.Triangle, Resampler.Bilinear),
                     (Resampler.CatmullRom, Resampler.Bicubic),
                 })
        {
            foreach (var (source, destination) in new[] { (97, 41), (41, 97), (64, 64) })
            {
                var a = WeightTable.Build(source, destination, alias);
                var b = WeightTable.Build(source, destination, original);

                a.Taps.ShouldBe(b.Taps, $"{alias} vs {original} taps");
                a.Starts.ShouldBe(b.Starts, $"{alias} vs {original} starts");
                a.Weights.ShouldBe(b.Weights, $"{alias} vs {original} weights");
            }
        }
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Kernel_IsSymmetric_AndVanishesBeyondItsSupport(Resampler resampler)
    {
        var (support, kernel) = ResamplerKernels.Get(resampler);

        kernel(0.0).ShouldBeGreaterThan(0.0, $"{resampler} must have mass at the centre");
        foreach (var x in new[] { 0.0, 0.25, 0.5, 1.0, 1.5 })
            kernel(x).ShouldBe(kernel(-x), $"{resampler} at {x}");

        // The declared support is what the weight table sizes its window from: a kernel with mass past it would
        // be silently truncated, which is how a "sharp" filter turns into a blurry one.
        kernel(support + 0.001).ShouldBe(0.0, $"{resampler} leaks past support {support}");
    }

    [Fact]
    public void Spline_IsTheCubicBSpline()
    {
        var (support, kernel) = ResamplerKernels.Get(Resampler.Spline);

        support.ShouldBe(2.0);
        kernel(0.0).ShouldBe(2.0 / 3.0);
        kernel(1.0).ShouldBe(1.0 / 6.0);
        kernel(2.0).ShouldBe(0.0);
    }

    [Fact]
    public void MitchellNetravali_IsTheBalancedCubic()
    {
        var (support, kernel) = ResamplerKernels.Get(Resampler.MitchellNetravali);

        support.ShouldBe(2.0);
        kernel(0.0).ShouldBe(8.0 / 9.0, 1e-15);
        kernel(1.0).ShouldBe(1.0 / 18.0, 1e-15);
    }

    [Fact]
    public void LanczosWindow_WidensWithTheOrder()
    {
        // 4× downscale: radius = support × 4, so the tap budget must grow strictly with a.
        var taps = new[] { Resampler.Lanczos2, Resampler.Lanczos3, Resampler.Lanczos5, Resampler.Lanczos8 }
            .Select(r => WeightTable.Build(256, 64, r).Taps)
            .ToArray();

        for (var i = 1; i < taps.Length; i++)
            taps[i].ShouldBeGreaterThan(taps[i - 1], $"Lanczos order {i}");
    }

    private static Image Constant(int width, int height, byte value)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        image.WritablePixels.Fill(value);
        return image;
    }
}
