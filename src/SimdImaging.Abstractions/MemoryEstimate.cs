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
/// <b>Why the split.</b> Scratch declared as <c>PerWorker</c> follows the workers used by that invocation. A
/// whole-image operator is invoked once by the plan, but it may still dispatch its own row-parallel loops; its
/// <c>WholeImage</c> scratch is held once, while its <c>PerWorker</c> scratch follows those internal workers.
/// </para>
/// <para>
/// <b>Undeclared is not zero.</b> <see cref="UndeclaredSteps"/> counts the steps that declared nothing. Until it
/// is zero, <see cref="PeakBytes"/> is a floor, not a ceiling, and <see cref="HasEstimatedTerms"/> says so.
/// </para>
/// </summary>
/// <param name="InputBytes">The source pixels handed to the pipeline.</param>
/// <param name="OutputBytes">The image the pipeline returns.</param>
/// <param name="PlanScratchBytes">Band windows and intermediate buffers the executor itself rents.</param>
/// <param name="PerWorkerBytes">The largest declared per-worker scratch among all steps, for one worker.</param>
/// <param name="Workers">The largest worker count used by any step with per-worker scratch.</param>
/// <param name="PeakBytes">The estimate: buffers plus the maximum simultaneous per-worker scratch peak and whole-image scratch.</param>
public readonly record struct MemoryEstimate(
    long InputBytes,
    long OutputBytes,
    long PlanScratchBytes,
    long PerWorkerBytes,
    int Workers,
    long PeakBytes)
{
    /// <summary>
    /// The maximum simultaneous per-worker scratch after applying each step's actual worker count. This can differ
    /// from <c>PerWorkerBytes × Workers</c> when different steps use different degrees of concurrency.
    /// </summary>
    public long PerWorkerPeakBytes { get; init; }

    /// <summary>
    /// Scratch shared by one whole-image invocation. Held once, not once per worker.
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
        (PerWorkerPeakBytes > 0 || PerWorkerBytes == 0
            ? PerWorkerPeakBytes
            : PerWorkerBytes * Math.Max(1, Workers)) + WholeImageScratchBytes;
}
