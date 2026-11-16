namespace SimdImaging.Processing;

/// <summary>
/// A chain of per-channel point transforms collapsed into one table, ready to apply to any number of images.
/// </summary>
/// <remarks>
/// <para>
/// Building the table is 1024 transform invocations — cheap next to a full frame and not cheap next to a thumbnail:
/// measured at 0.115 ms, which is about half the time a 256×256 <c>PointChain</c> call takes. A table that outlives
/// the call removes that, which matters when the same grade is applied to a video's worth of frames.
/// </para>
/// <para>
/// Immutable once built, so one instance can be shared across threads and images.
/// </para>
/// </remarks>
public sealed class PointChainTable
{
    private readonly byte[] _bytes;

    private PointChainTable(byte[] bytes) => _bytes = bytes;

    /// <summary>Collapses the transforms into a table.</summary>
    /// <param name="transforms">Per-channel transforms, applied in order.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentException">No transforms were given.</exception>
    /// <exception cref="NotSupportedException">A transform mixes channels, so it has no per-channel table.</exception>
    public static PointChainTable Create(params PointOps.RowTransform[] transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        if (transforms.Length == 0)
            throw new ArgumentException("A chain needs at least one transform.", nameof(transforms));

        for (var i = 0; i < transforms.Length; i++)
        {
            if (!FilterOps.IsPerChannel(transforms[i]))
            {
                throw new NotSupportedException(
                    $"Transform {i} mixes channels, so it has no per-channel table; chain the operators individually.");
            }
        }

        return new PointChainTable(FilterOps.BuildChainTable(transforms));
    }

    /// <summary>The table itself: four channels of 256 entries, channel-major. An array, not a span, because the
    /// band worker captures it.</summary>
    internal byte[] Bytes => _bytes;
}
