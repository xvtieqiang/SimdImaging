using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Tests;

/// <summary>
/// The contract every vector kernel lives under: the scalar twin is the reference, and the two must agree
/// exactly — not approximately — for every width, including the tails that the vector loop does not cover.
/// </summary>
public class RowMathTests
{
    [Fact]
    public void PremultipliedRgba8_VectorMatchesScalarExactly()
    {
        var random = new Random(20261105);

        for (var width = 1; width <= 65; width++)
        {
            var row = new byte[width * 4];
            random.NextBytes(row);

            var vector = new float[width * 4];
            var scalar = new float[width * 4];

            Pixels.ReadRowPremultipliedRgba8Vector(row, width, vector);
            Pixels.ReadRowPremultipliedScalar(row, width, WorkingFormat.Rgba8, scalar);

            vector.ShouldBe(scalar);
        }
    }

    [Fact]
    public void PremultipliedRgba8_AlphaLaneIsNotSquared()
    {
        var row = new byte[] { 10, 20, 30, 128 };
        var result = new float[4];

        Pixels.ReadRowPremultipliedRgba8Vector(row, 1, result);

        result[3].ShouldBe(128f / 255f);
        result[0].ShouldBe(10f / 255f * (128f / 255f));
    }

    [Fact]
    public void UnpremultipliedWriteRgba8_VectorMatchesScalarExactly()
    {
        var random = new Random(7717);

        for (var width = 1; width <= 33; width++)
        {
            var premultiplied = new float[width * 4];
            for (var x = 0; x < width; x++)
            {
                // Mix zero alpha, fully opaque and arbitrary partial alpha.
                var alpha = x % 7 == 0 ? 0f : (float)random.NextDouble();
                var o = x * 4;
                premultiplied[o] = (float)random.NextDouble() * alpha;
                premultiplied[o + 1] = (float)random.NextDouble() * alpha;
                premultiplied[o + 2] = (float)random.NextDouble() * alpha;
                premultiplied[o + 3] = alpha;
            }

            var vector = new byte[width * 4];
            var scalar = new byte[width * 4];

            Pixels.WriteRowUnpremultipliedRgba8Vector(premultiplied, width, vector);
            Pixels.WriteRowUnpremultipliedScalar(premultiplied, width, WorkingFormat.Rgba8, scalar);

            vector.ShouldBe(scalar);
        }
    }

    [Fact]
    public void UnpremultipliedWriteRgba8_ZeroAlphaWritesZeroColour()
    {
        var premultiplied = new float[] { 0f, 0f, 0f, 0f };
        var row = new byte[4];

        Pixels.WriteRowUnpremultipliedRgba8Vector(premultiplied, 1, row);

        row.ShouldBe(new byte[] { 0, 0, 0, 0 });
    }

    [Fact]
    public void Resize_VectorWriteMatchesScalarWriteEndToEnd()
    {
        using var source = TestImages.Gradient(97, 71, WorkingFormat.Rgba8);

        try
        {
            Pixels.ForceScalarWrite = true;
            using var scalar = source.Resize(31, 19, SimdImaging.Processing.Resampler.Lanczos3);

            Pixels.ForceScalarWrite = false;
            using var vector = source.Resize(31, 19, SimdImaging.Processing.Resampler.Lanczos3);

            vector.WritablePixels.ToArray().ShouldBe(scalar.WritablePixels.ToArray());
        }
        finally
        {
            Pixels.ForceScalarWrite = false;
        }
    }

    [Fact]
    public void Resize_SmokePattern_VectorWriteMatchesScalarWrite()
    {
        using var source = Image.Create(64, 48, WorkingFormat.Rgba8);
        var pixels = source.WritablePixels;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i * 37 + 11) & 0xFF);

        try
        {
            Pixels.ForceScalarWrite = true;
            using var scalar = source.Resize(97, 71, SimdImaging.Processing.Resampler.Lanczos3);

            Pixels.ForceScalarWrite = false;
            using var vector = source.Resize(97, 71, SimdImaging.Processing.Resampler.Lanczos3);

            var a = scalar.WritablePixels.ToArray();
            var b = vector.WritablePixels.ToArray();
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    throw new ShouldAssertException($"first difference at byte {i} (pixel {i / 4}, channel {i % 4}): scalar={a[i]} vector={b[i]}");
            }
        }
        finally
        {
            Pixels.ForceScalarWrite = false;
        }
    }
}
