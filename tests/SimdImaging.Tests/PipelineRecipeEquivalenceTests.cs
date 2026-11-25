using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// The pipeline now keeps geometry steps in a recipe and lets the compiler place the boundaries. The result must be
/// the bytes the same calls produced when each geometry method materialised on the spot: these chains compare the
/// two spellings — the recipe path and the direct operator calls — over every working format.
/// </summary>
public sealed class PipelineRecipeEquivalenceTests
{
    public static TheoryData<WorkingFormat> Formats => new()
    {
        WorkingFormat.Rgba8,
        WorkingFormat.Rgba16,
        WorkingFormat.Rgba32F,
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void CropBlurResizeSharpen_MatchesTheDirectCalls(WorkingFormat format) =>
        Compare(format, image => image.Pipe().Crop(new PixelRect(8, 6, 96, 72)).GaussianBlur(1f)
            .Resize(64, 40).Sharpen().ToImage(), view =>
        {
            using var cropped = Transforms.Crop(view, new PixelRect(8, 6, 96, 72));
            using var blurred = FilterOps.GaussianBlur(cropped.View, 1f);
            using var resized = Canvas.Resize(blurred.View, 64, 40, Resampler.Bicubic, ResizeMode.Stretch,
                Anchor.Centre, default, null);
            return FilterOps.Sharpen(resized.View, 0.5f, 1f);
        });

    [Theory]
    [MemberData(nameof(Formats))]
    public void RotateFlipContrast_MatchesTheDirectCalls(WorkingFormat format) =>
        Compare(format, image => image.Pipe().Rotate90().FlipHorizontal().Contrast(1.1f).ToImage(), view =>
        {
            using var rotated = Transforms.Rotate90(view, true);
            using var flipped = Transforms.FlipHorizontal(rotated.View);
            return PointOps.Contrast(flipped.View, 1.1f);
        });

    [Theory]
    [MemberData(nameof(Formats))]
    public void PadResize_MatchesTheDirectCalls(WorkingFormat format) =>
        Compare(format, image => image.Pipe().Pad(5, 7, 9, 11, new RgbaColor(10, 20, 30, 255))
            .Resize(70, 50).ToImage(), view =>
        {
            using var padded = Transforms.Pad(view, 5, 7, 9, 11, new RgbaColor(10, 20, 30, 255));
            return Canvas.Resize(padded.View, 70, 50, Resampler.Bicubic, ResizeMode.Stretch, Anchor.Centre,
                default, null);
        });

    [Theory]
    [MemberData(nameof(Formats))]
    public void TransposeRotate180_MatchesTheDirectCalls(WorkingFormat format) =>
        Compare(format, image => image.Pipe().Transpose().Rotate180().ToImage(), view =>
        {
            using var transposed = Orientation.Transpose(view);
            return Transforms.Rotate180(transposed.View);
        });

    [Theory]
    [MemberData(nameof(Formats))]
    public void Thumbnail_MatchesTheDirectCalls(WorkingFormat format) =>
        Compare(format, image => image.Pipe().Thumbnail(48, 32).ToImage(), view =>
        {
            var scale = MathF.Min(48f / view.Width, 32f / view.Height);
            var width = Math.Max(1, (int)MathF.Round(view.Width * scale));
            var height = Math.Max(1, (int)MathF.Round(view.Height * scale));
            using var resized = Canvas.Resize(view, width, height, Resampler.Lanczos3, ResizeMode.Stretch,
                Anchor.Centre, default, null);
            return FilterOps.Sharpen(resized.View, 0.4f, 0.8f);
        });

    [Theory]
    [MemberData(nameof(Formats))]
    public void ResizeToWidth_MatchesTheDirectCalls(WorkingFormat format) =>
        Compare(format, image => image.Pipe().ResizeToWidth(80).ToImage(), view =>
        {
            var height = Math.Max(1, (int)MathF.Round((float)view.Height * 80 / view.Width));
            return Canvas.Resize(view, 80, height, Resampler.Bicubic, ResizeMode.Stretch, Anchor.Centre,
                default, null);
        });

    [Theory]
    [MemberData(nameof(Formats))]
    public void BlurCropResizeBlur_MatchesTheDirectCalls(WorkingFormat format) =>
        Compare(format, image => image.Pipe().GaussianBlur(1f).Crop(new PixelRect(4, 3, 88, 70))
            .Resize(40, 30).GaussianBlur(1.2f).ToImage(), view =>
        {
            using var blurred = FilterOps.GaussianBlur(view, 1f);
            using var cropped = Transforms.Crop(blurred.View, new PixelRect(4, 3, 88, 70));
            using var resized = Canvas.Resize(cropped.View, 40, 30, Resampler.Bicubic, ResizeMode.Stretch,
                Anchor.Centre, default, null);
            return FilterOps.GaussianBlur(resized.View, 1.2f);
        });

    private static void Compare(WorkingFormat format, Func<Image, Image> pipeline,
        Func<PixelView, WorkingBuffer> direct)
    {
        using var image = TestImages.Gradient(128, 96, format);
        using var result = pipeline(image);
        using var expected = direct(image[0].Pixels.View);

        result.Width.ShouldBe(expected.Width);
        result.Height.ShouldBe(expected.Height);
        result.Format.ShouldBe(expected.Format);
        Rows(result).ShouldBe(Rows(expected));
    }

    private static byte[] Rows(Image image) => Rows(image[0].Pixels.View);

    private static byte[] Rows(WorkingBuffer buffer) => Rows(buffer.View);

    private static byte[] Rows(PixelView view)
    {
        var bytes = new byte[view.Height * view.RowBytes];
        for (var y = 0; y < view.Height; y++)
            view.Row(y).CopyTo(bytes.AsSpan(y * view.RowBytes, view.RowBytes));
        return bytes;
    }
}
