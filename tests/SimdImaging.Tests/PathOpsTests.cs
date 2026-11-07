using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class PathOpsTests
{
    private static Image Canvas(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        image.Fill(new RgbaColor(0, 0, 0, 255));
        return image;
    }

    private static (byte R, byte G, byte B) At(Image image, int x, int y)
    {
        var o = (y * image.Width + x) * 4;
        var span = image.WritablePixels;
        return (span[o], span[o + 1], span[o + 2]);
    }

    private static bool IsWhite(Image image, int x, int y) => At(image, x, y) == ((byte)255, (byte)255, (byte)255);

    private static byte AlphaAt(Image image, int x, int y) => image.WritablePixels[(y * image.Width + x) * 4 + 3];

    [Fact]
    public void FillPath_RectangleMatchesFillRect()
    {
        var rect = new PixelRect(2, 1, 5, 4);
        using var viaPath = Canvas(10, 8);
        var builder = new PixelPathBuilder();
        builder.AddRectangle(rect);
        viaPath.FillPath(builder.Build(), RgbaColor.White);

        using var viaRect = Canvas(10, 8);
        viaRect.FillRect(rect, RgbaColor.White);

        viaPath.WritablePixels.ToArray().ShouldBe(viaRect.WritablePixels.ToArray());
    }

    [Fact]
    public void FillPath_EllipseLeavesTheCornersEmpty()
    {
        using var image = Canvas(21, 21);
        var builder = new PixelPathBuilder();
        builder.AddEllipse(10.5f, 10.5f, 8f, 8f);
        image.FillPath(builder.Build(), RgbaColor.White);

        IsWhite(image, 10, 10).ShouldBeTrue();
        IsWhite(image, 3, 10).ShouldBeTrue();
        IsWhite(image, 10, 3).ShouldBeTrue();
        IsWhite(image, 0, 0).ShouldBeFalse();
        IsWhite(image, 20, 20).ShouldBeFalse();
        // Just outside the circle on the diagonal: 10.5 - 8/√2 ≈ 4.84.
        IsWhite(image, 3, 3).ShouldBeFalse();
        IsWhite(image, 5, 5).ShouldBeTrue();
    }

    [Fact]
    public void FillPath_EvenOddPunchesAHoleThatNonZeroDoesNot()
    {
        var builder = new PixelPathBuilder();
        builder.AddRectangle(new PixelRect(1, 1, 8, 8));
        builder.AddRectangle(new PixelRect(3, 3, 4, 4));
        var path = builder.Build();

        using var nonZero = Canvas(10, 10);
        nonZero.FillPath(path, RgbaColor.White, PathFillRule.NonZero);
        IsWhite(nonZero, 4, 4).ShouldBeTrue();
        IsWhite(nonZero, 1, 1).ShouldBeTrue();

        using var evenOdd = Canvas(10, 10);
        evenOdd.FillPath(path, RgbaColor.White, PathFillRule.EvenOdd);
        IsWhite(evenOdd, 4, 4).ShouldBeFalse();
        IsWhite(evenOdd, 1, 1).ShouldBeTrue();
    }

    [Fact]
    public void FillPath_ReversedInnerContourCancelsUnderNonZero()
    {
        // Outer contour one way, inner the other: non-zero cancels the interior, which is the classic way a
        // hole is expressed.
        var builder = new PixelPathBuilder();
        builder.MoveTo(1, 1);
        builder.LineTo(1, 8);
        builder.LineTo(8, 8);
        builder.LineTo(8, 1);
        builder.Close();
        builder.MoveTo(3, 3);
        builder.LineTo(6, 3);
        builder.LineTo(6, 6);
        builder.LineTo(3, 6);
        builder.Close();

        using var image = Canvas(10, 10);
        image.FillPath(builder.Build(), RgbaColor.White, PathFillRule.NonZero);

        IsWhite(image, 4, 4).ShouldBeFalse();
        IsWhite(image, 1, 1).ShouldBeTrue();
        IsWhite(image, 7, 7).ShouldBeTrue();
    }

    [Fact]
    public void FillPath_OpenSubPathIsClosedImplicitly()
    {
        var builder = new PixelPathBuilder();
        builder.MoveTo(2, 2);
        builder.LineTo(8, 2);
        builder.LineTo(8, 8);

        using var image = Canvas(12, 12);
        image.FillPath(builder.Build(), RgbaColor.White);

        IsWhite(image, 7, 3).ShouldBeTrue();
        IsWhite(image, 3, 7).ShouldBeFalse();
    }

    [Fact]
    public void FillPath_OutsideTheCanvasIsANoOp()
    {
        var builder = new PixelPathBuilder();
        builder.AddRectangle(new PixelRect(50, 50, 10, 10));

        using var image = Canvas(8, 8);
        image.FillPath(builder.Build(), RgbaColor.White);

        IsWhite(image, 0, 0).ShouldBeFalse();
        IsWhite(image, 7, 7).ShouldBeFalse();
    }

    [Fact]
    public void FillPath_EmptyPathIsANoOp()
    {
        using var image = Canvas(4, 4);
        image.FillPath(PixelPath.Empty, RgbaColor.White);

        IsWhite(image, 0, 0).ShouldBeFalse();
    }

    [Fact]
    public void Stroke_HorizontalLineCoversItsWidth()
    {
        var builder = new PixelPathBuilder();
        builder.MoveTo(2f, 4.5f);
        builder.LineTo(8f, 4.5f);

        using var image = Canvas(12, 9);
        image.DrawPath(builder.Build(), RgbaColor.White, new StrokeOptions(width: 3f));

        IsWhite(image, 5, 3).ShouldBeTrue();
        IsWhite(image, 5, 4).ShouldBeTrue();
        IsWhite(image, 5, 5).ShouldBeTrue();
        IsWhite(image, 5, 2).ShouldBeFalse();
        IsWhite(image, 5, 6).ShouldBeFalse();
    }

    [Fact]
    public void Stroke_SquareCapExtendsPastTheEndpoint()
    {
        var builder = new PixelPathBuilder();
        builder.MoveTo(2f, 4.5f);
        builder.LineTo(8f, 4.5f);
        var path = builder.Build();

        using var butt = Canvas(12, 9);
        butt.DrawPath(path, RgbaColor.White, new StrokeOptions(width: 3f, cap: LineCap.Butt));
        IsWhite(butt, 1, 4).ShouldBeFalse();
        IsWhite(butt, 2, 4).ShouldBeTrue();
        IsWhite(butt, 7, 4).ShouldBeTrue();
        IsWhite(butt, 8, 4).ShouldBeFalse();

        using var square = Canvas(12, 9);
        square.DrawPath(path, RgbaColor.White, new StrokeOptions(width: 3f, cap: LineCap.Square));
        IsWhite(square, 1, 4).ShouldBeTrue();
        IsWhite(square, 8, 4).ShouldBeTrue();
    }

    [Fact]
    public void Stroke_DashesLeaveGaps()
    {
        var builder = new PixelPathBuilder();
        builder.MoveTo(0.5f, 4.5f);
        builder.LineTo(19.5f, 4.5f);

        using var image = Canvas(20, 9);
        image.DrawPath(builder.Build(), RgbaColor.White,
            new StrokeOptions(width: 2f, dashPattern: new float[] { 4f, 4f }));

        IsWhite(image, 2, 4).ShouldBeTrue();
        IsWhite(image, 6, 4).ShouldBeFalse();
        IsWhite(image, 10, 4).ShouldBeTrue();
        IsWhite(image, 14, 4).ShouldBeFalse();
    }

    [Fact]
    public void Stroke_RoundJoinFillsTheCorner()
    {
        var builder = new PixelPathBuilder();
        builder.MoveTo(2f, 2f);
        builder.LineTo(2f, 8f);
        builder.LineTo(8f, 8f);

        using var image = Canvas(12, 12);
        image.DrawPath(builder.Build(), RgbaColor.White,
            new StrokeOptions(width: 4f, join: LineJoin.Round));

        // The inside of the corner is covered by the segments; the outside needs the join. (1,8) sits
        // outside both rectangles and fully inside the round join's quarter disc.
        IsWhite(image, 0, 5).ShouldBeTrue();
        IsWhite(image, 5, 9).ShouldBeTrue();
        IsWhite(image, 1, 8).ShouldBeTrue();
        IsWhite(image, 0, 10).ShouldBeFalse();
    }

    [Fact]
    public void Stroke_MiterJoinFillsTheCornerThatBevelLeavesEmpty()
    {
        var builder = new PixelPathBuilder();
        builder.MoveTo(2f, 2f);
        builder.LineTo(2f, 8f);
        builder.LineTo(8f, 8f);
        var path = builder.Build();

        using var miter = Canvas(12, 12);
        miter.DrawPath(path, RgbaColor.White, new StrokeOptions(width: 4f, join: LineJoin.Miter));
        IsWhite(miter, 0, 9).ShouldBeTrue();

        using var bevel = Canvas(12, 12);
        bevel.DrawPath(path, RgbaColor.White, new StrokeOptions(width: 4f, join: LineJoin.Bevel));
        IsWhite(bevel, 0, 9).ShouldBeFalse();
    }

    [Fact]
    public void Stroke_ClosedContourHasNoCaps()
    {
        var builder = new PixelPathBuilder();
        builder.AddRectangle(new PixelRect(2, 2, 6, 6));

        using var image = Canvas(10, 10);
        image.DrawPath(builder.Build(), RgbaColor.White, new StrokeOptions(width: 2f));

        IsWhite(image, 2, 2).ShouldBeTrue();
        IsWhite(image, 5, 5).ShouldBeFalse();
        IsWhite(image, 0, 0).ShouldBeFalse();
    }

    [Fact]
    public void FillPath_AntialiasesTheDiagonalEdge()
    {
        var builder = new PixelPathBuilder();
        builder.MoveTo(0, 0);
        builder.LineTo(10, 0);
        builder.LineTo(10, 10);

        using var image = Canvas(12, 12);
        image.FillPath(builder.Build(), RgbaColor.White);

        var partial = 0;
        for (var y = 0; y < 12; y++)
        {
            for (var x = 0; x < 12; x++)
            {
                var value = At(image, x, y).R;
                if (value is > 0 and < 255)
                    partial++;
            }
        }

        // A 45° hypotenuse cannot be represented by whole pixels, so coverage has to land in between.
        partial.ShouldBeGreaterThan(0);
        IsWhite(image, 9, 2).ShouldBeTrue();
        IsWhite(image, 2, 9).ShouldBeFalse();
    }

    [Fact]
    public void ClipPath_KeepsTheInsideAndFeathersTheRim()
    {
        using var image = Image.Create(8, 8, WorkingFormat.Rgba8);
        image.Fill(new RgbaColor(200, 100, 50, 255));

        var builder = new PixelPathBuilder();
        builder.AddEllipse(4, 4, 3, 3);
        image.ClipPath(builder.Build());

        AlphaAt(image, 4, 4).ShouldBe((byte)255);
        AlphaAt(image, 0, 0).ShouldBe((byte)0);

        var partial = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var alpha = AlphaAt(image, x, y);
                if (alpha is > 0 and < 255)
                    partial++;
            }
        }

        partial.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ClipPath_EmptyPathRemovesEverything()
    {
        using var image = Image.Create(4, 4, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.White);
        image.ClipPath(PixelPath.Empty);

        AlphaAt(image, 1, 1).ShouldBe((byte)0);
    }

    [Fact]
    public void Stroke_RejectsIllegalOptions()
    {
        var builder = new PixelPathBuilder();
        builder.MoveTo(0, 0);
        builder.LineTo(4, 4);
        var path = builder.Build();

        using var image = Canvas(8, 8);
        Should.Throw<ArgumentOutOfRangeException>(() => image.DrawPath(path, RgbaColor.White, new StrokeOptions(width: 0f)));
        Should.Throw<ArgumentOutOfRangeException>(() => image.DrawPath(path, RgbaColor.White, new StrokeOptions(width: -2f)));
        Should.Throw<ArgumentOutOfRangeException>(() => image.DrawPath(path, RgbaColor.White, new StrokeOptions(miterLimit: 0.5f)));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            image.DrawPath(path, RgbaColor.White, new StrokeOptions(dashPattern: new float[] { 0f })));
    }

    [Fact]
    public void Builder_RequiresMoveToBeforeDrawing()
    {
        Should.Throw<InvalidOperationException>(() => new PixelPathBuilder().LineTo(1, 1));
        Should.Throw<InvalidOperationException>(() => new PixelPathBuilder().CubicTo(0, 0, 1, 1, 2, 2));
    }

    [Fact]
    public void Builder_RejectsNonFiniteCoordinates()
    {
        Should.Throw<ArgumentException>(() => new PixelPathBuilder().MoveTo(float.NaN, 0));
        Should.Throw<ArgumentException>(() => new PixelPathBuilder().MoveTo(0, 0).LineTo(float.PositiveInfinity, 1));
    }

    [Fact]
    public void Builder_ContinuesFromTheClosedStart()
    {
        var builder = new PixelPathBuilder();
        builder.MoveTo(2, 2);
        builder.LineTo(6, 2);
        builder.Close();
        builder.LineTo(6, 6);

        var path = builder.Build();
        path.Commands.Length.ShouldBe(5);
        path.Commands[3].Verb.ShouldBe(PathVerb.MoveTo);
        path.Commands[3].End.ShouldBe(new PathPoint(2, 2));
        path.Commands[4].End.ShouldBe(new PathPoint(6, 6));
    }

    [Fact]
    public void FillPath_WorksOnSixteenBit()
    {
        var builder = new PixelPathBuilder();
        builder.AddRectangle(new PixelRect(0, 0, 2, 2));

        using var image = Image.Create(2, 2, WorkingFormat.Rgba16);
        image.FillPath(builder.Build(), new RgbaColor(10, 20, 30, 255));

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.WritablePixels);
        samples[0].ShouldBe((ushort)(10 * 257));
        samples[3].ShouldBe(ushort.MaxValue);
    }
}
