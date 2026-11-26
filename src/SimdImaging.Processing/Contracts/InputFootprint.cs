using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Contracts;

/// <summary>What shape of input a step reads. A fixed halo is one kind, not the definition.</summary>
internal enum InputFootprintKind : byte
{
    /// <summary>Output pixel <c>(x, y)</c> reads input pixel <c>(x, y)</c>.</summary>
    Identity = 0,

    /// <summary>The output rectangle widened by a constant radius on each side.</summary>
    Halo = 1,

    /// <summary>The whole image, whoever asks. Mirror/Wrap borders and whole-image statistics both land here.</summary>
    WholeSource = 2,

    /// <summary>The output rectangle widened to the block grid that contains it.</summary>
    BlockAligned = 3,

}

/// <summary>
/// The source region a step reads to produce a destination region.
/// <para>
/// This exists because <c>HaloX/HaloY</c> is not expressive enough: Pixelate reads whole blocks (a radius that
/// depends on where the band starts), Mirror/Wrap read the other edge of the image, and a geometry step reads the
/// inverse-mapped rectangle. Declaring a constant halo for those is not an approximation — it is a wrong pixel at
/// a boundary, which is how the two halo bugs recorded in <c>known-gaps.md</c> happened.
/// </para>
/// </summary>
internal interface IInputFootprint
{
    InputFootprintKind Kind { get; }

    /// <summary>Columns read on either side of the destination rectangle; 0 for the non-halo kinds.</summary>
    int HaloX { get; }

    /// <summary>Rows read above and below the destination rectangle; 0 for the non-halo kinds.</summary>
    int HaloY { get; }

    /// <summary>True when every execution must see the whole source, not a window of it.</summary>
    bool CoversWholeImage { get; }

    /// <summary>True when the read is aligned to a block grid rather than to a constant radius.</summary>
    bool IsBlockAligned { get; }

    /// <summary>One line for plan explanations.</summary>
    string Describe { get; }

    /// <summary>The source rectangle a step must be able to read to fill <paramref name="destination"/>.</summary>
    PixelRect MapSourceRect(PixelRect destination, int sourceWidth, int sourceHeight);

    /// <summary>
    /// The source regions a step must read. The default is the single rectangle; a footprint that can need
    /// several (or a geometry mapping) overrides it. Callers normalize and clamp the result.
    /// </summary>
    SourceRegionSet MapSourceRegions(PixelRect destination, int sourceWidth, int sourceHeight) =>
        SourceRegionSet.Of(MapSourceRect(destination, sourceWidth, sourceHeight));
}

/// <summary>One input pixel per output pixel.</summary>
internal sealed class IdentityFootprint : IInputFootprint
{
    internal static IdentityFootprint Instance { get; } = new();

    private IdentityFootprint()
    {
    }

    public InputFootprintKind Kind => InputFootprintKind.Identity;

    public int HaloX => 0;

    public int HaloY => 0;

    public bool CoversWholeImage => false;

    public bool IsBlockAligned => false;

    public string Describe => "identity";

    public PixelRect MapSourceRect(PixelRect destination, int sourceWidth, int sourceHeight) => destination;
}

/// <summary>A constant support radius, clamped to the image.</summary>
internal sealed class HaloFootprint(int haloX, int haloY) : IInputFootprint
{
    public InputFootprintKind Kind => InputFootprintKind.Halo;

    public int HaloX => haloX;

    public int HaloY => haloY;

    public bool CoversWholeImage => false;

    public bool IsBlockAligned => false;

    public string Describe => haloX == haloY ? $"halo {haloX}" : $"halo {haloX}x{haloY}";

    public PixelRect MapSourceRect(PixelRect destination, int sourceWidth, int sourceHeight) =>
        Expand(destination, haloX, haloY, sourceWidth, sourceHeight);

    internal static PixelRect Expand(PixelRect region, int x, int y, int width, int height)
    {
        var left = Math.Max(0, region.X - x);
        var top = Math.Max(0, region.Y - y);
        var right = Math.Min(width, region.X + region.Width + x);
        var bottom = Math.Min(height, region.Y + region.Height + y);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

/// <summary>The entire source image.</summary>
internal sealed class WholeSourceFootprint : IInputFootprint
{
    internal static WholeSourceFootprint Instance { get; } = new();

    private WholeSourceFootprint()
    {
    }

    public InputFootprintKind Kind => InputFootprintKind.WholeSource;

    public int HaloX => 0;

    public int HaloY => 0;

    public bool CoversWholeImage => true;

    public bool IsBlockAligned => false;

    public string Describe => "whole source";

    public PixelRect MapSourceRect(PixelRect destination, int sourceWidth, int sourceHeight) =>
        new(0, 0, sourceWidth, sourceHeight);
}

/// <summary>
/// The destination rectangle widened to the block grid that contains it. A constant halo cannot express this:
/// the radius a band needs depends on where the band starts, so any fixed number is wrong on one side or the
/// other. Operators that read this way must also declare <c>Partitioning.WholeImage</c> until a block-aware
/// band executor exists.
/// </summary>
internal sealed class BlockAlignedFootprint(int blockSize) : IInputFootprint
{
    internal int BlockSize => blockSize;

    public InputFootprintKind Kind => InputFootprintKind.BlockAligned;

    public int HaloX => 0;

    public int HaloY => 0;

    public bool CoversWholeImage => false;

    public bool IsBlockAligned => true;

    public string Describe => $"block-aligned {blockSize}px";

    public PixelRect MapSourceRect(PixelRect destination, int sourceWidth, int sourceHeight)
    {
        var size = Math.Max(1, blockSize);
        var left = (int)((long)Math.Max(0, destination.X) / size * size);
        var top = (int)((long)Math.Max(0, destination.Y) / size * size);
        var right = (int)Math.Min(sourceWidth, (long)(destination.X + destination.Width + size - 1) / size * size);
        var bottom = (int)Math.Min(sourceHeight, (long)(destination.Y + destination.Height + size - 1) / size * size);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
