using SimdImaging.Abstractions;
namespace SimdImaging.Processing.Compilation;

/// <summary>Which executor the compiler chose.</summary>
internal enum ExecutionBackend : byte
{
    /// <summary>Decode, run, encode whole.</summary>
    Frame = 0,

    /// <summary>Rows in, rows out.</summary>
    Stream = 1,

    /// <summary>A rectangle decoded and processed on its own.</summary>
    Region = 2,

    /// <summary>A tile request that cannot be honoured; the reason is in <see cref="CompiledExecutionPlan.TileRefusal"/>.</summary>
    TileRefused = 3,

    /// <summary>Rectangles in, rectangles out.</summary>
    Tile = 4,
}

/// <summary>A contiguous run of compiled steps: either operator steps or one geometry change.</summary>
internal enum CompiledSectionKind : byte
{
    OperatorRun = 0,
    Geometry = 1,

    /// <summary>One whole-image step that changes the working format; only Frame can run it.</summary>
    Format = 2,
}

/// <summary>
/// One section of a compiled plan. Operator runs index into <see cref="CompiledExecutionPlan.Steps"/>; a geometry
/// section points back into the recipe for its execution forms.
/// </summary>
internal readonly record struct CompiledSection(CompiledSectionKind Kind, int FirstStep, int StepCount, int RecipeIndex)
{
    /// <summary>Whether the operator run should execute fused. Always false for a geometry section.</summary>
    internal bool Fused { get; init; }
}


/// <summary>One section's geometry at the moment it runs: what it reads and what it produces.</summary>
internal readonly record struct SectionState(CompiledSectionKind Kind, int FirstStep, int StepCount, int RecipeIndex,
    int InputWidth, int InputHeight, int OutputWidth, int OutputHeight, WorkingFormat InputFormat,
    WorkingFormat OutputFormat)
{
    /// <summary>Whether the operator run was fused, which changes its window count.</summary>
    internal bool Fused { get; init; }
}

/// <summary>Why a tile request was refused. No sink advertises random region writes yet, so most requests stop at
/// <see cref="NoRegionSink"/>; the other values exist so the reason survives once one does.</summary>
internal enum TileRefusal : byte
{
    None = 0,

    /// <summary>The destination cannot accept a rectangle.</summary>
    NoRegionSink = 1,

    /// <summary>A step needs the whole image before it writes a pixel.</summary>
    GlobalStep = 2,

    /// <summary>A step needs a second input the tile path does not carry.</summary>
    CompositeStep = 3,

    /// <summary>A step reads a block grid that crosses tile boundaries.</summary>
    BlockAlignedOperator = 4,

    /// <summary>No step declares tile partitioning.</summary>
    NotTileable = 5,

    /// <summary>A step changes the working format, which a tile destination cannot splice.</summary>
    FormatChange = 6,

    /// <summary>A step needs the whole source image (Mirror/Wrap borders); a tile's halo is not enough.</summary>
    WholeSourceStep = 7,
}

/// <summary>
/// The immutable result of compiling a recipe: what runs, in which order, on which backend, and why. It contains no
/// buffers and no delegates other than those already held by the recipe and its steps.
/// </summary>
internal sealed class CompiledExecutionPlan
{
    internal CompiledExecutionPlan(
        PipelineRecipe recipe,
        IReadOnlyList<PlanStep> steps,
        IReadOnlyList<CompiledSection> sections,
        ExecutionBackend backend,
        StreamRefusal streamRefusal,
        RegionRefusal regionRefusal,
        TileRefusal? tileRefusal,
        int width,
        int height,
        bool sizeKnown,
        int bandRows,
        WorkspacePlan workspace,
        IReadOnlyList<CompiledStepInfo> stepInfo,
        RegionCost regionCost,
        IReadOnlyList<string> explanation)
    {
        Recipe = recipe;
        Steps = Array.AsReadOnly(steps.ToArray());
        Sections = Array.AsReadOnly(sections.ToArray());
        Backend = backend;
        StreamRefusal = streamRefusal;
        RegionRefusal = regionRefusal;
        TileRefusal = tileRefusal;
        Width = width;
        Height = height;
        SizeKnown = sizeKnown;
        BandRows = bandRows;
        Workspace = workspace;
        StepInfo = Array.AsReadOnly(stepInfo.ToArray());
        RegionCost = regionCost;
        Explanation = Array.AsReadOnly(explanation.ToArray());
    }

    internal PipelineRecipe Recipe { get; }

    /// <summary>The operator steps, in recipe order, with geometry removed. Sections index into this list.</summary>
    internal IReadOnlyList<PlanStep> Steps { get; }

    internal IReadOnlyList<CompiledSection> Sections { get; }

    internal ExecutionBackend Backend { get; }

    /// <summary>The first reason a stream backend was refused, or <see cref="StreamRefusal.None"/>.</summary>
    internal StreamRefusal StreamRefusal { get; }

    /// <summary>The first reason a region backend was refused, or <see cref="RegionRefusal.None"/>.</summary>
    internal RegionRefusal RegionRefusal { get; }

    /// <summary>Set only when a tile backend was requested and refused.</summary>
    internal TileRefusal? TileRefusal { get; }

    /// <summary>Output width after every geometry step; meaningless when <see cref="SizeKnown"/> is false.</summary>
    internal int Width { get; }

    internal int Height { get; }

    /// <summary>False when a geometry step's size depends on the content (trim, seam carve).</summary>
    internal bool SizeKnown { get; }

    /// <summary>Rows per band the sections will run with.</summary>
    internal int BandRows { get; }

    /// <summary>What the plan is predicted to hold, and what the engine cannot see. Report only.</summary>
    internal WorkspacePlan Workspace { get; }

    /// <summary>Per-step output geometry and source regions, derived backwards from the requested output.</summary>
    internal IReadOnlyList<CompiledStepInfo> StepInfo { get; }

    /// <summary>The source pixels the whole chain reads for the requested output rectangle.</summary>
    internal RegionCost RegionCost { get; }

    /// <summary>One line per decision: step semantics, fusion, backend, refusal.</summary>
    internal IReadOnlyList<string> Explanation { get; }
}
