using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Compilation;

/// <summary>Everything the compiler is allowed to consider besides the recipe itself.</summary>
internal sealed record CompileOptions
{
    /// <summary>Decoded width of the source the recipe will run on.</summary>
    internal int SourceWidth { get; init; }

    /// <summary>Decoded height of the source.</summary>
    internal int SourceHeight { get; init; }

    /// <summary>Working format of the source.</summary>
    internal WorkingFormat SourceFormat { get; init; }

    /// <summary>What the source can do. Defaults to a rewound file: seekable, replayable, stable.</summary>
    internal SourceCapabilities Source { get; init; } = SourceCapabilities.Seekable | SourceCapabilities.Replayable |
                                                       SourceCapabilities.StableContent;

    /// <summary>What the destination can accept. No capabilities means "a caller's buffer".</summary>
    internal SinkCapabilities Sink { get; init; }

    /// <summary>Budget, degree, fusion switches, diagnostics and cancellation for the plan sections.</summary>
    internal ExecuteOptions Execute { get; init; } = new();

    /// <summary>True when a <c>PlanObserver</c> needs every step's full output; fusion is then refused and the
    /// stream backend cannot avoid materialising.</summary>
    internal bool ObserveSteps { get; init; }

    /// <summary>Rows per band; 0 lets the executor size it, any positive value (128 included) is literal. The
    /// workspace prediction resolves 0 to <see cref="ProcessingPlan.DefaultBandRows"/>, which bounds the automatic
    /// band from above.</summary>
    internal int BandRows { get; init; }

    /// <summary>The rectangle the caller asked for, when the region backend is being considered.</summary>
    internal PixelRect? Region { get; init; }

    /// <summary>True when the caller asked for a two-dimensional tile execution.</summary>
    internal bool TileRequested { get; init; }

    /// <summary>The tile width the caller will use; 0 means the whole output for region-cost purposes.</summary>
    internal int TileWidth { get; init; }

    /// <summary>The tile height the caller will use; 0 means the whole output.</summary>
    internal int TileHeight { get; init; }

    /// <summary>Refuses a region run whose source read is more than this multiple of the output; 0 disables.</summary>
    internal double MaxRegionReadAmplification { get; init; }
}
