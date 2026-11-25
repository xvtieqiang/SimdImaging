using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;

namespace SimdImaging;

/// <summary>Which executor the compiler chose for a plan.</summary>
public enum PlanBackend : byte
{
    /// <summary>Decode, run, encode whole.</summary>
    Frame = 0,

    /// <summary>Rows in, rows out.</summary>
    Stream = 1,

    /// <summary>A rectangle decoded and processed on its own.</summary>
    Region = 2,

    /// <summary>Rectangles in, rectangles out.</summary>
    Tile = 3,

    /// <summary>A tile request that could not be honoured; <see cref="PlanRefusals.Tile"/> says why.</summary>
    TileRefused = 4,
}

/// <summary>Why a tile request was refused.</summary>
public enum PlanTileRefusal : byte
{
    /// <summary>Nothing refused it.</summary>
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

/// <summary>What one compiled section contains.</summary>
public enum PlanSectionKind : byte
{
    /// <summary>A contiguous run of same-size operator steps.</summary>
    OperatorRun = 0,

    /// <summary>One geometry change, which materialises or wraps the row stream.</summary>
    Geometry = 1,

    /// <summary>One whole-image step that changes the working format.</summary>
    Format = 2,
}

/// <summary>The kind of one engine-managed workspace term.</summary>
public enum PlanWorkspaceKind : byte
{
    /// <summary>The decoded source frame.</summary>
    Source = 0,

    /// <summary>The result image.</summary>
    Destination = 1,

    /// <summary>Band or stream windows.</summary>
    Window = 2,

    /// <summary>Scratch each worker holds for a band.</summary>
    PerWorkerScratch = 3,

    /// <summary>Scratch a whole-image step holds once.</summary>
    WholeImageScratch = 4,

    /// <summary>The resize row ring.</summary>
    ResizeRing = 5,

    /// <summary>The resize source-row staging chunk.</summary>
    ResizeStaging = 6,

    /// <summary>Destination rows the resize hands out in a batch.</summary>
    ResizeBatch = 7,

    /// <summary>Rows each resize worker accumulates.</summary>
    ResizeWorkerRows = 8,

