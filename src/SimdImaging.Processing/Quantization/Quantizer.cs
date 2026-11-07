namespace SimdImaging.Processing;

/// <summary>Which palette builder <c>Image.Quantize</c> uses.</summary>
public enum Quantizer : byte
{
    /// <summary>Median cut over a 5-bit histogram: splits the box with the widest axis at its weighted median.</summary>
    MedianCut,

    /// <summary>Wu's variance-minimizing split: evaluates every cut against cumulative moments, no sorting.</summary>
    Wu,

    /// <summary>Eight-level octree over the colour bits; the least populated deep nodes merge first.</summary>
    Octree,
}
