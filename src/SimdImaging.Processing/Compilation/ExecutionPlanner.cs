using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing.Fusion;

namespace SimdImaging.Processing.Compilation;

/// <summary>
/// Turns an <see cref="ExecutionRequest"/> into one decision: which backend runs the chain, whether the budget
/// admits it, and why the request was refused when nothing fits. It reuses <see cref="PipelineCompiler"/> for
/// per-backend compilation and <see cref="WorkspacePlanner"/> for the estimate; it does not execute anything.
/// </summary>
internal static class ExecutionPlanner
{
    internal static ExecutionDecision Plan(ExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryBuildCandidates(request, out var candidates, out var invalidMessage))
        {
            var invalid = CandidateEvaluation.Failed(
                request.Preferred ?? ExecutionBackend.Frame,
                ExecutionRefusalReason.InvalidRequest, null, invalidMessage);
            return new ExecutionDecision(request.Policy, request.Preferred, null, invalid,
                ExecutionRefusalReason.InvalidRequest, null, invalidMessage, false, [invalid]);
        }

        var evaluations = new List<CandidateEvaluation>(candidates.Count);
        var bestEffort = false;
        foreach (var backend in candidates)
        {
            var evaluation = Evaluate(request, backend, out var compiled);
            evaluations.Add(evaluation);
            if (!evaluation.Feasible)
                continue;

            bestEffort = evaluation.IsLowerBound && request.Execute.AllowUnestimatedScratch;
            return new ExecutionDecision(request.Policy, request.Preferred, compiled, null,
                ExecutionRefusalReason.None, null, null, bestEffort, evaluations);
        }

