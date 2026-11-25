using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Contracts;

namespace SimdImaging.Processing.Compilation;

/// <summary>One input port's source rectangle for a step's output region.</summary>
internal readonly record struct PortRegion(int Port, PixelRect Region);

/// <summary>The execution requirements a step declared, flattened for the plan explanation.</summary>
internal readonly record struct StepRequirements(Partitioning Partitioning, PassModel Pass, StateScope State,
    ExecutionOrder Order, Determinism Determinism)
{
    internal static StepRequirements Of(OperatorContract contract) => new(contract.Requirements.Partition,
        contract.Pass, contract.Requirements.State, contract.Requirements.Order, contract.Requirements.Determinism);

    /// <summary>A geometry reshape or a format boundary: one whole-image call, no state, order-free.</summary>
    internal static StepRequirements Boundary(Partitioning partitioning) => new(partitioning, PassModel.Single,
        StateScope.None, ExecutionOrder.Any, Determinism.CoordinateDeterministic);
}

/// <summary>
/// What the compiler derived for one step: its output geometry and the source rectangles each input port must
/// read. The region is conservative (a mapping can widen it), which is why the cost is reported alongside.
/// </summary>
internal sealed class CompiledStepInfo
{
    internal CompiledStepInfo(int index, string name, bool isGeometry, int width, int height, WorkingFormat format,
        IReadOnlyList<PortRegion> sourceRegions, long sourceArea, long outputArea, StepRequirements requirements)
    {
        Index = index;
        Name = name;
        IsGeometry = isGeometry;
        Width = width;
        Height = height;
        Format = format;
        SourceRegions = Array.AsReadOnly(sourceRegions.ToArray());
        SourceArea = sourceArea;
        OutputArea = outputArea;
        Requirements = requirements;
    }

    internal int Index { get; }
    internal string Name { get; }
    internal bool IsGeometry { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal WorkingFormat Format { get; }
    internal IReadOnlyList<PortRegion> SourceRegions { get; }

    /// <summary>Pixels read by all ports, counting region overlaps once each.</summary>
    internal long SourceArea { get; }

    /// <summary>Pixels the step writes.</summary>
    internal long OutputArea { get; }

    internal double Amplification => OutputArea <= 0 ? 0 : (double)SourceArea / OutputArea;

    /// <summary>Partitioning, pass model, state, ordering and determinism the step declared.</summary>
    internal StepRequirements Requirements { get; }
}

/// <summary>The whole chain's region cost for the requested output rectangle.</summary>
internal readonly record struct RegionCost(long SourceArea, long OutputArea, int RegionCount)
{
    internal double Amplification => OutputArea <= 0 ? 0 : (double)SourceArea / OutputArea;
}
