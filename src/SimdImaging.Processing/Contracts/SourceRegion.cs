using Sdcb.SimdCodecs.Abstractions;

namespace SimdImaging.Processing.Contracts;

/// <summary>
/// The set of source rectangles one input port needs for an output region. Most operators need one rectangle;
/// the type exists so a geometry mapping or a multi-region footprint can return more without changing the
/// interface, and so the compiler can normalize, clamp and cost the set in one place.
/// </summary>
internal sealed class SourceRegionSet
{
    private readonly PixelRect[] _regions;

    private SourceRegionSet(PixelRect[] regions)
    {
        _regions = regions;
        BoundingBox = ComputeBounds(regions);
        Area = SumArea(regions);
    }

    internal static SourceRegionSet Empty { get; } = new([]);

    internal static SourceRegionSet Of(PixelRect region) =>
        region.Width <= 0 || region.Height <= 0 ? Empty : new([region]);

    internal IReadOnlyList<PixelRect> Regions => _regions;

    internal PixelRect BoundingBox { get; }

    /// <summary>Pixels read, counting overlaps once per region (the executor may read them twice).</summary>
    internal long Area { get; }

    internal int Count => _regions.Length;

    /// <summary>Clamps every region to the image and drops the ones that fall outside.</summary>
    internal SourceRegionSet Clamp(int width, int height)
    {
        if (_regions.Length == 0)
            return this;

        var clamped = new List<PixelRect>(_regions.Length);
        foreach (var region in _regions)
        {
            var left = Math.Max(0, region.X);
            var top = Math.Max(0, region.Y);
            var right = Math.Min(width, region.X + region.Width);
            var bottom = Math.Min(height, region.Y + region.Height);
            if (right > left && bottom > top)
                clamped.Add(new PixelRect(left, top, right - left, bottom - top));
        }

        return clamped.Count == _regions.Length && clamped.SequenceEqual(_regions)
            ? this
            : new SourceRegionSet(clamped.ToArray());
    }

    /// <summary>
    /// Merges touching or overlapping rectangles into their bounding box, then caps the set: if more than
    /// <paramref name="maxRegions"/> remain, everything collapses to the overall bounding box. Conservative by
    /// construction — it can read more, never less.
    /// </summary>
    internal SourceRegionSet Normalize(int maxRegions = 32)
    {
        if (_regions.Length <= 1)
            return this;

        var merged = new List<PixelRect>(_regions);
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < merged.Count; i++)
            {
                for (var j = i + 1; j < merged.Count; j++)
                {
                    if (!IntersectsOrTouches(merged[i], merged[j]))
                        continue;

                    merged[i] = Union(merged[i], merged[j]);
                    merged.RemoveAt(j);
                    changed = true;
                    j--;
                }
            }
        }

        if (merged.Count > maxRegions)
            merged = [ComputeBounds(merged.ToArray())];

        return new SourceRegionSet(merged.ToArray());
    }

    internal string Describe() => _regions.Length switch
    {
        0 => "empty",
        1 => $"{_regions[0].X},{_regions[0].Y} {_regions[0].Width}x{_regions[0].Height}",
        _ => $"{_regions.Length} regions in {BoundingBox.Width}x{BoundingBox.Height}",
    };

    private static bool IntersectsOrTouches(PixelRect a, PixelRect b) =>
        a.X <= b.X + b.Width && b.X <= a.X + a.Width &&
        a.Y <= b.Y + b.Height && b.Y <= a.Y + a.Height;

    private static PixelRect Union(PixelRect a, PixelRect b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static long SumArea(PixelRect[] regions)
    {
        long area = 0;
        foreach (var region in regions)
            area += (long)region.Width * region.Height;
        return area;
    }

    private static PixelRect ComputeBounds(PixelRect[] regions)
    {
        if (regions.Length == 0)
            return default;

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        foreach (var region in regions)
        {
            left = Math.Min(left, region.X);
            top = Math.Min(top, region.Y);
            right = Math.Max(right, region.X + region.Width);
            bottom = Math.Max(bottom, region.Y + region.Height);
        }

        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
