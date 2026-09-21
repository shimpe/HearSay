using HearSay.SuperCollider.Validation;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.SuperCollider.Tests;

public sealed class ProbeTests(ITestOutputHelper output)
{
    [SuperColliderFact]
    public void Probe()
    {
        var sc = new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!);
        var native = new Fm7CompatibleOperatorMatrixRenderer();
        var request = RenderRequest.Create(220.0, 0.5, 0.3);

        OperatorMatrixPatch simple = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.2)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.002, 0.1, 0.3, 0.1)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 2.5));

        OperatorMatrixPatch feedback = simple
            .WithEdge(new ModulationEdge(0, 0, true, 0.9))
            .WithEdge(new ModulationEdge(0, 1, true, 0.4));

        OperatorMatrixPatch dynamic = simple
            .WithEdge(new ModulationEdge(1, 0, true, 4.0, OperatorEnvelope.TwoStageDecay(0.001, 0.05, 0.4, 0.3, 0.1, 0.1)))
            with
            {
                Pitch = new PitchModel(
                    new OperatorEnvelope([new EnvelopePoint(0, 40), new EnvelopePoint(0.08, 0)], null, 2),
                    new LfoSettings(5.5, 12, 0.1, 0.2)),
            };

        var jobs = new[] { (simple, request), (feedback, request), (dynamic, request) };
        IReadOnlyList<RenderedAudio> scAudio = sc.RenderMany(jobs);
        for (int j = 0; j < jobs.Length; j++)
        {
            float[] a = native.Render(jobs[j].Item1, request).Samples;
            float[] b = scAudio[j].Samples;
            for (int lag = -2; lag <= 2; lag++)
            {
                double max = 0;
                double peak = 0;
                int at = 0;
                for (int i = 2; i < a.Length - 2; i++)
                {
                    double d = Math.Abs(a[i] - b[i + lag]);
                    peak = Math.Max(peak, Math.Abs(b[i]));
                    if (d > max)
                    {
                        max = d;
                        at = i;
                    }
                }

                output.WriteLine($"job {j} lag {lag}: max diff {max:E3} at {at} (peak {peak:F3})");
            }

            output.WriteLine($"first native: {string.Join(" ", a.Take(6).Select(x => x.ToString("E3")))}");
            output.WriteLine($"first sc    : {string.Join(" ", b.Take(6).Select(x => x.ToString("E3")))}");
        }
    }
}
