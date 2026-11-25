using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;

namespace SimdImaging;

/// <summary>One input port's source rectangle in a <see cref="PlanStepExplanation"/>.</summary>
public readonly record struct PlanSourceRegion(int Port, PixelRect Region);

/// <summary>
/// What the compiler derived for one step: its output geometry and the source rectangles each input port reads.
/// This is the public form of the plan explanation; the caller can compare it with measured memory and time.
/// </summary>
public sealed record PlanStepExplanation(string Name, bool IsGeometry, int Width, int Height, WorkingFormat Format,
    IReadOnlyList<PlanSourceRegion> SourceRegions, long SourceArea, long OutputArea)
{
    /// <summary>Pixels read by all ports over the pixels written; 1 is a one-to-one read.</summary>
    public double Amplification => OutputArea <= 0 ? 0 : (double)SourceArea / OutputArea;
}
