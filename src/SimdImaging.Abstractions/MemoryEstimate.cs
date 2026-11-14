namespace SimdImaging.Abstractions;

/// <summary>
/// What a pipeline is going to hold in memory, split by how well it is known.
/// <para>
/// <see cref="InputBytes"/>, <see cref="OutputBytes"/> and <see cref="PlanScratchBytes"/> are exact: they
/// are the buffers the executor rents, and their sizes are arithmetic on the image geometry. The per-worker
/// term is not — an operator's internal scratch is its own business, so it declares it (see
/// <c>PlanStep.Scratch</c>) and this type reports the declaration rather than a measurement. A caller that
/// needs certainty should read <see cref="HasEstimatedTerms"/> before quoting a number.
/// </para>
/// </summary>
/// <param name="InputBytes">The source pixels handed to the pipeline.</param>
/// <param name="OutputBytes">The image the pipeline returns.</param>
/// <param name="PlanScratchBytes">Band windows and intermediate buffers the executor itself rents.</param>
/// <param name="PerWorkerBytes">The largest declared operator scratch, for one worker.</param>
/// <param name="Workers">How many workers the executor would use.</param>
/// <param name="PeakBytes">The estimate: plan scratch plus per-worker scratch times workers.</param>
public readonly record struct MemoryEstimate(
    long InputBytes,
    long OutputBytes,
    long PlanScratchBytes,
    long PerWorkerBytes,
    int Workers,
    long PeakBytes)
{
    /// <summary>True when at least one step declared scratch, i.e. when <see cref="PeakBytes"/> rests on a
    /// declaration rather than on arithmetic the executor controls.</summary>
    public bool HasEstimatedTerms => PerWorkerBytes > 0;

    /// <summary>The buffers alone, without any per-worker scratch.</summary>
    public long BufferBytes => InputBytes + OutputBytes + PlanScratchBytes;
}
