namespace SimdImaging.Abstractions;

/// <summary>
/// What a pipeline is going to hold in memory, split by how well it is known.
/// <para>
/// <see cref="InputBytes"/>, <see cref="OutputBytes"/> and <see cref="PlanScratchBytes"/> are exact: they
/// are the buffers the executor rents, and their sizes are arithmetic on the image geometry. The two scratch
/// terms are declarations, not measurements — an operator's internal working set is its own business, so it
/// declares it (see <c>PlanStep.Scratch</c>) and this type reports the declaration.
/// </para>
/// <para>
/// <b>Why the split.</b> A banded step holds its scratch once per worker; a whole-image step runs on one thread
/// and holds it once. Multiplying a whole-image step by the worker count — which this type used to do — reads as
/// conservative and is really just wrong, and it was wrong in the direction that rejects plans that would run.
/// </para>
/// <para>
/// <b>Undeclared is not zero.</b> <see cref="UndeclaredSteps"/> counts the steps that declared nothing. Until it
/// is zero, <see cref="PeakBytes"/> is a floor, not a ceiling, and <see cref="HasEstimatedTerms"/> says so.
/// </para>
/// </summary>
/// <param name="InputBytes">The source pixels handed to the pipeline.</param>
/// <param name="OutputBytes">The image the pipeline returns.</param>
/// <param name="PlanScratchBytes">Band windows and intermediate buffers the executor itself rents.</param>
/// <param name="PerWorkerBytes">The largest declared scratch among banded steps, for one worker.</param>
/// <param name="Workers">How many workers the executor would use.</param>
/// <param name="PeakBytes">The estimate: buffers plus banded scratch times workers plus whole-image scratch.</param>
public readonly record struct MemoryEstimate(
    long InputBytes,
    long OutputBytes,
    long PlanScratchBytes,
    long PerWorkerBytes,
    int Workers,
    long PeakBytes)
{
    /// <summary>
    /// Scratch declared by steps that run whole-image on the calling thread. Held once, not once per worker.
    /// </summary>
    public long WholeImageScratchBytes { get; init; }

    /// <summary>
    /// Steps that declared no scratch. While this is non-zero the estimate is a lower bound: the executor cannot
    /// see inside those operators. A budget that would rather refuse than guess should check it.
    /// </summary>
    public int UndeclaredSteps { get; init; }

    /// <summary>True when every step declared its scratch, i.e. when <see cref="PeakBytes"/> is not a floor.</summary>
    public bool HasEstimatedTerms => UndeclaredSteps == 0;

    /// <summary>The buffers alone, without any operator scratch.</summary>
    public long BufferBytes => InputBytes + OutputBytes + PlanScratchBytes;

    /// <summary>The operator scratch this estimate accounts for, banded and whole-image alike.</summary>
    public long OperatorScratchBytes =>
        (PerWorkerBytes * Math.Max(1, Workers)) + WholeImageScratchBytes;
}
