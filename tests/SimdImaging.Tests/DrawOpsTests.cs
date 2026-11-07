using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Tests;

public class DrawOpsTests
{
    private static byte[] At(Image image, int x, int y)
    {
        var o = (y * image.Width + x) * 4;
        return image.WritablePixels.Slice(o, 4).ToArray();
    }

    [Fact]
    public void Fill_CoversEveryPixel()
    {
        using var image = Image.Create(4, 3, WorkingFormat.Rgba8);
        image.Fill(new RgbaColor(10, 20, 30, 40));

        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i += 4)
            span.Slice(i, 4).ToArray().ShouldBe(new byte[] { 10, 20, 30, 40 });
    }

    [Fact]
    public void FillRect_ClipsToTheImage()
    {
        using var image = Image.Create(4, 4, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.FillRect(new PixelRect(-1, -1, 3, 3), RgbaColor.White);

        At(image, 0, 0).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 1, 1).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 2, 2).ShouldBe(new byte[] { 0, 0, 0, 255 });
        At(image, 3, 3).ShouldBe(new byte[] { 0, 0, 0, 255 });
    }

    [Fact]
    public void FillRect_EntirelyOutsideIsANoOp()
    {
        using var image = Image.Create(3, 3, WorkingFormat.Rgba8);
        image.Fill(new RgbaColor(1, 2, 3, 255));
        image.FillRect(new PixelRect(10, 10, 5, 5), RgbaColor.White);

        At(image, 2, 2).ShouldBe(new byte[] { 1, 2, 3, 255 });
    }

    [Fact]
    public void Clear_ZeroesEverything()
    {
        using var image = Image.Create(3, 2, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.White);
        image.Clear();

        var span = image.WritablePixels;
        for (var i = 0; i < span.Length; i++)
            span[i].ShouldBe((byte)0);
    }

    [Fact]
    public void Fill_ScalesToSixteenBit()
    {
        using var image = Image.Create(2, 2, WorkingFormat.Rgba16);
        image.Fill(new RgbaColor(255, 0, 128, 255));

        var samples = MemoryMarshal.Cast<byte, ushort>(image.WritablePixels);
        samples[0].ShouldBe((ushort)65535);
        samples[1].ShouldBe((ushort)0);
        samples[2].ShouldBe((ushort)(128 * 257));
        samples[3].ShouldBe((ushort)65535);
    }

    [Fact]
    public void Fill_WritesFloatInUnitRange()
    {
        using var image = Image.Create(2, 2, WorkingFormat.Rgba32F);
        image.Fill(new RgbaColor(255, 0, 51, 255));

        var samples = MemoryMarshal.Cast<byte, float>(image.WritablePixels);
        samples[0].ShouldBe(1f);
        samples[2].ShouldBe(0.2f, 1e-6f);
    }

    [Fact]
    public void DrawLine_HorizontalCoversTheRun()
    {
        using var image = Image.Create(6, 3, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.DrawLine(1, 1, 4, 1, RgbaColor.White);

        At(image, 0, 1).ShouldBe(new byte[] { 0, 0, 0, 255 });
        At(image, 1, 1).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 4, 1).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 5, 1).ShouldBe(new byte[] { 0, 0, 0, 255 });
        At(image, 2, 0).ShouldBe(new byte[] { 0, 0, 0, 255 });
    }

    [Fact]
    public void DrawLine_DiagonalIsContinuous()
    {
        using var image = Image.Create(5, 5, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.DrawLine(0, 0, 4, 4, RgbaColor.White);

        for (var i = 0; i < 5; i++)
            At(image, i, i).ShouldBe(new byte[] { 255, 255, 255, 255 });
    }

    [Fact]
    public void DrawLine_ClipsASegmentStartingFarOutside()
    {
        using var image = Image.Create(5, 5, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);

        // Without clipping this would iterate ~2^31 times.
        image.DrawLine(-1000, 2, int.MaxValue, 2, RgbaColor.White);

        At(image, 0, 2).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 4, 2).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 2, 1).ShouldBe(new byte[] { 0, 0, 0, 255 });
    }

    [Fact]
    public void DrawLine_OutsideIsANoOp()
    {
        using var image = Image.Create(4, 4, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.DrawLine(10, 10, 20, 20, RgbaColor.White);

        At(image, 0, 0).ShouldBe(new byte[] { 0, 0, 0, 255 });
    }

    [Fact]
    public void DrawRect_StrokesOnlyTheBorder()
    {
        using var image = Image.Create(6, 6, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.DrawRect(new PixelRect(1, 1, 4, 4), RgbaColor.White);

        At(image, 1, 1).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 4, 4).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 2, 2).ShouldBe(new byte[] { 0, 0, 0, 255 });
        At(image, 0, 0).ShouldBe(new byte[] { 0, 0, 0, 255 });
    }

    [Fact]
    public void DrawRect_ThicknessTwoCoversTwoRows()
    {
        using var image = Image.Create(8, 8, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.DrawRect(new PixelRect(1, 1, 5, 5), RgbaColor.White, thickness: 2);

        At(image, 2, 1).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 2, 2).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 3, 3).ShouldBe(new byte[] { 0, 0, 0, 255 });
    }

    [Fact]
    public void DrawRect_RejectsNonPositiveThickness()
    {
        using var image = Image.Create(3, 3, WorkingFormat.Rgba8);
        Should.Throw<ArgumentOutOfRangeException>(() => image.DrawRect(new PixelRect(0, 0, 2, 2), RgbaColor.White, 0));
    }

    [Fact]
    public void FillEllipse_IsSymmetricAndLeavesTheCorners()
    {
        using var image = Image.Create(9, 9, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.FillEllipse(new PixelRect(0, 0, 9, 9), RgbaColor.White);

        At(image, 4, 4).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 0, 0).ShouldBe(new byte[] { 0, 0, 0, 255 });
        At(image, 8, 8).ShouldBe(new byte[] { 0, 0, 0, 255 });

        // The middle row spans the full width, and symmetrically.
        for (var x = 0; x < 9; x++)
            At(image, x, 4).ShouldBe(new byte[] { 255, 255, 255, 255 });
    }

    [Fact]
    public void FillEllipse_ClipsToTheImage()
    {
        using var image = Image.Create(5, 5, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);
        image.FillEllipse(new PixelRect(-2, -2, 6, 6), RgbaColor.White);

        At(image, 2, 2).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 4, 4).ShouldBe(new byte[] { 0, 0, 0, 255 });
    }

    [Fact]
    public void FillPolygon_FillsASquare()
    {
        using var image = Image.Create(8, 8, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);

        PixelPoint[] square =
        [
            new(1, 1), new(6, 1), new(6, 6), new(1, 6),
        ];
        image.FillPolygon(square, RgbaColor.White);

        At(image, 3, 3).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 1, 1).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 0, 0).ShouldBe(new byte[] { 0, 0, 0, 255 });
        At(image, 7, 7).ShouldBe(new byte[] { 0, 0, 0, 255 });
    }

    [Fact]
    public void FillPolygon_TriangleFillsAboutHalfItsBounds()
    {
        using var image = Image.Create(10, 10, WorkingFormat.Rgba8);
        image.Fill(RgbaColor.Black);

        PixelPoint[] triangle =
        [
            new(0, 0), new(9, 0), new(0, 9),
        ];
        image.FillPolygon(triangle, RgbaColor.White);

        At(image, 1, 1).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 8, 8).ShouldBe(new byte[] { 0, 0, 0, 255 });
        At(image, 7, 1).ShouldBe(new byte[] { 255, 255, 255, 255 });
        At(image, 8, 1).ShouldBe(new byte[] { 0, 0, 0, 255 });   // x + y = 9 is the hypotenuse
    }

    [Fact]
    public void FillPolygon_RejectsDegenerateInput()
    {
        using var image = Image.Create(4, 4, WorkingFormat.Rgba8);
        Should.Throw<ArgumentException>(() => image.FillPolygon(new[] { new PixelPoint(0, 0), new PixelPoint(1, 1) }, RgbaColor.White));
    }
}
