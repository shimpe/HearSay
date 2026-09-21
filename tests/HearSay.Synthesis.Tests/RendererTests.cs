using HearSay.Synthesis.Envelopes;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Tests;

public sealed class RendererTests
{
    private const int SampleRate = 48000;
    private static readonly Fm7CompatibleOperatorMatrixRenderer Renderer = new();
    private static readonly RenderRequest Note = RenderRequest.Create(220.0, 0.25, 0.05);

    [Fact]
    public void ASingleOperator_IsASineThatStartsOneSampleIntoItsCycle()
    {
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), PatchModelTests.Flat))
            .WithOutputLevel(0, 1.0);

        float[] samples = Renderer.Render(patch, Note).Samples;

        for (int n = 0; n < 4800; n++)
        {
            double expected = Math.Sin(2.0 * Math.PI * 220.0 * (n + 1) / SampleRate);
            Assert.InRange(samples[n] - expected, -2e-4, 2e-4);
        }
    }

    [Fact]
    public void Modulation_UsesTheOperatorOutputsOfThePreviousSample()
    {
        // OP2 -> OP1, OP1 -> OP2 (a loop through two operators) and OP1 -> OP1 (self-feedback).
        const double into1From2 = 1.7;
        const double into2From1 = 0.6;
        const double self1 = 0.8;
        OperatorMatrixPatch patch = PatchModelTests.TwoOperator(into1From2)
            .WithEdge(new ModulationEdge(0, 1, true, into2From1))
            .WithEdge(new ModulationEdge(0, 0, true, self1));

        float[] samples = Renderer.Render(patch, Note).Samples;

        // Reference recurrence: all phase deviations of sample n come from the outputs of sample n - 1.
        double w1 = 2.0 * Math.PI * 220.0 / SampleRate;
        double w2 = 2.0 * Math.PI * 440.0 / SampleRate;
        double out1 = 0;
        double out2 = 0;
        for (int n = 0; n < 2400; n++)
        {
            double next1 = Math.Sin((w1 * (n + 1)) + (into1From2 * out2) + (self1 * out1));
            double next2 = Math.Sin((w2 * (n + 1)) + (into2From1 * out1));
            out1 = next1;
            out2 = next2;
            Assert.InRange(samples[n] - out1, -2e-3, 2e-3);
        }
    }

    [Fact]
    public void SameSampleEvaluation_WouldGiveADifferentSignal()
    {
        // Guards the test above: evaluating the modulator first and using its output in the same sample is audibly
        // different from the one-sample delay of the backend.
        OperatorMatrixPatch patch = PatchModelTests.TwoOperator(3.0);
        float[] samples = Renderer.Render(patch, RenderRequest.Create(2000.0, 0.05, 0.0)).Samples;

        double w1 = 2.0 * Math.PI * 2000.0 / SampleRate;
        double worst = 0;
        for (int n = 0; n < 2000; n++)
        {
            double sameSample = Math.Sin((w1 * (n + 1)) + (3.0 * Math.Sin(2 * w1 * (n + 1))));
            worst = Math.Max(worst, Math.Abs(samples[n] - sameSample));
        }

        Assert.True(worst > 0.2, $"worst difference {worst}");
    }

    [Fact]
    public void Rendering_IsDeterministic()
    {
        OperatorMatrixPatch patch = new RandomPatchGenerator(SynthesisProfiles.SuperColliderFm7.Capabilities).Generate(3);

        float[] a = Renderer.Render(patch, Note).Samples;
        float[] b = Renderer.Render(patch, Note).Samples;

        Assert.Equal(a, b);
        Assert.Equal(Note.TotalSamples, a.Length);
    }

    [Fact]
    public void FixedOperators_DoNotTransposeAndRatioOperatorsDo()
    {
        OperatorMatrixPatch fixedPatch = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtFixed(300.0), PatchModelTests.Flat))
            .WithOutputLevel(0, 1.0);
        OperatorMatrixPatch ratioPatch = fixedPatch.WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), PatchModelTests.Flat));

        Assert.Equal(
            Renderer.Render(fixedPatch, RenderRequest.Create(110, 0.1, 0)).Samples,
            Renderer.Render(fixedPatch, RenderRequest.Create(440, 0.1, 0)).Samples);
        Assert.NotEqual(
            Renderer.Render(ratioPatch, RenderRequest.Create(110, 0.1, 0)).Samples,
            Renderer.Render(ratioPatch, RenderRequest.Create(440, 0.1, 0)).Samples);
    }

    [Fact]
    public void OutputLevels_MixOperatorsIndependently()
    {
        OperatorMatrixPatch first = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), PatchModelTests.Flat))
            .WithOutputLevel(0, 0.5);
        OperatorMatrixPatch second = OperatorMatrixPatch.Silent(6)
            .WithOperator(3, OperatorPatch.Sine(OperatorFrequency.AtRatio(3), PatchModelTests.Flat))
            .WithOutputLevel(3, 0.25);
        OperatorMatrixPatch both = first
            .WithOperator(3, second.Operators[3])
            .WithOutputLevel(3, 0.25);

        float[] a = Renderer.Render(first, Note).Samples;
        float[] b = Renderer.Render(second, Note).Samples;
        float[] mix = Renderer.Render(both, Note).Samples;

        for (int n = 0; n < mix.Length; n++)
        {
            Assert.InRange(mix[n] - (a[n] + b[n]), -1e-6, 1e-6);
        }
    }

    [Fact]
    public void InaudibleOperatorsAndDisabledEdges_DoNotChangeTheSound()
    {
        OperatorMatrixPatch patch = PatchModelTests.TwoOperator();
        OperatorMatrixPatch cluttered = patch
            .WithOperator(4, OperatorPatch.Sine(OperatorFrequency.AtRatio(7), PatchModelTests.Flat))
            .WithEdge(new ModulationEdge(0, 4, true, 3.0))
            .WithEdge(new ModulationEdge(1, 1, false, 5.0));

        Assert.Equal(Renderer.Render(patch, Note).Samples, Renderer.Render(cluttered, Note).Samples);
    }

    [Fact]
    public void AmplitudeEnvelopeOfAModulator_ShapesTheModulationItSends()
    {
        OperatorMatrixPatch bright = PatchModelTests.TwoOperator(3.0);
        OperatorMatrixPatch fading = bright.WithOperator(1, bright.Operators[1] with
        {
            AmplitudeEnvelope = new OperatorEnvelope(
                [new EnvelopePoint(0, 1), new EnvelopePoint(0.1, 0.00001, EnvelopeCurve.Exponential), new EnvelopePoint(0.01, 0.00001)],
                SustainPoint: 1,
                ReleasePoint: 2),
        });

        float[] samples = Renderer.Render(fading, Note).Samples;

        // Once the modulator has faded, OP1 is a plain sine again.
        double w = 2.0 * Math.PI * 220.0 / SampleRate;
        for (int n = 9600; n < 11000; n++)
        {
            Assert.InRange(samples[n] - Math.Sin(w * (n + 1)), -2e-3, 2e-3);
        }
    }

    [Fact]
    public void VelocityAndKeyScaling_ChangeTheLevel()
    {
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), PatchModelTests.Flat) with
            {
                VelocitySensitivity = 1.0,
                KeyScaling = new KeyScaling(LevelDbPerOctave: -6.0, ReferenceMidiNote: 57),
            })
            .WithOutputLevel(0, 1.0);

        float loud = Renderer.Render(patch, Note).Peak;
        float soft = Renderer.Render(patch, Note with { Velocity = 0.5 }).Peak;
        float high = Renderer.Render(patch, Note with { FrequencyHz = 440.0 }).Peak;

        Assert.InRange(soft / loud, 0.49, 0.51);
        Assert.InRange(high / loud, 0.49, 0.52);
    }

    [Fact]
    public void ClosingTheGate_StartsTheReleaseAndTheNoteEndsSilent()
    {
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.05, 0.7, 0.05)))
            .WithOutputLevel(0, 1.0);

        float[] samples = Renderer.Render(patch, RenderRequest.Create(220, 0.2, 0.2)).Samples;

        Assert.True(Peak(samples, 8000, 9600) > 0.6);
        Assert.True(Peak(samples, 9600 + 4800, samples.Length) < 1e-3);
    }

    [Fact]
    public void ControlRateEnvelope_FollowsSegmentsHoldsAndReleasesEarly()
    {
        const double controlRate = 750;
        var envelope = new OperatorEnvelope(
            [new EnvelopePoint(0, 0), new EnvelopePoint(0.1, 1), new EnvelopePoint(0.1, 0.5), new EnvelopePoint(0.1, 0)],
            SustainPoint: 2,
            ReleasePoint: 3);

        var runner = new ControlRateEnvelope(envelope, controlRate);
        var values = new double[300];
        for (int b = 0; b < values.Length; b++)
        {
            values[b] = runner.Next(b < 200);
        }

        Assert.Equal(0.0, values[0], 9);
        Assert.InRange(values[37], 0.48, 0.52);
        Assert.Equal(1.0, values[75], 2);
        Assert.Equal(0.5, values[150], 9);
        Assert.Equal(0.5, values[199], 9);
        Assert.Equal(0.25, values[237], 2);
        Assert.Equal(0.0, values[290], 9);

        // Gate closes during the attack: the release starts from the level reached so far.
        var early = new ControlRateEnvelope(envelope, controlRate);
        double last = 0;
        for (int b = 0; b < 38; b++)
        {
            last = early.Next(true);
        }

        double afterRelease = early.Next(false);
        Assert.InRange(last, 0.45, 0.55);
        Assert.InRange(afterRelease, last - 0.02, last + 0.02);
        for (int b = 0; b < 80; b++)
        {
            afterRelease = early.Next(false);
        }

        Assert.Equal(0.0, afterRelease, 9);
    }

    [Fact]
    public void Fm7Renderer_RejectsWhatFm7CannotPlay()
    {
        OperatorMatrixPatch patch = PatchModelTests.TwoOperator();
        patch = patch.WithOperator(0, patch.Operators[0] with { Waveform = OperatorWaveform.Triangle });

        var error = Assert.Throws<PatchNotSupportedException>(() => Renderer.Render(patch, Note));

        Assert.Contains(error.Result.Issues, static i => i.Code == PatchCompatibilityCodes.UnsupportedWaveform);
        Assert.Throws<ArgumentException>(() => new Fm7CompatibleOperatorMatrixRenderer(SynthesisProfiles.ExperimentalSineTriangle));
    }

    [Fact]
    public void ExtendedRenderer_PlaysTriangleOperatorsAndAgreesWithFm7OnSines()
    {
        var extended = new ExtendedOperatorMatrixRenderer(SynthesisProfiles.ExperimentalSineTriangle, oversampling: 1);
        OperatorMatrixPatch sine = PatchModelTests.TwoOperator(1.5);
        OperatorMatrixPatch triangle = sine.WithOperator(0, sine.Operators[0] with { Waveform = OperatorWaveform.Triangle });

        float[] reference = Renderer.Render(sine, Note).Samples;
        float[] sineExtended = extended.Render(sine, Note).Samples;
        float[] triangleExtended = extended.Render(triangle, Note).Samples;

        // The extended renderer starts at phase zero, the backend one sample later.
        double sineError = 0;
        double triangleDifference = 0;
        for (int n = 0; n < 4000; n++)
        {
            sineError = Math.Max(sineError, Math.Abs(sineExtended[n + 1] - reference[n]));
            triangleDifference = Math.Max(triangleDifference, Math.Abs(triangleExtended[n] - sineExtended[n]));
        }

        Assert.True(sineError < 5e-3, $"sine error {sineError}");
        Assert.True(triangleDifference > 0.1, $"triangle difference {triangleDifference}");
        Assert.Throws<ArgumentException>(() => new ExtendedOperatorMatrixRenderer(SynthesisProfiles.HearSayExtended));
    }

    private static float Peak(float[] samples, int from, int to)
    {
        float peak = 0;
        for (int i = from; i < to; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }

        return peak;
    }
}
