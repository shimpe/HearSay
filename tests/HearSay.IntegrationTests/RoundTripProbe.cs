using HearSay.Analysis;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.IntegrationTests;

public sealed class RoundTripProbe(ITestOutputHelper output)
{
    [Fact]
    public void Probe()
    {
        var renderer = new Fm7CompatibleOperatorMatrixRenderer();
        OperatorMatrixPatch known = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.TwoStageDecay(0.003, 0.3, 0.4, 1.2, 0.1, 0.2)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.TwoStageDecay(0.001, 0.15, 0.3, 1.0, 0.05, 0.1)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 3.2));
        RenderedAudio audio = renderer.Render(known, RenderRequest.Create(220, 1.2, 0.4));
        var coordinator = new OperatorMatrixPatchSearchCoordinator(renderer, [new SinePhaseModulationPriorProvider()]);
        SearchOutcome outcome = coordinator.Search(new AudioTarget(audio.Samples, audio.SampleRate, 220.0), new PatchMatchOptions { EvaluationBudget = 20000 });
        foreach (SearchStageSummary s in outcome.Summary.Stages)
        {
            output.WriteLine($"{s.Name}: {s.Evaluations} evals, best {s.BestLoss:F4} ({s.Level}) {s.Notes}");
        }

        output.WriteLine($"initial {outcome.Summary.InitialAudioLoss:F4} final {outcome.Summary.FinalAudioLoss:F4} used {outcome.Summary.EvaluationsUsed} cache {outcome.Summary.CacheHits} time {outcome.Summary.Elapsed}");
        foreach (PatchCandidateResult c in outcome.Candidates)
        {
            output.WriteLine($"{c.Loss:F4} audio {c.AudioLoss:F4} cx {c.Complexity:F2} | {c.Regime} | {c.Origin}");
            output.WriteLine($"   terms {c.Terms}");
        }

        foreach (BasinSummary b in outcome.Summary.Basins.Take(12))
        {
            output.WriteLine($"basin {b.Id}: {b.BestLoss:F4} evals {b.Evaluations} restarts {b.Restarts} {b.Fate} | {b.Origin}");
        }
    }
}
