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

    [Fact]
    public void Survivors_ReserveHalfTheSlotsForTheBestOfEveryGroup()
    {
        // Ranked best first. Group 0 sounds right before any tuning and takes the whole top of the ranking; group 1
        // only gets good after optimization and would not survive a global cut.
        (string Name, int Group)[] ranked = [("n0", 0), ("n1", 0), ("n2", 0), ("n3", 0), ("n4", 0), ("n5", 0), ("s0", 1), ("s1", 1), ("h0", 2)];

        List<(string Name, int Group)> survivors = OperatorMatrixPatchSearchCoordinator.SelectSurvivors(ranked, static r => r.Group, 6);

        // Three reserved slots, the best of every group in turn; then the rest of the ranking; best first again.
        Assert.Equal(["n0", "n1", "n2", "n3", "s0", "h0"], survivors.Select(static s => s.Name));
    }

    [Fact]
    public void Survivors_AreTheWholeRankingWhenItFits()
    {
        (string Name, int Group)[] ranked = [("a", 0), ("b", 1), ("c", 0)];

        Assert.Equal(["a", "b", "c"], OperatorMatrixPatchSearchCoordinator.SelectSurvivors(ranked, static r => r.Group, 10).Select(static s => s.Name));
    }
}