    /// <summary>A format-changing step's output frame.</summary>
    FormatStep = 9,
}

/// <summary>One input port's source rectangle in a <see cref="PlanStepExplanation"/>.</summary>
public readonly record struct PlanSourceRegion(int Port, PixelRect Region);

/// <summary>One rectangle the compiler counted, and what it represents.</summary>
public sealed record PlanWorkspaceTerm(PlanWorkspaceKind Kind, long Bytes, string Note);

/// <summary>Memory the engine does not own and therefore cannot budget.</summary>
public sealed record PlanUnmanagedTerm(string Name, string Note);

/// <summary>
/// What the plan is predicted to hold, by term, plus everything it cannot see. <paramref name="IsLowerBound"/> is
/// true when at least one step declared no workspace, so <paramref name="PredictedBytes"/> is a lower bound rather
/// than an upper bound.
/// </summary>
public sealed record PlanWorkspaceReport(long PredictedBytes, bool IsLowerBound,
    IReadOnlyList<PlanWorkspaceTerm> Terms, IReadOnlyList<PlanUnmanagedTerm> Unmanaged);

/// <summary>What the compiler refused, by backend. <see cref="StreamRefusal.None"/> and
/// <see cref="RegionRefusal.None"/> mean "available"; a backend that was never offered is explained in
/// <see cref="PlanExplanation.Notes"/>.</summary>
public sealed record PlanRefusals(StreamRefusal Stream, RegionRefusal Region, PlanTileRefusal Tile);

/// <summary>Output geometry after every geometry step, and the chain's read cost for the requested rectangle.</summary>
public sealed record PlanOutputGeometry(bool SizeKnown, int Width, int Height, long RegionSourceArea,
    long RegionOutputArea, int RegionCount)
{
    /// <summary>Pixels read by the whole chain over the pixels written; 1 is a one-to-one read.</summary>
    public double RegionAmplification => RegionOutputArea <= 0 ? 0 : (double)RegionSourceArea / RegionOutputArea;
}

/// <summary>One compiled section: a fused operator run, or one materialising boundary.</summary>
public sealed record PlanSectionExplanation(PlanSectionKind Kind, int RecipeIndex, int StepCount, bool Fused);

/// <summary>The execution requirements a step declared, exposed so a caller can see why a backend was chosen.</summary>
public sealed record PlanStepRequirements(Partitioning Partitioning, PassModel Pass, StateScope State,
    ExecutionOrder Order, Determinism Determinism);

/// <summary>
/// What the compiler derived for one step: its output geometry, the source rectangles each input port reads, and
/// the requirements it declared. This is the structured form of the plan explanation.
/// </summary>
public sealed record PlanStepExplanation(string Name, bool IsGeometry, int Width, int Height, WorkingFormat Format,
    IReadOnlyList<PlanSourceRegion> SourceRegions, long SourceArea, long OutputArea,
    PlanStepRequirements Requirements)
{
    /// <summary>Pixels read by all ports over the pixels written; 1 is a one-to-one read.</summary>
    public double Amplification => OutputArea <= 0 ? 0 : (double)SourceArea / OutputArea;
}

/// <summary>
/// The compiler's account of a plan: the backend it chose, what it refused, how the steps were grouped, per-step
/// requirements and regions, and the workspace prediction. It compiles without executing and holds no buffers.
/// </summary>
public sealed record PlanExplanation(PlanBackend Backend, PlanRefusals Refusals, PlanOutputGeometry Output,
    PlanWorkspaceReport Workspace, IReadOnlyList<PlanSectionExplanation> Sections,
    IReadOnlyList<PlanStepExplanation> Steps, IReadOnlyList<string> Notes)
{
    internal static PlanExplanation From(CompiledExecutionPlan compiled) => new(
        compiled.Backend switch
        {
            ExecutionBackend.Stream => PlanBackend.Stream,
            ExecutionBackend.Region => PlanBackend.Region,
            ExecutionBackend.Tile => PlanBackend.Tile,
            ExecutionBackend.TileRefused => PlanBackend.TileRefused,
            _ => PlanBackend.Frame,
        },
        new PlanRefusals(compiled.StreamRefusal, compiled.RegionRefusal, compiled.TileRefusal switch
        {
            TileRefusal.NoRegionSink => PlanTileRefusal.NoRegionSink,
            TileRefusal.GlobalStep => PlanTileRefusal.GlobalStep,
            TileRefusal.CompositeStep => PlanTileRefusal.CompositeStep,
            TileRefusal.BlockAlignedOperator => PlanTileRefusal.BlockAlignedOperator,
            TileRefusal.NotTileable => PlanTileRefusal.NotTileable,
            TileRefusal.FormatChange => PlanTileRefusal.FormatChange,
            TileRefusal.WholeSourceStep => PlanTileRefusal.WholeSourceStep,
            _ => PlanTileRefusal.None,
        }),
        new PlanOutputGeometry(compiled.SizeKnown, compiled.Width, compiled.Height,
            compiled.RegionCost.SourceArea, compiled.RegionCost.OutputArea, compiled.RegionCost.RegionCount),
        new PlanWorkspaceReport(compiled.Workspace.BoundedBytes, compiled.Workspace.IsLowerBound,
            compiled.Workspace.Terms
                .Select(term => new PlanWorkspaceTerm(term.Kind switch
                {
                    WorkspaceTermKind.Source => PlanWorkspaceKind.Source,
                    WorkspaceTermKind.Destination => PlanWorkspaceKind.Destination,
                    WorkspaceTermKind.Window => PlanWorkspaceKind.Window,
                    WorkspaceTermKind.PerWorkerScratch => PlanWorkspaceKind.PerWorkerScratch,
                    WorkspaceTermKind.WholeImageScratch => PlanWorkspaceKind.WholeImageScratch,
                    WorkspaceTermKind.ResizeRing => PlanWorkspaceKind.ResizeRing,
                    WorkspaceTermKind.ResizeStaging => PlanWorkspaceKind.ResizeStaging,
                    WorkspaceTermKind.ResizeBatch => PlanWorkspaceKind.ResizeBatch,
                    WorkspaceTermKind.ResizeWorkerRows => PlanWorkspaceKind.ResizeWorkerRows,
                    _ => PlanWorkspaceKind.FormatStep,
                }, term.Bytes, term.Note))
                .ToArray(),
            compiled.Workspace.Unmanaged
                .Select(term => new PlanUnmanagedTerm(term.Name, term.Note))
                .ToArray()),
        compiled.Sections
            .Select(section => new PlanSectionExplanation(section.Kind switch
            {
                CompiledSectionKind.Geometry => PlanSectionKind.Geometry,
                CompiledSectionKind.Format => PlanSectionKind.Format,
                _ => PlanSectionKind.OperatorRun,
            }, section.RecipeIndex, section.StepCount, section.Fused))
            .ToArray(),
        compiled.StepInfo
            .Select(info => new PlanStepExplanation(info.Name, info.IsGeometry, info.Width, info.Height, info.Format,
                info.SourceRegions.Select(port => new PlanSourceRegion(port.Port, port.Region)).ToArray(),
                info.SourceArea, info.OutputArea,
                new PlanStepRequirements(info.Requirements.Partitioning, info.Requirements.Pass,
                    info.Requirements.State, info.Requirements.Order, info.Requirements.Determinism)))
            .ToArray(),
        compiled.Explanation.ToArray());
}
