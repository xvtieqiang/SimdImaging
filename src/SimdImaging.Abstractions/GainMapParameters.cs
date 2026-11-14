namespace SimdImaging.Abstractions;

/// <summary>
/// The reconstruction parameters a gain map carries — the <c>hdrgm</c> vocabulary, whose values come
/// from the file's XMP and describe how to turn the SDR rendition plus the gain map back into HDR.
/// <para>
/// This is the numeric contract, not the serialisation: <c>SimdImaging</c> converts it to and from the
/// codec's own metadata at the file boundary, which keeps <c>SimdImaging.Processing</c> free of codec
/// types (AGENTS: Abstractions ← Processing ← SimdImaging, never the reverse).
/// </para>
/// <para>
/// All gains are in log2 units: the gain map's 0..1 sample picks a point between
/// <see cref="GainMapMin"/> and <see cref="GainMapMax"/>, and <c>2^that</c> is the brightness
/// multiplier. <see cref="OffsetSdr"/>/<see cref="OffsetHdr"/> are the black offsets the model applies
/// before and after the boost; 1/64 is the value the reference implementation writes.
/// </para>
/// </summary>
public readonly record struct GainMapParameters(
    double GainMapMin,
    double GainMapMax,
    double Gamma,
    double OffsetSdr,
    double OffsetHdr,
    double HdrCapacityMin,
    double HdrCapacityMax,
    bool BaseRenditionIsHdr)
{
    /// <summary>The defaults the format recommends: one stop either way, no offsets beyond 1/64.</summary>
    public static GainMapParameters Default { get; } = new(-1, 1, 1, 1.0 / 64.0, 1.0 / 64.0, 0, 1, false);

    /// <summary>Largest brightness multiplier the map describes: <c>2^HdrCapacityMax</c>.</summary>
    public double Headroom => Math.Pow(2, HdrCapacityMax);

    /// <summary>Throws when a field cannot describe a reconstruction.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A field is outside its range.</exception>
    /// <exception cref="ArgumentException">The capacity range is empty.</exception>
    public void Validate()
    {
        if (!double.IsFinite(GainMapMin) || !double.IsFinite(GainMapMax))
            throw new ArgumentOutOfRangeException(nameof(GainMapMin), GainMapMin, "The gain bounds must be finite.");
        if (GainMapMax < GainMapMin)
            throw new ArgumentOutOfRangeException(nameof(GainMapMax), GainMapMax, "The maximum gain is below the minimum.");
        if (!(Gamma > 0) || !double.IsFinite(Gamma))
            throw new ArgumentOutOfRangeException(nameof(Gamma), Gamma, "Gamma must be finite and positive.");
        if (OffsetSdr < 0 || !double.IsFinite(OffsetSdr))
            throw new ArgumentOutOfRangeException(nameof(OffsetSdr), OffsetSdr, "The SDR offset cannot be negative.");
        if (OffsetHdr < 0 || !double.IsFinite(OffsetHdr))
            throw new ArgumentOutOfRangeException(nameof(OffsetHdr), OffsetHdr, "The HDR offset cannot be negative.");
        if (!double.IsFinite(HdrCapacityMin) || !double.IsFinite(HdrCapacityMax))
            throw new ArgumentOutOfRangeException(nameof(HdrCapacityMin), HdrCapacityMin, "The capacity bounds must be finite.");
        if (HdrCapacityMax < HdrCapacityMin)
            throw new ArgumentException("The capacity range is inverted.", nameof(HdrCapacityMax));
    }
}
