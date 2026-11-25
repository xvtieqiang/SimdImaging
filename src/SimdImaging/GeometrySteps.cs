using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>
/// The geometry steps a recipe can carry. Each one is the lambda that used to sit inside the matching
/// <see cref="ImagePipeline"/> method, plus the two facts the compiler needs: how the size changes, and whether a
/// row-source form exists.
/// </summary>
internal static class GeometrySteps
{
    internal static GeometryDescriptor Resize(int width, int height, Resampler resampler,
        ExecuteOptions? execute = null) =>
        new($"resize {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Canvas.Resize(view, width, height, resampler, ResizeMode.Stretch, Anchor.Centre,
                default, pool),
            (source, pool) => new ResizeRowSource(source, width, height, resampler, execute));

    internal static GeometryDescriptor Resize(int width, int height, ResizeMode mode, Anchor anchor,
        Resampler resampler, RgbaColor background) =>
        new($"resize {width}x{height} {mode}", new FixedSizeRule(width, height),
            (view, pool) => Canvas.Resize(view, width, height, resampler, mode, anchor, background, pool));

    internal static GeometryDescriptor Crop(PixelRect region) =>
        new($"crop {region.Width}x{region.Height}", new FixedSizeRule(region.Width, region.Height),
            (view, pool) => Transforms.Crop(view, region, pool),
            (source, pool) => new CropRowSource(source, region.X, region.Y, region.Width, region.Height, pool));

    internal static GeometryDescriptor Rotate90(bool clockwise) =>
        new("rotate90", new SwappedSizeRule(),
            (view, pool) => Transforms.Rotate90(view, clockwise, pool));

    internal static GeometryDescriptor Rotate180() =>
        new("rotate180", GeometryRule.SameSize, (view, pool) => Transforms.Rotate180(view, pool));

    internal static GeometryDescriptor FlipHorizontal() =>
        new("flip-horizontal", GeometryRule.SameSize,
            (view, pool) => Transforms.FlipHorizontal(view, pool),
            (source, pool) => new FlipRowSource(source));

    internal static GeometryDescriptor FlipVertical() =>
        new("flip-vertical", GeometryRule.SameSize, (view, pool) => Transforms.FlipVertical(view, pool));

    internal static GeometryDescriptor ResizeCanvas(int width, int height, Anchor anchor, RgbaColor background) =>
        new($"canvas {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Canvas.ResizeCanvas(view, width, height, anchor, background, pool));

    internal static GeometryDescriptor Tile(int width, int height, bool mirror) =>
        new($"tile {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Canvas.Tile(view, width, height, mirror, pool));

    internal static GeometryDescriptor Trim(float tolerance) =>
        new("trim", new ContentDerivedRule(), (view, pool) => Canvas.Trim(view, tolerance, pool));

    internal static GeometryDescriptor RoundCorners(float radius) =>
        new("round-corners", GeometryRule.SameSize, (view, pool) => Canvas.RoundCorners(view, radius, pool));

    internal static GeometryDescriptor Transpose(bool anti) =>
        new("transpose", new SwappedSizeRule(), (view, pool) => Orientation.Transpose(view, anti, pool));

    internal static GeometryDescriptor RotateFlip(int quarterTurns, bool flipHorizontal) =>
        new("rotate-flip", new RelativeRule(
                (width, height) => (quarterTurns & 1) == 0 ? (width, height) : (height, width),
                "rotate/flip"),
            (view, pool) => Orientation.RotateFlip(view, quarterTurns, flipHorizontal, pool));

    internal static GeometryDescriptor EntropyCrop(float threshold) =>
        new("entropy-crop", new ContentDerivedRule(),
            (view, pool) => Processing.Geometry.EntropyCrop.Crop(view, threshold, pool));

    internal static GeometryDescriptor RemoveSeams(SeamAxis axis, int count) =>
        new($"remove {count} seams", new RelativeRule(
                (width, height) => axis == SeamAxis.Vertical
                    ? (Math.Max(1, width - count), height)
                    : (width, Math.Max(1, height - count)),
                $"remove {count} seams"),
            (view, pool) => SeamCarving.RemoveSeams(view, axis, count, pool));

    internal static GeometryDescriptor InsertSeams(SeamAxis axis, int count) =>
        new($"insert {count} seams", new RelativeRule(
                (width, height) => axis == SeamAxis.Vertical
                    ? (width + count, height)
                    : (width, height + count),
                $"insert {count} seams"),
            (view, pool) => SeamCarving.InsertSeams(view, axis, count, pool));

    internal static GeometryDescriptor SeamCarve(int width, int height) =>
        new($"carve {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => SeamCarving.CarveTo(view, width, height, pool));

    internal static GeometryDescriptor Pad(int margin, RgbaColor background) =>
        Pad(margin, margin, margin, margin, background);

    internal static GeometryDescriptor Pad(int left, int top, int right, int bottom, RgbaColor background) =>
        new($"pad {left},{top},{right},{bottom}", new RelativeRule(
                (width, height) => (width + left + right, height + top + bottom),
                $"pad {left},{top},{right},{bottom}"),
            (view, pool) => Transforms.Pad(view, left, top, right, bottom, background, pool));

    internal static GeometryDescriptor Affine(AffineMatrix forward, int width, int height, RgbaColor background) =>
        new($"affine {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Mapping.Transform(view, forward, width, height, background, pool));

    internal static GeometryDescriptor AffineWithOptions(AffineMatrix forward, int width, int height,
        Resampler resampler, SampleOptions options, RgbaColor background) =>
        new($"affine {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Mapping.TransformWithOptions(view, forward, width, height, resampler, options,
                background, pool));

    internal static GeometryDescriptor Warp(int width, int height, PixelWarp map, Resampler resampler,
        RgbaColor background) =>
        new($"warp {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Sampling.Warp(view, width, height, map, resampler, background, pool));

    internal static GeometryDescriptor WarpWithOptions(int width, int height, PixelWarp map, Resampler resampler,
        SampleOptions options, RgbaColor background) =>
        new($"warp {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Sampling.WarpWithOptions(view, width, height, map, resampler, options, background, pool));

    internal static GeometryDescriptor PerspectiveWithOptions(ReadOnlySpan<float> homography, int width,
        int height, Resampler resampler, SampleOptions options, RgbaColor background)
    {
        var matrix = homography.ToArray();
        return new($"perspective {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Mapping.PerspectiveWithOptions(view, matrix, width, height, resampler, options,
                background, pool));
    }

    internal static GeometryDescriptor ResizeWithOptions(int width, int height, Resampler resampler,
        SampleOptions options, RgbaColor background) =>
        new($"resize {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Resample.ResizeWithOptions(view, width, height, resampler, options, background, pool));

    internal static GeometryDescriptor ResizeToWidth(int width, Resampler resampler) =>
        new($"resize to width {width}", new RelativeRule(
                (current, height) => (width, Math.Max(1, (int)MathF.Round((float)height * width / current))),
                $"resize to width {width}"),
            (view, pool) => Canvas.Resize(view, width,
                Math.Max(1, (int)MathF.Round((float)view.Height * width / view.Width)), resampler,
                ResizeMode.Stretch, Anchor.Centre, default, pool));

    internal static GeometryDescriptor ResizeToHeight(int height, Resampler resampler) =>
        new($"resize to height {height}", new RelativeRule(
                (width, current) => (Math.Max(1, (int)MathF.Round((float)width * height / current)), height),
                $"resize to height {height}"),
            (view, pool) => Canvas.Resize(view,
                Math.Max(1, (int)MathF.Round((float)view.Width * height / view.Height)), height, resampler,
                ResizeMode.Stretch, Anchor.Centre, default, pool));

    internal static GeometryDescriptor Thumbnail(int maxWidth, int maxHeight, Resampler resampler,
        float sharpenAmount) =>
        new("thumbnail", new RelativeRule(
                (width, height) => Fit(width, height, maxWidth, maxHeight),
                "thumbnail"),
            (view, pool) =>
            {
                var scale = MathF.Min(maxWidth / (float)view.Width, maxHeight / (float)view.Height);
                if (scale >= 1f)
                    return Transforms.Crop(view, new PixelRect(0, 0, view.Width, view.Height), pool);

                var width = Math.Max(1, (int)MathF.Round(view.Width * scale));
                var height = Math.Max(1, (int)MathF.Round(view.Height * scale));
                using var resized = Canvas.Resize(view, width, height, resampler, ResizeMode.Stretch, Anchor.Centre,
                    default, pool);
                return sharpenAmount > 0f
                    ? FilterOps.Sharpen(resized.View, sharpenAmount, 0.8f, pool)
                    : resized.Clone(pool);
            });

    internal static GeometryDescriptor Rotate(float degrees, RgbaColor background) =>
        new($"rotate {degrees}", RotateRule(degrees),
            (view, pool) =>
            {
                var (width, height, forward) = Image.RotationCanvas(view.Width, view.Height, degrees);
                return Mapping.Transform(view, forward, width, height, background, pool);
            });

    internal static GeometryDescriptor RotateWithOptions(float degrees, Resampler resampler, SampleOptions options,
        RgbaColor background) =>
        new($"rotate {degrees}", RotateRule(degrees),
            (view, pool) =>
            {
                var (width, height, forward) = Image.RotationCanvas(view.Width, view.Height, degrees);
                return Mapping.TransformWithOptions(view, forward, width, height, resampler, options, background,
                    pool);
            });

    private static RelativeRule RotateRule(float degrees) =>
        new((width, height) =>
        {
            var (rotatedWidth, rotatedHeight, _) = Image.RotationCanvas(width, height, degrees);
            return (rotatedWidth, rotatedHeight);
        }, $"rotate {degrees}");

    private static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        var scale = MathF.Min(maxWidth / (float)width, maxHeight / (float)height);
        return scale >= 1f
            ? (width, height)
            : (Math.Max(1, (int)MathF.Round(width * scale)), Math.Max(1, (int)MathF.Round(height * scale)));
    }
}
