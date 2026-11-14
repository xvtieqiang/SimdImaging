namespace SimdImaging.Tests;

/// <summary>
/// Keeps <c>docs/contracts.md</c> honest. Each contract names a test class as its anchor; this test proves
/// every named class exists in this assembly, so the table cannot drift into prose that nobody tests.
/// <para>
/// It does not parse the markdown — a parser would only prove the file and the array agree, and the array is
/// the list of things that must exist. Adding a contract means adding its class here in the same change,
/// which is exactly the reminder the document promises.
/// </para>
/// </summary>
public class ContractsTests
{
    private static readonly (int Id, string Anchor)[] Contracts =
    [
        (1, nameof(RowMathTests)),
        (2, nameof(FiniteContractTests)),
        (3, nameof(ProcessingPlanLeakTests)),
        (4, nameof(FusedExecutorEquivalenceTests)),
        (5, nameof(PointLutCompositionTests)),
        (6, nameof(SampleOptionsContractTests)),
        (7, nameof(GeometryOptionsContractTests)),
        (8, nameof(EwaPrecisionTests)),
        (9, nameof(MappingTapPlanEquivalenceTests)),
        (10, nameof(FilterRgba16Tests)),
        (11, nameof(HdrTests)),
        (12, nameof(HdrTests)),
        (13, nameof(HdrTests)),
        (14, nameof(GainMappedImageTests)),
        (15, nameof(GainMapTests)),
        (16, nameof(IccTests)),
        (17, nameof(IccTests)),
        (18, nameof(IccClutTests)),
        (19, nameof(ColorDifferenceTests)),
        (20, nameof(IccTests)),
        (21, nameof(IccTests)),
        (22, nameof(NonLocalMeansTests)),
        (23, nameof(LocalLaplacianTests)),
        (24, nameof(RichardsonLucyTests)),
        (25, nameof(SeamCarvingTests)),
        (26, nameof(MemoryBudgetTests)),
        (27, nameof(MemoryEstimateTests)),
        (28, nameof(PublicApiTests)),
        (29, nameof(MetadataOpsTests)),
        (30, nameof(DistanceTransformTests)),
    ];

    [Fact]
    public void EveryContractNamesATestClassThatExists()
    {
        foreach (var (id, anchor) in Contracts)
        {
            var type = typeof(ContractsTests).Assembly.GetType($"SimdImaging.Tests.{anchor}");
            type.ShouldNotBeNull($"contract {id} points at {anchor}, which is not in the test assembly");
            type!.GetMethods().Any(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0
                                        || m.GetCustomAttributes(typeof(TheoryAttribute), false).Length > 0)
                .ShouldBeTrue($"contract {id}: {anchor} has no [Fact] or [Theory] — an empty anchor proves nothing");
        }
    }

    [Fact]
    public void ThereAreThirtyContractsAndNoneIsUnanchored()
    {
        Contracts.Length.ShouldBe(30, "docs/contracts.md lists 30; update both together");
        Contracts.Select(c => c.Id).ShouldBe(Enumerable.Range(1, 30));
        Contracts.ShouldAllBe(c => !string.IsNullOrWhiteSpace(c.Anchor));
    }
}
