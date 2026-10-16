using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.SuperCollider.Tests;

/// <summary>Operator filters in the FMX6-compatible renderer.</summary>
public sealed class Fmx6FilterTests
{
    private static readonly Fmx6CompatibleOperatorMatrixRenderer Renderer = new();
    private const int Settled = 9600;

    internal static OperatorMatrixPatch Saw(OperatorFilter? filter, double ratio = 1) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Saw, HarmonicLimit = 64, Filter = filter })
            .WithOutputLevel(0, 1.0);

    private static float[] Render(OperatorMatrixPatch patch, double hz = 100) =>
        Renderer.Render(patch, new RenderRequest(hz, null, 1.0, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.05), 48000)).Samples;

    private static double Centroid(float[] samples, int from, int count)
    {
        var spectrogram = new HearSay.Spectral.StftAnalyzer().Analyze(samples.AsSpan(from, count).ToArray(), 48000, 2048, 512);
        double weighted = 0, total = 0;
        for (int f = 0; f < spectrogram.FrameCount; f++)
        {
            ReadOnlySpan<float> frame = spectrogram.Frame(f);
            for (int k = 1; k < spectrogram.BinCount; k++)
            {
                double power = (double)frame[k] * frame[k];
                weighted += power * k * spectrogram.BinHz;
                total += power;
            }
        }

        return weighted / total;
    }

    [Fact]
    public void NoFilter_RendersExactlyAsBefore()
    {
        OperatorMatrixPatch plain = Saw(null);
        float[] expected = Render(plain);
        Assert.Equal(expected, Render(plain with { }));
        Assert.Null(HearSay.Synthesis.Compilation.PatchCompiler.Compile(plain, RenderRequest.Create(100, 0.4, 0.05)).Filters);
    }

    [Fact]
    public void LowPass_DarkensTheSaw_FourPolesMore()
    {
        double open = Centroid(Render(Saw(null)), Settled, 4800);
        double two = Centroid(Render(Saw(OperatorFilter.LowPass(4))), Settled, 4800);
        double four = Centroid(Render(Saw(OperatorFilter.LowPass(4, poles: 4))), Settled, 4800);
        Assert.True(two < open * 0.7 && four < two, $"open {open:F0}, 2 poles {two:F0}, 4 poles {four:F0}");
    }

    [Fact]
    public void FilteredModulator_ChangesTheModulation()
    {
        OperatorMatrixPatch Fm(OperatorFilter? filter) =>
            OperatorMatrixPatch.Silent(6)
                .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()))
                .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Saw, Filter = filter })
                .WithOutputLevel(0, 1.0)
                .WithEdge(new ModulationEdge(1, 0, true, 2.0));
        double plain = Centroid(Render(Fm(null)), Settled, 4800);
        double soft = Centroid(Render(Fm(OperatorFilter.LowPass(1.5, poles: 4))), Settled, 4800);
        Assert.True(soft < plain * 0.8, $"plain {plain:F0}, low-passed modulator {soft:F0}");
    }

    [Fact]
    public void Sweep_StartsBrightAndCloses()
    {
        var sweep = new OperatorEnvelope([new EnvelopePoint(0, 1), new EnvelopePoint(0.15, 0), new EnvelopePoint(0.05, 0)], SustainPoint: 1, ReleasePoint: 2);
        float[] samples = Render(Saw(OperatorFilter.LowPass(2, poles: 4) with { EnvelopeOctaves = 3, Envelope = sweep }));
        double early = Centroid(samples, 0, 2400);
        double late = Centroid(samples, 12000, 4800);
        Assert.True(early > late * 1.5, $"early {early:F0}, late {late:F0}");
    }

    [Fact]
    public void ExtremeSettings_StayFinite()
    {
        // Review focus 1: a cutoff far above Nyquist on a high note is clamped, not NaN.
        Assert.All(Render(Saw(OperatorFilter.LowPass(64, q: 20, poles: 4)), hz: 2000), static s => Assert.True(float.IsFinite(s)));

        // Review focus 2: a resonant filter inside a self-feedback loop stays bounded.
        OperatorMatrixPatch loop = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Filter = OperatorFilter.BandPass(3, 20) })
            .WithOutputLevel(0, 1.0)
            .WithEdge(new ModulationEdge(0, 0, true, 3.0));
        Assert.All(Render(loop), static s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 50f));
    }
}