        // No candidate fits. The primary reason is the first candidate's blocker: that is the backend the
        // caller preferred (or the only one the strict policy allows), and it is the most actionable answer.
        // The rest stay in Candidates for diagnostics rather than being collapsed into "needs N bytes".
        var primary = evaluations[0];
        return new ExecutionDecision(request.Policy, request.Preferred, null, primary,
            primary.Reason, primary.RefusingStep, primary.Message, false, evaluations);
    }

    private static bool TryBuildCandidates(ExecutionRequest request, out List<ExecutionBackend> candidates,
        out string invalidMessage)
    {
        candidates = [];
        invalidMessage = string.Empty;

        if (request.Region is not null)
        {
            if (request.Policy != ExecutionPolicy.Auto)
            {
                invalidMessage =
                    "A Region request is a data-access constraint; it cannot be combined with RequireTiled or " +
                    "RequireStreaming. Use the Region entry point or drop the policy.";
                return false;
            }

            if (request.Preferred is ExecutionBackend.Tile or ExecutionBackend.Stream)
            {
                invalidMessage =
                    "A Region request cannot also prefer Tile or Stream; the region is the execution constraint.";
                return false;
            }

            candidates.Add(ExecutionBackend.Region);
            return true;
        }

        switch (request.Policy)
        {
            case ExecutionPolicy.RequireTiled when request.Preferred is ExecutionBackend.Stream or ExecutionBackend.Frame:
                invalidMessage = "RequireTiled conflicts with the entry point's streaming/materialised preference.";
                return false;
            case ExecutionPolicy.RequireStreaming when request.Preferred is ExecutionBackend.Tile or ExecutionBackend.Frame:
                invalidMessage = "RequireStreaming conflicts with the entry point's tiled/materialised preference.";
                return false;
            case ExecutionPolicy.RequireTiled:
                candidates.Add(ExecutionBackend.Tile);
                return true;
            case ExecutionPolicy.RequireStreaming:
                candidates.Add(ExecutionBackend.Stream);
                return true;
        }

        switch (request.Preferred)
        {
            case ExecutionBackend.Tile:
                candidates.Add(ExecutionBackend.Tile);
                candidates.Add(ExecutionBackend.Frame);
                break;
            case ExecutionBackend.Stream:
                candidates.Add(ExecutionBackend.Stream);
                candidates.Add(ExecutionBackend.Frame);
                break;
            case ExecutionBackend.Frame:
                candidates.Add(ExecutionBackend.Frame);
                break;
            case ExecutionBackend.Region:
                invalidMessage = "A Region preference must use ExecutionRequest.Region, not Preferred.";
                return false;
            default:
                candidates.Add(ExecutionBackend.Stream);
                candidates.Add(ExecutionBackend.Frame);
                break;
        }

        return true;
    }

    private static CandidateEvaluation Evaluate(ExecutionRequest request, ExecutionBackend backend,
        out CompiledExecutionPlan? compiled)
    {
        compiled = null;
        if (backend == ExecutionBackend.Tile &&
            (request.Source & SourceCapabilities.RandomRegion) == 0)
        {
            return CandidateEvaluation.Failed(backend, ExecutionRefusalReason.Capability, null,
                "The source cannot read random regions, which a tile walk requires.");
        }

        var options = CompileOptionsFor(request, backend);
        try
        {
            compiled = PipelineCompiler.Compile(request.Recipe, options);
        }
        catch (NotSupportedException e)
        {
            return CandidateEvaluation.Failed(backend, ExecutionRefusalReason.InvalidPlan, null, e.Message);
        }
        catch (InvalidOperationException e)
        {
            return CandidateEvaluation.Failed(backend, ExecutionRefusalReason.InvalidPlan, null, e.Message);
        }

        var actual = compiled.Backend;
        var refusalStep = RefusingStep(compiled, backend);
        var refusalMessage = RefusalMessage(compiled, backend);
        if (actual != backend)
        {
            compiled = null;
            return CandidateEvaluation.Failed(backend, ExecutionRefusalReason.Capability, refusalStep,
                refusalMessage ?? $"The compiler selected {actual} instead of {backend}.");
        }

        var workspaceBytes = compiled.Workspace.BoundedBytes;
        var extra = request.AdditionalBytesFor?.Invoke(backend) ?? 0;
        var bytes = checked(workspaceBytes + extra);
        var lowerBound = compiled.Workspace.IsLowerBound;
        var max = request.Execute.MaxWorkingBytes;
        if (max > 0)
        {
            if (bytes > max)
            {
                compiled = null;
                return CandidateEvaluation.Failed(backend, ExecutionRefusalReason.Budget, null,
                    $"The plan's estimated peak is {bytes} bytes, above MaxWorkingBytes = {max}.", bytes, lowerBound);
            }

            if (lowerBound && !request.Execute.AllowUnestimatedScratch)
            {
                compiled = null;
                return CandidateEvaluation.Failed(backend, ExecutionRefusalReason.BudgetUnverifiable, null,
                    $"The plan's estimate is a lower bound ({bytes} bytes): some steps declare no scratch. " +
                    "Declare the workspace or set AllowUnestimatedScratch for a best-effort run.", bytes, true);
            }
        }

        return CandidateEvaluation.Selected(backend, bytes, lowerBound);
    }

    private static CompileOptions CompileOptionsFor(ExecutionRequest request, ExecutionBackend backend)
    {
        var execute = request.Execute with { MaxWorkingBytes = 0 };
        // Frame/materialised and Region return a buffer, so the caller's sink capabilities do not constrain
        // them. Stream and Tile only work when the real sink can accept their output shape.
        var sink = backend is ExecutionBackend.Stream or ExecutionBackend.Tile
            ? request.Sink
            : SinkCapabilities.None;

        return new CompileOptions
        {
            SourceWidth = request.Width,
            SourceHeight = request.Height,
            SourceFormat = request.Format,
            Source = request.Source,
            Sink = sink,
            BandRows = request.BandRows,
            Region = backend == ExecutionBackend.Region ? request.Region : null,
            TileRequested = backend == ExecutionBackend.Tile,
            TileWidth = request.TileWidth,
            TileHeight = request.TileHeight,
            ObserveSteps = request.ObserveSteps,
            MaxRegionReadAmplification = request.MaxRegionReadAmplification,
            // The candidate knows what it will run: a streamed plan links stages at its boundaries, everything else
            // materialises a frame. Compiling each candidate with its own price is why the planner does not need one
            // segmentation that is wrong for half of its options.
            Boundary = backend == ExecutionBackend.Stream ? FusionBoundary.RowPump : FusionBoundary.Frame,
            Execute = execute,
        };
    }

    private static string? RefusingStep(CompiledExecutionPlan compiled, ExecutionBackend backend)
    {
        if (backend == ExecutionBackend.Stream)
        {
            StreamingExecutor.Classify(compiled.Steps, out var step);
            return step;
        }

        if (backend == ExecutionBackend.Region)
        {
            var hasResize = compiled.Recipe.Steps.Any(recipeStep =>
                recipeStep is GeometryStep geometry && geometry.Kind == GeometryKind.Resize);
            RegionExecutor.Classify(compiled.Steps, hasResize, out var step);
            return step;
        }

        // RefuseTile reports a category, not a step; the compiler does not retain it yet.
        return null;
    }

    private static string? RefusalMessage(CompiledExecutionPlan compiled, ExecutionBackend backend) =>
        backend switch
        {
            ExecutionBackend.Tile when compiled.TileRefusal is { } tile => $"Tile refused: {tile}.",
            ExecutionBackend.Stream when compiled.StreamRefusal != StreamRefusal.None =>
                $"Stream refused: {compiled.StreamRefusal}.",
            ExecutionBackend.Region when compiled.RegionRefusal != RegionRefusal.None =>
                $"Region refused: {compiled.RegionRefusal}.",
            _ => null,
        };
}
