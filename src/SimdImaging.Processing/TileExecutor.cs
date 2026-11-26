using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Runs a same-format plan one rectangle at a time, reading each tile's halo from an <see cref="IRegionSource"/>
/// and writing the tile to an <see cref="IRegionSink"/>. The per-tile work is exactly what
/// <see cref="RegionExecutor"/> already does; this type adds the walk over tiles and the sink contract.
/// <para>
/// The walk rents the tile, so the walk reserves it — once, before the rent, at the size of one tile. The region
/// executor receives the tile as its destination and counts only the windows it rents, so the tile appears in the
/// account exactly once. The reservation is held for the whole walk because one tile is resident throughout; the
/// per-tile rent is smaller only at the right and bottom edges, which the full-tile figure covers.
/// </para>
/// </summary>
internal static class TileExecutor
{
    /// <summary>
    /// Executes <paramref name="plan"/> over the whole image described by <paramref name="source"/>, writing
    /// <paramref name="tileWidth"/> × <paramref name="tileHeight"/> rectangles to <paramref name="sink"/>. The tile
    /// size is the only memory knob: a tile plus its halo is resident at a time.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plan contains a step a rectangle cannot run.</exception>
    internal static void Execute(ProcessingPlan plan, IRegionSource source, IRegionSink sink, int tileWidth,
        int tileHeight, ExecuteOptions? options = null, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileHeight);
        options ??= new ExecuteOptions();

        if (source.Width != sink.Width || source.Height != sink.Height)
            throw new ArgumentException(
                $"The sink is {sink.Width}x{sink.Height}, the source is {source.Width}x{source.Height}.");
        if (source.Format != sink.Format)
            throw new ArgumentException($"The sink is {sink.Format}, the source is {source.Format}.");

        // The edge tiles are clipped; budget the largest tile that can actually be rented.
        tileWidth = Math.Min(tileWidth, source.Width);
        tileHeight = Math.Min(tileHeight, source.Height);
        var tileBytes = TargetBuffer.Bytes(tileWidth, tileHeight, source.Format);
        if (options.MaxWorkingBytes > 0 && tileBytes > options.MaxWorkingBytes)
            throw new InvalidOperationException(
                $"A {tileWidth}x{tileHeight} tile needs {tileBytes} bytes, above " +
                $"ExecuteOptions.MaxWorkingBytes = {options.MaxWorkingBytes}.");

        var refusal = RegionExecutor.Classify(plan.Steps, plan.HasResize, out var refusingStep);
        if (refusal != RegionRefusal.None)
            throw new InvalidOperationException(
                $"A tile plan cannot run '{refusingStep}': {refusal}. Refuse it at compile time instead.");

        // MaxWorkingBytes is a per-call ceiling, not a separate allowance for each nested executor.
        // The tile remains alive while RegionExecutor rents its windows, so pass only the remainder down.
        var regionOptions = options;
        if (options.MaxWorkingBytes > 0 && plan.Steps.Count > 0)
        {
            var remaining = options.MaxWorkingBytes - tileBytes;
            if (remaining <= 0)
                throw new InvalidOperationException(
                    $"A {tileWidth}x{tileHeight} tile leaves no working-set budget for region windows.");
            regionOptions = options with { MaxWorkingBytes = remaining };
        }

        using var targetLease = TargetBuffer.Reserve(options, tileWidth, tileHeight, source.Format, "tile destination");

        for (var y = 0; y < sink.Height; y += tileHeight)
        {
            for (var x = 0; x < sink.Width; x += tileWidth)
            {
                var region = new PixelRect(x, y, Math.Min(tileWidth, sink.Width - x),
                    Math.Min(tileHeight, sink.Height - y));
                using var tile = WorkingBuffer.Rent(region.Width, region.Height, source.Format, pool);
                var result = RegionExecutor.Execute(plan, source, region, tile.View, regionOptions, pool);
                if (result.Refusal != RegionRefusal.None)
                    throw new InvalidOperationException(
                        $"The tile at ({region.X},{region.Y}) was refused: {result.Refusal} at '{result.RefusingStep}'.");
                sink.WriteRegion(region, tile.View);
            }
        }
    }
}
