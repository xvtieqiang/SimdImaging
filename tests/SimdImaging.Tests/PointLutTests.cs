using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// The byte-domain LUT path must reproduce the float scalar twin exactly — same rule as every vector
/// kernel in this repository. Comparison is over raw bytes, not "visually close".
/// </summary>
public class PointLutTests
{
    [Fact]
    public void Rgba8_LutMatchesScalarTwinExactly()
    {
        var random = new Random(20261106);
        const int width = 37, height = 11;
        var data = new byte[width * height * 4];
        random.NextBytes(data);

        using var image = Image.Create(width, height, WorkingFormat.Rgba8);
        data.CopyTo(image.WritablePixels);
        var view = image[0].Pixels.View;

        var cases = new (string Name, Func<PixelView, WorkingBuffer> Run)[]
        {
            ("Invert", v => PointOps.Invert(v)),
            ("Brightness", v => PointOps.Brightness(v, 0.23f)),
            ("Lightness", v => PointOps.Lightness(v, 1.3f)),
            ("Contrast", v => PointOps.Contrast(v, 1.4f)),
            ("Gamma", v => PointOps.Gamma(v, 2.2f)),
            ("Posterize", v => PointOps.Posterize(v, 5)),
            ("Levels", v => PointOps.Levels(v, 0.1f, 0.9f, 1.2f)),
            ("Curves", v => PointOps.Curves(v, SCurvePoints())),
            ("Clamp", v => PointOps.Clamp(v, 0.2f, 0.8f)),
            ("AlphaThreshold", v => PointOps.AlphaThreshold(v, 0.4f)),
            ("Temperature", v => PointOps.Temperature(v, 0.3f)),
            ("Tint", v => PointOps.Tint(v, new RgbaColor(20, 200, 90, 255), 0.4f)),
            ("Opacity", v => PointOps.Opacity(v, 0.6f)),
            ("Grayscale", v => PointOps.Grayscale(v)),
            ("Grayscale709", v => PointOps.Grayscale(v, bt709: true)),
            ("Sepia", v => PointOps.Sepia(v)),
            ("HueRotate", v => PointOps.HueRotate(v, 37f)),
            ("ColorMatrix", v => PointOps.ColorMatrix(v,
                [0.9f, 0.1f, 0f, 0.02f, 0f, 1.1f, -0.1f, 0f, 0.05f, 0f, 0.8f, -0.01f])),
        };

        try
        {
            foreach (var (name, run) in cases)
            {
                PointOps.ForceScalarPoint = true;
                using var expected = run(view);
                PointOps.ForceScalarPoint = false;
                using var actual = run(view);

                actual.View.Span.SequenceEqual(expected.View.Span)
                    .ShouldBeTrue($"{name}: LUT path must be byte-identical to the scalar twin");
            }
        }
        finally
        {
            PointOps.ForceScalarPoint = false;
        }
    }

    /// <summary>An asymmetric S curve — uneven point spacing keeps the interpolation paths honest.</summary>
    private static CurvePoint[] SCurvePoints() =>
    [
        new(0f, 0f),
        new(0.2f, 0.14f),
        new(0.5f, 0.5f),
        new(0.78f, 0.87f),
        new(1f, 1f),
    ];

    [Fact]
    public void MatrixVectorPack_MatchesScalarTwinExactly()
    {
        // 37 pixels per row is not a multiple of 4, so the tail of the pack loop is exercised too.
        var random = new Random(90210);
        const int width = 37, height = 11;
        var data = new byte[width * height * 4];
        random.NextBytes(data);

        using var image = Image.Create(width, height, WorkingFormat.Rgba8);
        data.CopyTo(image.WritablePixels);
        var view = image[0].Pixels.View;

        var cases = new (string Name, Func<PixelView, WorkingBuffer> Run)[]
        {
            ("Sepia", v => PointOps.Sepia(v)),
            ("HueRotate", v => PointOps.HueRotate(v, 63f)),
            ("ColorMatrix", v => PointOps.ColorMatrix(v,
                [0.9f, 0.1f, 0f, 0.02f, 0f, 1.1f, -0.1f, 0f, 0.05f, 0f, 0.8f, -0.01f])),
        };

        try
        {
            foreach (var (name, run) in cases)
            {
                PointLut.ForceScalarMatrixPack = true;
                byte[] expected;
                using (var scalar = run(view))
                    expected = scalar.View.Span.ToArray();

                PointLut.ForceScalarMatrixPack = false;
                byte[] actual;
                using (var vector = run(view))
                    actual = vector.View.Span.ToArray();

                actual.AsSpan().SequenceEqual(expected)
                    .ShouldBeTrue($"{name}: vector clamp/pack must be byte-identical to scalar ToByte");
            }
        }
        finally
        {
            PointLut.ForceScalarMatrixPack = false;
        }
    }
}
