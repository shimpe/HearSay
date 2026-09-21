using HearSay.Analysis;
using HearSay.Envelope;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.IntegrationTests;

public sealed class DecayProbe(ITestOutputHelper output)
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
        TargetAnalysis a = new TargetAnalyzer().Analyze(new AudioTarget(audio.Samples, audio.SampleRate, 220.0));
        EnvelopeProfile e = a.Envelope;
        output.WriteLine($"hop {e.HopMs} lead {e.LeadInMs} peakMs {e.PeakMs} n {e.LevelsDb.Length} relStart {e.ReleaseStartMs}");
        for (int ms = 0; ms < 1300; ms += 50)
        {
            int i = (int)((ms + e.LeadInMs) / e.HopMs);
            if (i < e.LevelsDb.Length) output.WriteLine($"  t={ms} ms: {e.LevelsDb[i]:F2} dB");
        }
        output.WriteLine(a.AmplitudeDecay.ToString());
    }
}
