using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Internal;

/// <summary>
/// The target buffer's reservation, in one place. Whoever rents a destination reserves it here <b>before</b> the
/// rent: the region facade for the rectangle it returns, the tile walk for the tile in hand, the buffer tile entry
/// for the frame it returns. The executors that are handed a destination view — <see cref="RegionExecutor"/> and the
/// per-tile work — deliberately do not count it a second time, which is what keeps one tile from being charged once
/// by the walk and once again by the rectangle executor.
/// </summary>
internal static class TargetBuffer
{
    /// <summary>Bytes a <paramref name="width"/> × <paramref name="height"/> target at
    /// <paramref name="format"/> holds.</summary>
    internal static long Bytes(int width, int height, WorkingFormat format) =>
        (long)width * height * format.BytesPerPixel();

    /// <summary>
    /// Reserves the target against <paramref name="options"/>' account, or does nothing when the caller supplied no
    /// account. Call this before the rent, not after it: a reservation refused after the allocation would leave a
    /// buffer no <c>Dispose</c> can reach.
    /// </summary>
    internal static IDisposable? Reserve(ExecuteOptions? options, int width, int height, WorkingFormat format,
        string name) =>
        options?.Workspace?.Reserve(Bytes(width, height, format), name);
}
