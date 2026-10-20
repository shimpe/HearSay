using HearSay.Objective;
using HearSay.OperatorMatrix.Search;

namespace HearSay.Studio.Tests;

/// <summary>The scales of the search chart: which losses one line may join, and the ticks of the loss axis.</summary>
public sealed class SearchChartTests
{
    private static SearchTracePoint At(string stage, EvaluationLevel level) => new(1, 1, stage, level, 0.1);

    [Fact]
    public void ScaleGroups_JoinDeepAndFinalistOptimization_AndSeparateTheRest()
    {
        int screening = SearchChart.ScaleGroup(At("hypothesis screening", EvaluationLevel.Coarse));
        int survivors = SearchChart.ScaleGroup(At("survivor optimization", EvaluationLevel.Normal));
        int deep = SearchChart.ScaleGroup(At("deep optimization", EvaluationLevel.Normal));
        int finalists = SearchChart.ScaleGroup(At("finalist optimization", EvaluationLevel.Normal));
        int refinement = SearchChart.ScaleGroup(At("local refinement", EvaluationLevel.Detailed));

        Assert.Equal(deep, finalists);
        Assert.Equal(5, new[] { screening, survivors, deep, refinement, SearchChart.ScaleGroup(At("finalist optimization", EvaluationLevel.Detailed)) }.Distinct().Count());
    }

    [Fact]
    public void Ticks_AreDecades_WhenTwoFit_Else1_2_And5()
    {
        Assert.Equal([0.01, 0.1, 1.0], SearchChart.Ticks(Math.Log10(0.005), Math.Log10(3.0)));
        IReadOnlyList<double> narrow = SearchChart.Ticks(Math.Log10(0.09), Math.Log10(0.6));
        Assert.Equal(3, narrow.Count);
        Assert.Equal(0.1, narrow[0], 12);
        Assert.Equal(0.2, narrow[1], 12);
        Assert.Equal(0.5, narrow[2], 12);
    }
}
