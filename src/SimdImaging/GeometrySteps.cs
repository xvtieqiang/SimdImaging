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
            (source, pool, executeOptions) => new ResizeRowSource(source, width, height, resampler,
                executeOptions ?? execute), Kind: GeometryKind.Resize, Resampler: resampler,
            MapRegions: (destination, sourceWidth, sourceHeight, destWidth, destHeight) =>
                ScaleRegion(destination, sourceWidth, sourceHeight, destWidth, destHeight, Support(resampler)),
            Alpha: AlphaPolicy.Recomputes);

    internal static GeometryDescriptor Resize(int width, int height, ResizeMode mode, Anchor anchor,
        Resampler resampler, RgbaColor background)
    {
        MapGeometryRegions? map = mode == ResizeMode.Stretch
            ? (destination, sourceWidth, sourceHeight, destWidth, destHeight) =>
                ScaleRegion(destination, sourceWidth, sourceHeight, destWidth, destHeight, Support(resampler))
            : null;
        return new($"resize {width}x{height} {mode}", new FixedSizeRule(width, height),
            (view, pool) => Canvas.Resize(view, width, height, resampler, mode, anchor, background, pool),
            MapRegions: map, Alpha: AlphaPolicy.Recomputes);
    }

    internal static GeometryDescriptor Crop(PixelRect region) =>
        new($"crop {region.Width}x{region.Height}", new FixedSizeRule(region.Width, region.Height),
            (view, pool) => Transforms.Crop(view, region, pool),
            (source, pool, _) => new CropRowSource(source, region.X, region.Y, region.Width, region.Height, pool),
            Kind: GeometryKind.Crop,
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) => SourceRegionSet.Of(
                new PixelRect(destination.X + region.X, destination.Y + region.Y, destination.Width,
                    destination.Height)).Clamp(sourceWidth, sourceHeight));

    /// <summary>Applies the EXIF orientation as a geometry step; the metadata is set by the pipeline.</summary>
    internal static GeometryDescriptor AutoOrient(ImageOrientation orientation) =>
        new("auto-orient", (int)orientation >= 5 ? new SwappedSizeRule() : GeometryRule.SameSize,
            (view, pool) => Orientation.AutoOrient(view, orientation, pool));

    internal static GeometryDescriptor Rotate90(bool clockwise) =>
        new("rotate90", new SwappedSizeRule(),
            (view, pool) => Transforms.Rotate90(view, clockwise, pool),
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) => SourceRegionSet.Of(
                clockwise
                    ? new PixelRect(destination.Y, sourceHeight - (destination.X + destination.Width),
                        destination.Height, destination.Width)
                    : new PixelRect(sourceWidth - (destination.Y + destination.Height), destination.X,
                        destination.Height, destination.Width)).Clamp(sourceWidth, sourceHeight));

    internal static GeometryDescriptor Rotate180() =>
        new("rotate180", GeometryRule.SameSize, (view, pool) => Transforms.Rotate180(view, pool),
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) => SourceRegionSet.Of(
                new PixelRect(sourceWidth - (destination.X + destination.Width),
                    sourceHeight - (destination.Y + destination.Height), destination.Width,
                    destination.Height)).Clamp(sourceWidth, sourceHeight));

    internal static GeometryDescriptor FlipHorizontal() =>
        new("flip-horizontal", GeometryRule.SameSize,
            (view, pool) => Transforms.FlipHorizontal(view, pool),
            (source, pool, _) => new FlipRowSource(source), Kind: GeometryKind.FlipHorizontal,
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) => SourceRegionSet.Of(
                new PixelRect(sourceWidth - (destination.X + destination.Width), destination.Y,
                    destination.Width, destination.Height)).Clamp(sourceWidth, sourceHeight));

    internal static GeometryDescriptor FlipVertical() =>
        new("flip-vertical", GeometryRule.SameSize, (view, pool) => Transforms.FlipVertical(view, pool),
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) => SourceRegionSet.Of(
                new PixelRect(destination.X, sourceHeight - (destination.Y + destination.Height),
                    destination.Width, destination.Height)).Clamp(sourceWidth, sourceHeight));

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
        new("transpose", new SwappedSizeRule(), (view, pool) => Orientation.Transpose(view, anti, pool),
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) => anti
                ? SourceRegionSet.Of(new PixelRect(sourceWidth - (destination.Y + destination.Height),
                    sourceHeight - (destination.X + destination.Width), destination.Height,
                    destination.Width)).Clamp(sourceWidth, sourceHeight)
                : SourceRegionSet.Of(new PixelRect(destination.Y, destination.X, destination.Height,
                    destination.Width)).Clamp(sourceWidth, sourceHeight));

    internal static GeometryDescriptor RotateFlip(int quarterTurns, bool flipHorizontal) =>
        new("rotate-flip", new RelativeRule(
                (width, height) => (quarterTurns & 1) == 0 ? (width, height) : (height, width),
                "rotate/flip"),
            (view, pool) => Orientation.RotateFlip(view, quarterTurns, flipHorizontal, pool),
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) =>
                MapRotateFlip(quarterTurns, flipHorizontal, destination, sourceWidth, sourceHeight));

    internal static GeometryDescriptor EntropyCrop(float threshold) =>
        new("entropy-crop", new ContentDerivedRule(),
            (view, pool) => Processing.Geometry.EntropyCrop.Crop(view, threshold, pool));

    internal static GeometryDescriptor RemoveSeams(SeamAxis axis, int count) =>
        new($"remove {count} seams", new RelativeRule(
                (width, height) => axis == SeamAxis.Vertical
                    ? (Math.Max(1, width - count), height)
                    : (width, Math.Max(1, height - count)),
                $"remove {count} seams"),
            (view, pool) => SeamCarving.RemoveSeams(view, axis, count, pool),
            Pass: PassModel.WholeFrameAlgorithm, State: StateScope.PerImage, Order: ExecutionOrder.SourceOrder,
            Determinism: Determinism.OrderDependent, Workspace: WorkspaceModel.Undeclared);

    internal static GeometryDescriptor InsertSeams(SeamAxis axis, int count) =>
        new($"insert {count} seams", new RelativeRule(
                (width, height) => axis == SeamAxis.Vertical
                    ? (width + count, height)
                    : (width, height + count),
                $"insert {count} seams"),
            (view, pool) => SeamCarving.InsertSeams(view, axis, count, pool),
            Pass: PassModel.WholeFrameAlgorithm, State: StateScope.PerImage, Order: ExecutionOrder.SourceOrder,
            Determinism: Determinism.OrderDependent, Workspace: WorkspaceModel.Undeclared);

    internal static GeometryDescriptor SeamCarve(int width, int height) =>
        new($"carve {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => SeamCarving.CarveTo(view, width, height, pool),
            Pass: PassModel.WholeFrameAlgorithm, State: StateScope.PerImage, Order: ExecutionOrder.SourceOrder,
            Determinism: Determinism.OrderDependent, Workspace: WorkspaceModel.Undeclared);

    internal static GeometryDescriptor Pad(int margin, RgbaColor background) =>
        Pad(margin, margin, margin, margin, background);

    internal static GeometryDescriptor Pad(int left, int top, int right, int bottom, RgbaColor background) =>
        new($"pad {left},{top},{right},{bottom}", new RelativeRule(
                (width, height) => (width + left + right, height + top + bottom),
                $"pad {left},{top},{right},{bottom}"),
            (view, pool) => Transforms.Pad(view, left, top, right, bottom, background, pool),
            (source, pool, _) => new PadRowSource(source, left, top, right, bottom, background),
            Kind: GeometryKind.Pad,
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) => SourceRegionSet.Of(
                new PixelRect(destination.X - left, destination.Y - top, destination.Width,
                    destination.Height)).Clamp(sourceWidth, sourceHeight));

    internal static GeometryDescriptor Affine(AffineMatrix forward, int width, int height, RgbaColor background) =>
        new($"affine {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Mapping.Transform(view, forward, width, height, background, pool),
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) =>
                MapAffine(forward, destination, sourceWidth, sourceHeight, support: 1),
            Alpha: AlphaPolicy.Recomputes);

    internal static GeometryDescriptor AffineWithOptions(AffineMatrix forward, int width, int height,
        Resampler resampler, SampleOptions options, RgbaColor background) =>
        new($"affine {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Mapping.TransformWithOptions(view, forward, width, height, resampler, options,
                background, pool),
            MapRegions: options.AaMode == SampleAaMode.Elliptical
                ? null
                : (destination, sourceWidth, sourceHeight, _, _) =>
                    MapAffine(forward, destination, sourceWidth, sourceHeight, Support(resampler)),
            Alpha: AlphaPolicy.Recomputes);

    internal static GeometryDescriptor Warp(int width, int height, PixelWarp map, Resampler resampler,
        RgbaColor background) =>
        new($"warp {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Sampling.Warp(view, width, height, map, resampler, background, pool),
            Alpha: AlphaPolicy.Recomputes);

    internal static GeometryDescriptor WarpWithOptions(int width, int height, PixelWarp map, Resampler resampler,
        SampleOptions options, RgbaColor background) =>
        new($"warp {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Sampling.WarpWithOptions(view, width, height, map, resampler, options, background, pool),
            Alpha: AlphaPolicy.Recomputes);

    internal static GeometryDescriptor Perspective(ReadOnlySpan<float> homography, int width, int height,
        RgbaColor background)
    {
        var matrix = homography.ToArray();
        return new($"perspective {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Mapping.Perspective(view, matrix, width, height, background, pool),
            Alpha: AlphaPolicy.Recomputes);
    }

    internal static GeometryDescriptor PerspectiveWithOptions(ReadOnlySpan<float> homography, int width,
        int height, Resampler resampler, SampleOptions options, RgbaColor background)
    {
        var matrix = homography.ToArray();
        return new($"perspective {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Mapping.PerspectiveWithOptions(view, matrix, width, height, resampler, options,
                background, pool),
            Alpha: AlphaPolicy.Recomputes);
    }

    internal static GeometryDescriptor ResizeWithOptions(int width, int height, Resampler resampler,
        SampleOptions options, RgbaColor background) =>
        new($"resize {width}x{height}", new FixedSizeRule(width, height),
            (view, pool) => Resample.ResizeWithOptions(view, width, height, resampler, options, background, pool),
            MapRegions: (destination, sourceWidth, sourceHeight, destWidth, destHeight) =>
                ScaleRegion(destination, sourceWidth, sourceHeight, destWidth, destHeight, Support(resampler)),
            Alpha: AlphaPolicy.Recomputes);

    internal static GeometryDescriptor ResizeToWidth(int width, Resampler resampler) =>
        new($"resize to width {width}", new RelativeRule(
                (current, height) => (width, Math.Max(1, (int)MathF.Round((float)height * width / current))),
                $"resize to width {width}"),
            (view, pool) => Canvas.Resize(view, width,
                Math.Max(1, (int)MathF.Round((float)view.Height * width / view.Width)), resampler,
                ResizeMode.Stretch, Anchor.Centre, default, pool),
            MapRegions: (destination, sourceWidth, sourceHeight, destWidth, destHeight) =>
                ScaleRegion(destination, sourceWidth, sourceHeight, destWidth, destHeight, Support(resampler)),
            Alpha: AlphaPolicy.Recomputes);

    internal static GeometryDescriptor ResizeToHeight(int height, Resampler resampler) =>
        new($"resize to height {height}", new RelativeRule(
                (width, current) => (Math.Max(1, (int)MathF.Round((float)width * height / current)), height),
                $"resize to height {height}"),
            (view, pool) => Canvas.Resize(view,
                Math.Max(1, (int)MathF.Round((float)view.Width * height / view.Height)), height, resampler,
                ResizeMode.Stretch, Anchor.Centre, default, pool),
            MapRegions: (destination, sourceWidth, sourceHeight, destWidth, destHeight) =>
                ScaleRegion(destination, sourceWidth, sourceHeight, destWidth, destHeight, Support(resampler)),
            Alpha: AlphaPolicy.Recomputes);

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
            },
            MapRegions: (destination, sourceWidth, sourceHeight, destWidth, destHeight) =>
                ScaleRegion(destination, sourceWidth, sourceHeight, destWidth, destHeight,
                    Support(resampler) + 1),
            Alpha: AlphaPolicy.Recomputes);

    internal static GeometryDescriptor Rotate(float degrees, RgbaColor background) =>
        new($"rotate {degrees}", RotateRule(degrees),
            (view, pool) =>
            {
                var (width, height, forward) = Image.RotationCanvas(view.Width, view.Height, degrees);
                return Mapping.Transform(view, forward, width, height, background, pool);
            },
            MapRegions: (destination, sourceWidth, sourceHeight, _, _) =>
            {
                var (_, _, forward) = Image.RotationCanvas(sourceWidth, sourceHeight, degrees);
                return MapAffine(forward, destination, sourceWidth, sourceHeight, support: 1);
            },
            Alpha: AlphaPolicy.Recomputes);

    internal static GeometryDescriptor RotateWithOptions(float degrees, Resampler resampler, SampleOptions options,
        RgbaColor background) =>
        new($"rotate {degrees}", RotateRule(degrees),
            (view, pool) =>
            {
                var (width, height, forward) = Image.RotationCanvas(view.Width, view.Height, degrees);
                return Mapping.TransformWithOptions(view, forward, width, height, resampler, options, background,
                    pool);
            },
            MapRegions: options.AaMode == SampleAaMode.Elliptical
                ? null
                : (destination, sourceWidth, sourceHeight, _, _) =>
                {
                    var (_, _, forward) = Image.RotationCanvas(sourceWidth, sourceHeight, degrees);
                    return MapAffine(forward, destination, sourceWidth, sourceHeight, Support(resampler));
                },
            Alpha: AlphaPolicy.Recomputes);

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

    /// <summary>The source rectangle a destination rectangle reads under a quarter-turn/transpose combination.</summary>
    private static SourceRegionSet MapRotateFlip(int quarterTurns, bool flipHorizontal, PixelRect destination,
        int sourceWidth, int sourceHeight)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        var x = destination.X;
        var y = destination.Y;
        var w = destination.Width;
        var h = destination.Height;

        // The kernel walks destination pixels and reads the source pixel each case names; for an axis-aligned
        // rectangle the image of that map is again a rectangle, so the bounds are exact.
        var region = (turns, flipHorizontal) switch
        {
            (0, false) => new PixelRect(x, y, w, h),
            (0, true) => new PixelRect(sourceWidth - (x + w), y, w, h),
            (1, false) => new PixelRect(y, sourceHeight - (x + w), h, w),
            (1, true) => new PixelRect(y, x, h, w),
            (2, false) => new PixelRect(sourceWidth - (x + w), sourceHeight - (y + h), w, h),
            (2, true) => new PixelRect(x, sourceHeight - (y + h), w, h),
            (3, false) => new PixelRect(sourceWidth - (y + h), x, h, w),
            _ => new PixelRect(sourceWidth - (y + h), sourceHeight - (x + w), h, w),
        };
        return SourceRegionSet.Of(region).Clamp(sourceWidth, sourceHeight);
    }

    /// <summary>
    /// The source rectangle a destination rectangle reads through an affine map: the inverse-mapped corners bounded
    /// and widened by the kernel support. The result is a superset, never an under-read; a singular map falls back
    /// to the whole source, as the executor refuses it anyway.
    /// </summary>
    private static SourceRegionSet MapAffine(AffineMatrix forward, PixelRect destination, int sourceWidth,
        int sourceHeight, int support)
    {
        if (!forward.TryInvert(out var inverse))
            return SourceRegionSet.Of(new PixelRect(0, 0, sourceWidth, sourceHeight));

        var left = float.MaxValue;
        var top = float.MaxValue;
        var right = float.MinValue;
        var bottom = float.MinValue;
        Accumulate(inverse, destination.X, destination.Y);
        Accumulate(inverse, destination.X + destination.Width, destination.Y);
        Accumulate(inverse, destination.X + destination.Width, destination.Y + destination.Height);
        Accumulate(inverse, destination.X, destination.Y + destination.Height);

        var x0 = (int)Math.Clamp(MathF.Floor(left) - support, 0, sourceWidth);
        var y0 = (int)Math.Clamp(MathF.Floor(top) - support, 0, sourceHeight);
        var x1 = (int)Math.Clamp(MathF.Ceiling(right) + support, 0, sourceWidth);
        var y1 = (int)Math.Clamp(MathF.Ceiling(bottom) + support, 0, sourceHeight);
        return SourceRegionSet.Of(new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0)));

        void Accumulate(AffineMatrix map, float x, float y)
        {
            var (sx, sy) = map.Apply(x, y);
            left = MathF.Min(left, sx);
            top = MathF.Min(top, sy);
            right = MathF.Max(right, sx);
            bottom = MathF.Max(bottom, sy);
        }
    }

    /// <summary>The largest source-pixel distance a resampler's kernel reaches.</summary>
    private static int Support(Resampler resampler) => resampler switch
    {
        Resampler.Nearest => 0,
        Resampler.Bilinear or Resampler.Triangle => 1,
        Resampler.Bicubic => 2,
        Resampler.Lanczos3 => 3,
        _ => 4,
    };

    /// <summary>
    /// The source rectangle a scaled output rectangle reads: the inverse-mapped rectangle widened by the kernel
    /// support, clamped to the source. It is a conservative single region; a caller that can read rectangles uses
    /// it as the tile/region request.
    /// </summary>
    private static SourceRegionSet ScaleRegion(PixelRect destination, int sourceWidth, int sourceHeight,
        int destWidth, int destHeight, int support)
    {
        if (destWidth <= 0 || destHeight <= 0)
            return SourceRegionSet.Empty;

        var scaleX = (double)sourceWidth / destWidth;
        var scaleY = (double)sourceHeight / destHeight;
        var left = (int)Math.Floor(destination.X * scaleX) - support;
        var top = (int)Math.Floor(destination.Y * scaleY) - support;
        var right = (int)Math.Ceiling((destination.X + destination.Width) * scaleX) + support;
        var bottom = (int)Math.Ceiling((destination.Y + destination.Height) * scaleY) + support;

        left = Math.Clamp(left, 0, sourceWidth);
        top = Math.Clamp(top, 0, sourceHeight);
        right = Math.Clamp(right, 0, sourceWidth);
        bottom = Math.Clamp(bottom, 0, sourceHeight);
        return SourceRegionSet.Of(new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)));
    }
}
