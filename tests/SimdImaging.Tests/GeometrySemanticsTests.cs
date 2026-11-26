using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Geometry;

namespace SimdImaging.Tests;

/// <summary>
/// The geometry steps the P0 audit named but did not pixel-test: Tile, ResizeCanvas, RoundCorners, Trim and
/// EntropyCrop. Each assertion ties the declaration to a pixel-level consequence, not to another declaration.
/// </summary>
public sealed class GeometrySemanticsTests
{
    [Fact]
    public void Tile_IsAnExactPermutation()
    {
        using var source = Image.Create(2, 2, WorkingFormat.Rgba8);
        var pixels = source.WritablePixels;
        for (var i = 0; i < 4; i++)
        {
            pixels[(i * 4)] = (byte)(10 + i);
            pixels[(i * 4) + 1] = (byte)(20 + i);
            pixels[(i * 4) + 2] = (byte)(30 + i);
            pixels[(i * 4) + 3] = 200;
        }

        var descriptor = GeometrySteps.Tile(4, 4, mirror: false);
        using var tiled = descriptor.Reshape(source[0].Pixels.View, null);

        descriptor.Requirements.Partition.ShouldBe(Partitioning.WholeImage);
        descriptor.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        descriptor.PixelSemantics.Precision.ShouldBe(Precision.IntegerExact);

        tiled.Width.ShouldBe(4);
        tiled.Height.ShouldBe(4);
        tiled.View.Span[0].ShouldBe((byte)10);
        tiled.View.Span[(3 * 4) + 3].ShouldBe((byte)200, "the source alpha is copied, never recomputed");
    }

    [Fact]
    public void ResizeCanvas_FillsTheBackgroundAndKeepsTheSourceAlpha()
    {
        using var source = Image.Create(2, 2, WorkingFormat.Rgba8);
        var pixels = source.WritablePixels;
        for (var i = 0; i < 4; i++)
            pixels[(i * 4) + 3] = 200;

        var background = new RgbaColor(10, 20, 30, 40);
        var descriptor = GeometrySteps.ResizeCanvas(4, 4, Anchor.Centre, background);
        using var canvas = descriptor.Reshape(source[0].Pixels.View, null);

        descriptor.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        descriptor.PixelSemantics.Precision.ShouldBe(Precision.IntegerExact);

        // Centre anchor puts a 2x2 source at (1,1) on a 4x4 canvas.
        canvas.View.Span[0].ShouldBe((byte)10);
        canvas.View.Span[3].ShouldBe((byte)40, "padding writes the background alpha");
        canvas.View.Span[((1 * 4) + 1) * 4 + 3].ShouldBe((byte)200, "the source pixels keep their alpha");
    }

    [Fact]
    public void RoundCorners_ChangesTheCornerAlpha()
    {
        using var source = Image.Create(16, 16, WorkingFormat.Rgba8);
        source.Fill(new RgbaColor(255, 255, 255, 255));

        var descriptor = GeometrySteps.RoundCorners(6f);
        using var rounded = descriptor.Reshape(source[0].Pixels.View, null);

        descriptor.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Recomputes);
        descriptor.PixelSemantics.Precision.ShouldBe(Precision.SingleRoundFloat);

        var span = rounded.View.Span;
        span[3].ShouldBeLessThan((byte)255);
        span[((8 * 16) + 8) * 4 + 3].ShouldBe((byte)255);
    }

    [Fact]
    public void Trim_KeepsTheSurvivingPixelsUnchanged()
    {
        using var source = Image.Create(8, 8, WorkingFormat.Rgba8);
        var pixels = source.WritablePixels;
        for (var y = 2; y < 6; y++)
        {
            for (var x = 2; x < 6; x++)
            {
                var o = ((y * 8) + x) * 4;
                pixels[o] = 90;
                pixels[o + 1] = 110;
                pixels[o + 2] = 130;
                pixels[o + 3] = 200;
            }
        }

        var descriptor = GeometrySteps.Trim(0.1f);
        using var trimmed = descriptor.Reshape(source[0].Pixels.View, null);

        descriptor.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        descriptor.PixelSemantics.Precision.ShouldBe(Precision.IntegerExact);
        trimmed.Width.ShouldBe(4);
        trimmed.Height.ShouldBe(4);
        trimmed.View.Span[3].ShouldBe((byte)200);
        trimmed.View.Span[0].ShouldBe((byte)90);
    }

    [Fact]
    public void EntropyCrop_KeepsTheSurvivingPixelsUnchanged()
    {
        using var source = Image.Create(12, 12, WorkingFormat.Rgba8);
        var pixels = source.WritablePixels;

        // A flat grey border and a 4x4 interior whose pixels vary in luma and alpha. Every interior row and
        // column then has more than one level, so only the border is below the entropy cut.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 90;
            pixels[i + 1] = 90;
            pixels[i + 2] = 90;
            pixels[i + 3] = 255;
        }

        for (var y = 4; y < 8; y++)
        {
            for (var x = 4; x < 8; x++)
            {
                var o = ((y * 12) + x) * 4;
                var k = ((y - 4) * 4) + (x - 4);
                pixels[o] = (byte)(10 + (k * 15));
                pixels[o + 1] = (byte)(240 - (k * 15));
                pixels[o + 2] = (byte)(20 + (k * 12));
                pixels[o + 3] = (byte)(10 + (k * 15));
            }
        }

        var descriptor = GeometrySteps.EntropyCrop(0.25f);
        using var cropped = descriptor.Reshape(source[0].Pixels.View, null);

        descriptor.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        descriptor.PixelSemantics.Precision.ShouldBe(Precision.IntegerExact);
        cropped.Width.ShouldBe(4);
        cropped.Height.ShouldBe(4);

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var expected = (((y + 4) * 12) + (x + 4)) * 4;
                var actual = ((y * 4) + x) * 4;
                cropped.View.Span.Slice(actual, 4).ToArray()
                    .ShouldBe(source[0].Pixels.View.Span.Slice(expected, 4).ToArray(), $"pixel ({x},{y})");
            }
        }
    }

    [Fact]
    public void EntropyCrop_DeclaresAnIntegerPreservingContract()
    {
        var descriptor = GeometrySteps.EntropyCrop(0.5f);

        descriptor.Requirements.Partition.ShouldBe(Partitioning.WholeImage);
        descriptor.PixelSemantics.Alpha.ShouldBe(AlphaPolicy.Preserve);
        descriptor.PixelSemantics.Precision.ShouldBe(Precision.IntegerExact);
        descriptor.WorkspaceModel.IsDeclared.ShouldBeTrue();
        descriptor.WorkspaceModel.Scope.ShouldBe(WorkspaceScope.WholeImage,
            "the column histograms are held once, not per worker");
        descriptor.WorkspaceModel.Estimate!(64, 48, 48, WorkingFormat.Rgba8)
            .ShouldBeGreaterThan(1024L * 64, "the per-column histograms dominate and must be declared");
    }
}
