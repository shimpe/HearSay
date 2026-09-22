using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Tests;

public sealed class HypothesisCutTests
{
    private static List<PatchHypothesis> Group(string name, params double[] scores) =>
        [.. scores.Select((s, i) => new PatchHypothesis(OperatorMatrixPatch.Silent(6), s, $"{name}{i}"))];

    [Fact]
    public void Cut_TakesTheBestOfEveryProviderInTurn()
    {
        // The first provider scores everything high, the second modestly: a global cut would drop the second entirely.
        List<List<PatchHypothesis>> groups = [Group("high", 0.9, 0.8, 0.7, 0.6, 0.5), Group("modest", 0.3, 0.2, 0.1)];

        List<PatchHypothesis> kept = OperatorMatrixPatchSearchCoordinator.Interleave(groups, 4);

        Assert.Equal(["high0", "modest0", "high1", "modest1"], kept.Select(static h => h.Origin));
    }

    [Fact]
    public void Cut_OrdersWithinAProviderByScore_AndStopsWhenAllAreTaken()
    {
        List<List<PatchHypothesis>> groups = [Group("a", 0.1, 0.5, 0.3), [], Group("b", 0.2)];

        List<PatchHypothesis> kept = OperatorMatrixPatchSearchCoordinator.Interleave(groups, 100);

        Assert.Equal(["a1", "b0", "a2", "a0"], kept.Select(static h => h.Origin));
    }
}
