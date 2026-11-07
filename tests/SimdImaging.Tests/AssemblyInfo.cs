using Xunit;

// The library exposes internal test hooks (ForceScalarPoint, ForceScalarWrite, RowParallel.DegreeOverride)
// that are process-wide. Serial execution keeps those hooks deterministic and makes byte-exact
// parallelism-degree assertions trustworthy.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
