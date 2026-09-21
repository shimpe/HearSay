using HearSay.Analysis;
using HearSay.Objective;
using HearSay.OperatorMatrix.Basins;
using HearSay.OperatorMatrix.Parameters;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.IntegrationTests;

public sealed class ConvergenceProbe(ITestOutputHelper output)
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
        RenderRequest request = RenderRequest.Create(220, 1.2, 0.4);
        RenderedAudio audio = renderer.Render(known, request);
        var objective = new AudioObjective();
        PreparedObjectiveTarget prepared = objective.Prepare(new AudioTarget(audio.Samples, audio.SampleRate, 220.0));
        TargetAnalysis a = prepared.Analysis;
        output.WriteLine($"noteOff {a.NoteOffSeconds:F3} dur {a.DurationSeconds:F3} attack {a.Envelope.AttackMs} decay two={a.AmplitudeDecay.IsTwoStage} fast {a.AmplitudeDecay.FastRateDbPerSecond:F1} slow {a.AmplitudeDecay.SlowRateDbPerSecond:F1} break {a.AmplitudeDecay.BreakSeconds:F2} rel {a.Envelope.ReleaseMs}");
        var options = new PatchMatchOptions { EvaluationBudget = 100000, MaxParallelEvaluations = 16, StagnationGenerations = 30 };
        var req2 = new RenderRequest(220, null, 1.0, TimeSpan.FromSeconds(a.NoteOffSeconds), TimeSpan.FromSeconds(a.DurationSeconds - a.NoteOffSeconds), 48000);
        var evaluator = new PatchEvaluator(renderer, objective, prepared, req2, 100000, 0.02, null, 16);
        output.WriteLine($"known patch loss: {evaluator.Evaluate(known, EvaluationLevel.Normal)!.AudioLoss:F4}  terms {evaluator.Evaluate(known, EvaluationLevel.Normal)!.Terms}");
        var exact = new PatchEvaluator(renderer, objective, prepared, request, 100, 0.02);
        output.WriteLine($"known patch, exact gate: {exact.Evaluate(known, EvaluationLevel.Normal)!.AudioLoss:F4}");

        PatchHypothesis h = new SinePhaseModulationPriorProvider().Generate(a, SynthesisProfiles.SuperColliderFm7.Capabilities).First(x => x.Origin == "stack 1:2");
        output.WriteLine(h.Patch.Operators[0].AmplitudeEnvelope.ToString() + string.Join(",", h.Patch.Operators[0].AmplitudeEnvelope.Points));
        output.WriteLine(string.Join(",", h.Patch.Operators[1].AmplitudeEnvelope.Points) + " amt " + h.Patch.ModulationMatrix[0, 1]);
        SearchBasin basin = new SearchBasin(1, h.Origin, evaluator.Evaluate(h.Patch, EvaluationLevel.Normal)!);
        output.WriteLine($"start {basin.Best.AudioLoss:F4} {basin.Best.Terms}");
        var optimizer = new BasinOptimizer(evaluator, SynthesisProfiles.SuperColliderFm7.Capabilities, options);
        PatchParameterSchema schema = PatchParameterSchema.Build(h.Patch, SynthesisProfiles.SuperColliderFm7.Capabilities);
        for (int round = 0; round < 6; round++)
        {
            basin = new SearchBasin(1, h.Origin, evaluator.Evaluate(h.Patch, EvaluationLevel.Normal)!);
            var all = new List<PatchParameter>();
            foreach (ContinuousStage st in new[] { ContinuousStage.Spectral, ContinuousStage.TemporalDetail })
            {
                foreach (ActiveParameterSet s in optimizer.SelectSubsets(basin.Best.Patch, st, false))
                {
                    all.AddRange(s.Parameters);
                }
            }

            int before = evaluator.Used;
            optimizer.Optimize(basin, new ActiveParameterSet(all), 2500, 0.2, 100 + round);
            output.WriteLine($"round {round}: n={all.Count} used {evaluator.Used - before} loss {basin.Best.AudioLoss:F4} {basin.Best.Terms}");
        }

        output.WriteLine(string.Join(",", basin.Best.Patch.Operators[0].AmplitudeEnvelope.Points));
        output.WriteLine(string.Join(",", basin.Best.Patch.Operators[1].AmplitudeEnvelope.Points) + " amt " + basin.Best.Patch.ModulationMatrix[0, 1] + " out " + basin.Best.Patch.OutputLevels[0]);
    }
}
