using HearSay.Analysis;
using HearSay.Objective;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Tests;

/// <summary>The course of a search, point by point, as the Studio draws it: live through the progress and in the summary.</summary>
public sealed class SearchTraceTests
{
    private static (SearchOutcome Outcome, List<SearchProgress> Reports) SmallSearch()
    {
        var renderer = new Fm7CompatibleOperatorMatrixRenderer();
        ReferencePatch bell = ReferencePatches.Find("bell")!;
        RenderedAudio audio = renderer.Render(bell.Patch, RenderRequest.Create(bell.FrequencyHz, 0.8, 0.3));
        ISynthesisPriorProvider sine = new SinePhaseModulationPriorProvider();
        var coordinator = new OperatorMatrixPatchSearchCoordinator(renderer, [sine, new PartialTrackPriorProvider(sine)]);
        var reports = new List<SearchProgress>();
        SearchOutcome outcome = coordinator.Search(
            new AudioTarget(audio.Samples, audio.SampleRate, bell.FrequencyHz),
            new PatchMatchOptions { EvaluationBudget = 600, RandomSeed = 5, StructureLibrarySize = 0 },
            new SynchronousProgress(reports.Add));
        return (outcome, reports);
    }

    [Fact]
    public void Summary_HasEveryScreenedHypothesis_AndTheCourseOfEveryBasin()
    {
        (SearchOutcome outcome, _) = SmallSearch();
        SearchSummary summary = outcome.Summary;
        IReadOnlyList<SearchTracePoint> trace = summary.Trace;

        Assert.Contains(trace, static p => p.Basin == 0 && p.Level == EvaluationLevel.Coarse && p.Stage == "hypothesis screening");
        Assert.All(trace, p => Assert.InRange(p.Evaluations, 1, summary.EvaluationsUsed));
        Assert.All(trace.Zip(trace.Skip(1)), static p => Assert.True(p.Second.Evaluations >= p.First.Evaluations));

        foreach (BasinSummary basin in summary.Basins)
        {
            List<SearchTracePoint> course = [.. trace.Where(p => p.Basin == basin.Id)];
            Assert.NotEmpty(course);
            Assert.Equal(basin.BestLoss, course[^1].Loss);

            // Within a stage and a level the loss only falls.
            foreach ((SearchTracePoint a, SearchTracePoint b) in course.Zip(course.Skip(1)))
            {
                if (a.Stage == b.Stage && a.Level == b.Level)
                {
                    Assert.True(b.Loss < a.Loss, $"basin {basin.Id}: {a} then {b}");
                }
            }
        }
    }

    [Fact]
    public void Progress_CarriesTheTraceSoFar()
    {
        (SearchOutcome outcome, List<SearchProgress> reports) = SmallSearch();
        IReadOnlyList<SearchTracePoint> final = outcome.Summary.Trace;
        Assert.NotEmpty(reports);
        int previous = 0;
        foreach (SearchProgress report in reports)
        {
            Assert.True(report.Trace.Count >= previous);
            Assert.Equal(final.Take(report.Trace.Count), report.Trace);
            previous = report.Trace.Count;
        }

        Assert.Contains(reports, static r => r.Trace.Count > 0);
    }

    /// <summary>Reports at once, on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SynchronousProgress(Action<SearchProgress> report) : IProgress<SearchProgress>
    {
        public void Report(SearchProgress value) => report(value);
    }
}
