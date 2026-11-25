using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;

namespace SimdImaging;

/// <summary>Tile execution: rectangles in, rectangles out, through the same compiled sections the other backends use.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Runs <paramref name="plan"/> over the whole image described by <paramref name="source"/> one
    /// <paramref name="tileWidth"/> × <paramref name="tileHeight"/> rectangle at a time, returning a new buffer.
    /// </summary>
    /// <remarks>
    /// The tile size is the memory knob: one tile plus its halo window is resident at a time. The plan must contain
    /// only steps a rectangle can run; a whole-image step, a composite, a block-aligned step or a format change is
    /// refused with the compiler's reason rather than approximated.
    /// </remarks>
    /// <exception cref="NotSupportedException">The plan has a step a tile cannot run.</exception>
    public static WorkingBuffer ProcessTiles(IRegionSource source, ProcessingPlan plan, int tileWidth, int tileHeight,
        ExecuteOptions? options = null, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var destination = WorkingBuffer.Rent(source.Width, source.Height, source.Format, pool);
        try
        {
            ProcessTiles(source, new BufferRegionSink(destination), plan, tileWidth, tileHeight, options, pool);
            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The sink form of <see cref="ProcessTiles(IRegionSource, ProcessingPlan, int, int, ExecuteOptions?, IPixelPool?)"/>:
    /// each finished tile is handed to <paramref name="sink"/> at its absolute coordinates.
    /// </summary>
    public static void ProcessTiles(IRegionSource source, IRegionSink sink, ProcessingPlan plan, int tileWidth,
        int tileHeight, ExecuteOptions? options = null, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(plan);

        var recipe = new PipelineRecipeBuilder();
        foreach (var step in plan.Steps)
            recipe.Append(step.Definition);

        var compiled = PipelineCompiler.Compile(recipe.Build(), new CompileOptions
        {
            SourceWidth = source.Width,
            SourceHeight = source.Height,
            SourceFormat = source.Format,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion |
                     SourceCapabilities.StableContent,
            Sink = SinkCapabilities.RandomRegionWrite,
            TileRequested = true,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            Execute = options ?? new ExecuteOptions(),
        });

        if (compiled.Backend != ExecutionBackend.Tile)
            throw new NotSupportedException(
                $"The plan cannot run as tiles: {compiled.TileRefusal?.ToString() ?? compiled.Backend.ToString()}.");

        CompiledPlanExecutor.ExecuteTiles(compiled, source, sink, tileWidth, tileHeight, options, pool);
    }

    /// <summary>The in-memory sink behind the buffer-returning overload; it owns nothing.</summary>
    private sealed class BufferRegionSink(WorkingBuffer buffer) : IRegionSink
    {
        public int Width => buffer.Width;
        public int Height => buffer.Height;
        public WorkingFormat Format => buffer.Format;

        public void WriteRegion(PixelRect region, PixelView source)
        {
            var bytes = Format.BytesPerPixel();
            if (region.Width != source.Width || region.Height != source.Height)
                throw new ArgumentException("The source view is not the region's size.", nameof(source));

            for (var y = 0; y < region.Height; y++)
            {
                source.Row(y).CopyTo(buffer.View.Row(region.Y + y)
                    .Slice(region.X * bytes, region.Width * bytes));
            }
        }
    }
}
