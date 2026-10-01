using HearSay.SuperCollider.FM7;
using HearSay.SuperCollider.FMX6;
using HearSay.SuperCollider.Nome6;
using HearSay.SuperCollider.Validation;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Nome6Compatible.Rendering;
using HearSay.Synthesis.Oscillators;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.SuperCollider.Tests;

/// <summary>The Nome6-compatible renderer and exporter, and their agreement with the real Nome6 UGen.</summary>
public sealed class Nome6Tests(ITestOutputHelper output)
{
    private static readonly SynthesisCapabilities Nome6 = SynthesisProfiles.SuperColliderNome6.Capabilities;
    private static readonly Nome6CompatibleOperatorMatrixRenderer Renderer = new();

    /// <summary>The profile without its noise operators: the UGen seeds its noise per synth, so only tonal patches can match sample by sample.</summary>
    private static readonly SynthesisCapabilities Tonal = Nome6 with { SupportedWaveforms = new HashSet<OperatorWaveform> { OperatorWaveform.Sine, OperatorWaveform.Elliptic } };

    /// <summary>Samples into a render after which the constant envelope has reached its level; a whole number of 100 Hz cycles.</summary>
    private const int Settled = 9600;

    private static OperatorMatrixPatch Single(OperatorWaveform waveform, double m = 0.0, double morph = 0.0, double ratio = 1) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), OperatorEnvelope.Constant()) with { Waveform = waveform, Shape = waveform == OperatorWaveform.Elliptic ? m : 0.5, Morph = morph })
            .WithOutputLevel(0, 1.0);

    private static float[] Render(OperatorMatrixPatch patch, double hz = 100, int sampleRate = 48000) =>
        Renderer.Render(patch, new RenderRequest(hz, null, 1.0, TimeSpan.FromSeconds(0.25), TimeSpan.Zero, sampleRate)).Samples;

    /// <summary>Amplitude of harmonic <paramref name="n"/> of 100 Hz over the settled part, by correlation.</summary>
    private static double Harmonic(float[] samples, int n)
    {
        double re = 0, im = 0;
        int count = 2400;
        for (int k = Settled; k < Settled + count; k++)
        {
            double phase = 2.0 * Math.PI * n * 100.0 * k / 48000.0;
            re += samples[k] * Math.Cos(phase);
            im += samples[k] * Math.Sin(phase);
        }

        return 2.0 * Math.Sqrt((re * re) + (im * im)) / count;
    }

    [Fact]
    public void SineAndEllipticAtZero_AreTheSameSine()
    {
        float[] sine = Render(Single(OperatorWaveform.Sine));
        float[] zero = Render(Single(OperatorWaveform.Elliptic, m: 0.0));
        Assert.Equal(sine, zero);
        Assert.InRange(Harmonic(sine, 1), 0.99, 1.01);
        Assert.True(Harmonic(sine, 3) < 1e-4);
    }

    [Fact]
    public void Elliptic_HasOddHarmonicsFallingByTheNome()
    {
        double m = 0.999;
        double q = JacobiNome.FromParameter(m);
        float[] samples = Render(Single(OperatorWaveform.Elliptic, m));
        double h1 = Harmonic(samples, 1), h3 = Harmonic(samples, 3), h5 = Harmonic(samples, 5);
        output.WriteLine($"q {q:F4}: h3/h1 {h3 / h1:F4}, h5/h3 {h5 / h3:F4}, h2 {Harmonic(samples, 2):E2}");
        // Harmonic 2n + 1 has the level q^(n + 1/2) / (1 - q^(2n + 1)).
        double r31 = q * (1 - q) / (1 - (q * q * q));
        double r53 = q * (1 - (q * q * q)) / (1 - Math.Pow(q, 5));
        Assert.InRange(h3 / h1, r31 * 0.99, r31 * 1.01);
        Assert.InRange(h5 / h3, r53 * 0.99, r53 * 1.01);
        Assert.True(Harmonic(samples, 2) < 1e-3 * h1 && Harmonic(samples, 4) < 1e-3 * h1);

        // Peak-normalized: sn peaks at one, as does sd; the harmonics of sd alternate in sign and fall as q (1 + q) / (1 + q^3).
        Assert.InRange(samples.Skip(Settled).Max(), 0.98, 1.02);
        float[] sd = Render(Single(OperatorWaveform.Elliptic, m, morph: 1.0));
        Assert.InRange(sd.Skip(Settled).Max(), 0.98, 1.02);
        double sd31 = q * (1 + q) / (1 + (q * q * q));
        Assert.InRange(Harmonic(sd, 3) / Harmonic(sd, 1), sd31 * 0.99, sd31 * 1.01);
        Assert.True(sd.Zip(samples).Skip(Settled).Max(static p => Math.Abs(p.First - p.Second)) > 0.1, "sn and sd should be different waveforms");
    }

    [Fact]
    public void Nome_RoundTripsTheParameter()
    {
        foreach (double m in new[] { 0.0, 0.1, 0.5, 0.9, 0.99, 0.9999, OperatorPatch.MaxEllipticM })
        {
            double q = JacobiNome.FromParameter(m);
            Assert.InRange(JacobiNome.ToParameter(q), m - 1e-9, m + 1e-9);
        }

        Assert.InRange(JacobiNome.FromParameter(OperatorPatch.DefaultEllipticM), 0.26, 0.265);
    }

    [Fact]
    public void Exporter_WritesTheEllipticParametersPerRow()
    {
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.3)) with { Waveform = OperatorWaveform.Elliptic, Shape = 0.99, Morph = 0.25 })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.3)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 1.5));

        string scd = new SuperColliderNome6Exporter().Export(patch, new PatchExportOptions { InstrumentName = "Nome6Test" });
        output.WriteLine(scd);

        Assert.Contains("Needs SuperCollider with the Nome6 plugin", scd);
        Assert.Contains("var m = [0.99, 0.0, 0, 0, 0, 0][i];", scd);
        Assert.Contains("var family = [0.25, 0.0, 0, 0, 0, 0][i];", scd);
        Assert.Contains("var noise = [0, 0, 0, 0, 0, 0][i];", scd);
        Assert.Contains("modMatrix, 2,", scd);
        Assert.Contains("noiseColor: 0.0,", scd);
        Assert.Contains("noiseCutoff: 8000.0);", scd);
        Assert.DoesNotContain("FM7", scd);

        // A noise operator moves its amplitude into the noise field; roughness and colour are written as given.
        OperatorMatrixPatch noisy = patch
            .WithOperator(0, patch.Operators[0] with { NoiseM = -0.2 })
            .WithOperator(1, patch.Operators[1] with { NoisePhase = 0.5 })
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.001, 0.05, 0, 0.05)) with { Waveform = OperatorWaveform.Noise })
            .WithOutputLevel(2, 0.3)
            with { Noise = new SharedNoise(0.5, 3000) };
        string noisyScd = new SuperColliderNome6Exporter().Export(noisy);
        Assert.Contains("var noise = [0, 0, 1, 0, 0, 0][i];", noisyScd);
        Assert.Contains("var phaseNoise = [0.0, 0.5, 0.0, 0, 0, 0][i];", noisyScd);
        Assert.Contains("var mNoise = [-0.2, 0.0, 0.0, 0, 0, 0][i];", noisyScd);
        Assert.Contains("if(noise > 0) { [row[0], row[1], 0, 0, 0, 0, row[2], 0, 0] }", noisyScd);
        Assert.Contains("noiseColor: 0.5,", noisyScd);
        Assert.Contains("noiseCutoff: 3000.0);", noisyScd);
        Assert.Throws<PatchNotSupportedException>(() => new SuperColliderFmx6Exporter().Export(OperatorMatrixPatch.Silent(6).WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { NoisePhase = 0.5 }).WithOutputLevel(0, 1.0)));

        // FM7 and FMX6 cannot play an elliptic operator and say so, and m stays below one.
        Assert.Throws<PatchNotSupportedException>(() => new SuperColliderFm7Exporter().Export(patch));
        Assert.Throws<PatchNotSupportedException>(() => new SuperColliderFmx6Exporter().Export(patch));
        Assert.NotEmpty((patch.WithOperator(0, patch.Operators[0] with { Shape = 1.0 })).Validate());
    }

    [SuperColliderFact]
    public void Basics_AreSampleExactInSuperCollider()
    {
        var validator = new BackendEquivalenceValidator(
            Renderer,
            new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new SuperColliderNome6Exporter(), Nome6));
        var request = new RenderRequest(220, null, 1.0, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.1), 48000);
        OperatorMatrixPatch modulated = Single(OperatorWaveform.Elliptic, 0.99, 0.5)
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1)) with { Waveform = OperatorWaveform.Elliptic, Shape = 0.9999, Morph = 1.0 })
            .WithEdge(new ModulationEdge(1, 0, true, 1.2))
            .WithEdge(new ModulationEdge(0, 0, true, 0.3));
        (string Name, OperatorMatrixPatch Patch)[] patches =
        [
            ("sine", Single(OperatorWaveform.Sine)),
            ("elliptic sn", Single(OperatorWaveform.Elliptic, 0.999)),
            ("elliptic sd", Single(OperatorWaveform.Elliptic, 0.999999, 1.0)),
            ("modulated with feedback", modulated),
        ];

        IReadOnlyList<BackendExportValidation> results = validator.Validate([.. patches.Select(p => (p.Patch, request))]);
        for (int i = 0; i < results.Count; i++)
        {
            BackendExportValidation r = results[i];
            output.WriteLine($"{patches[i].Name,-24} max sample diff {r.MaxSampleDifference:E2} (peak {r.PeakLevel:F2}), overall {r.OverallDistance:F5}");
        }

        Assert.All(results, static r => Assert.True(r.MaxSampleDifference < 1e-4 * Math.Max(0.05, r.PeakLevel), r.ToString()));
    }

    [SuperColliderFact]
    public void RandomPatches_SoundTheSameInSuperCollider()
    {
        var validator = new BackendEquivalenceValidator(
            Renderer,
            new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new SuperColliderNome6Exporter(), Nome6));
        (string Name, RandomPatchOptions Options)[] families =
        [
            ("sparse", new RandomPatchOptions { EdgeDensity = 0.1, SelfFeedbackProbability = 0, AllowCircularFeedback = false }),
            ("dense", new RandomPatchOptions { EdgeDensity = 0.6, MaxEdgeAmount = 1.5 }),
            ("self-feedback", new RandomPatchOptions { SelfFeedbackProbability = 0.9, AllowCircularFeedback = false }),
            ("fixed-and-detuned", new RandomPatchOptions { FixedFrequencyProbability = 0.5, DetuneProbability = 0.9 }),
            ("moving", new RandomPatchOptions { EdgeEnvelopeProbability = 0.9, PitchModulationProbability = 1.0 }),
        ];

        var jobs = new List<(OperatorMatrixPatch, RenderRequest)>();
        var names = new List<string>();
        int elliptic = 0;
        foreach ((string name, RandomPatchOptions options) in families)
        {
            var generator = new RandomPatchGenerator(Tonal, options);
            for (int seed = 0; seed < 5; seed++)
            {
                OperatorMatrixPatch patch = generator.Generate(seed);
                bool[] audible = patch.FindAudibleOperators();
                elliptic += Enumerable.Range(0, patch.OperatorCount).Count(i => audible[i] && patch.Operators[i].Waveform == OperatorWaveform.Elliptic);
                double frequency = 110.0 * Math.Pow(2.0, seed / 2.0);
                jobs.Add((patch, new RenderRequest(frequency, null, 0.4 + (0.15 * seed), TimeSpan.FromSeconds(0.5 + (0.0137 * seed)), TimeSpan.FromSeconds(0.25), 48000)));
                names.Add($"{name}/{seed}");
            }
        }

        IReadOnlyList<BackendExportValidation> results = validator.Validate(jobs);

        int sampleExact = 0;
        for (int i = 0; i < results.Count; i++)
        {
            BackendExportValidation r = results[i];
            output.WriteLine($"{names[i],-22} overall {r.OverallDistance:F5} spectral {r.SpectralDistance:F5} envelope {r.EnvelopeDistance:F5} partial {r.PartialDistance:F5} max sample diff {r.MaxSampleDifference:E2} (peak {r.PeakLevel:F2})");
            if (r.MaxSampleDifference < 1e-3 * Math.Max(0.05, r.PeakLevel))
            {
                sampleExact++;
            }
        }

        output.WriteLine($"{sampleExact} of {results.Count} patches match sample by sample; {elliptic} audible elliptic operators");
        Assert.True(elliptic >= 20, "the random patches should have many elliptic operators");
        Assert.True(sampleExact >= results.Count * 3 / 4, $"only {sampleExact} of {results.Count} are sample-exact");
        Assert.All(results, static r => Assert.True(r.OverallDistance < 0.1, r.ToString()));
        Assert.True(results.Count(static r => r.IsEquivalent) >= results.Count * 4 / 5, "too few patches are equivalent");
    }

    private static OperatorMatrixPatch NoiseOnly(double color, double cutoffHz) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Noise })
            .WithOutputLevel(2, 1.0)
            with { Noise = new SharedNoise(color, cutoffHz) };

    /// <summary>Power-weighted mean frequency of the settled part of a render.</summary>
    private static double Centroid(float[] samples)
    {
        var spectrogram = new HearSay.Spectral.StftAnalyzer().Analyze(samples.AsSpan(Settled).ToArray(), 48000, 2048, 512);
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
    public void SharedNoise_HasTheColourOfThePatch()
    {
        double low = Centroid(Render(NoiseOnly(-1.0, 1000)));
        double white = Centroid(Render(NoiseOnly(0.0, 1000)));
        double high = Centroid(Render(NoiseOnly(1.0, 1000)));
        output.WriteLine($"low {low:F0} Hz, white {white:F0} Hz, high {high:F0} Hz");
        Assert.True(low < 0.5 * white && high > white, $"low {low:F0}, white {white:F0}, high {high:F0}");

        // Deterministic here, unlike the UGen, which seeds its generator per synth.
        Assert.Equal(Render(NoiseOnly(0.3, 2000)), Render(NoiseOnly(0.3, 2000)));
        // The noise is clipped to one before decimation; the half-band filter may ring a little past it.
        Assert.All(Render(NoiseOnly(0.0, 8000)), static s => Assert.InRange(s, -1.3f, 1.3f));
    }

    [Fact]
    public void Roughness_SpreadsTheHarmonicsOfATone()
    {
        OperatorMatrixPatch clean = Single(OperatorWaveform.Elliptic, 0.999);
        OperatorMatrixPatch phase = clean.WithOperator(0, clean.Operators[0] with { NoisePhase = 2.0 });
        OperatorMatrixPatch spectral = clean.WithOperator(0, clean.Operators[0] with { NoiseM = 0.3 });
        static double Tonal(float[] s) => Harmonic(s, 1) * Harmonic(s, 1) / s.Skip(Settled).Take(2400).Average(static x => 2.0 * x * x);
        double cleanTonal = Tonal(Render(clean)), phaseTonal = Tonal(Render(phase));
        output.WriteLine($"share of the first harmonic: clean {cleanTonal:F3}, phase noise {phaseTonal:F3}");
        Assert.True(phaseTonal < cleanTonal - 0.05, "phase noise should move power away from the harmonics");

        // m noise moves the ellipticity between m - 0.3 and m + 0.3: the third harmonic flickers but stays on average.
        float[] rough = Render(spectral);
        Assert.NotEqual(Render(clean), rough);
        Assert.InRange(Harmonic(rough, 3) / Harmonic(rough, 1), 0.05, 0.4);
    }

    [SuperColliderFact]
    public void NoisePatches_SoundAlikeInSuperCollider()
    {
        var validator = new BackendEquivalenceValidator(
            Renderer,
            new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new SuperColliderNome6Exporter(), Nome6));
        var request = new RenderRequest(220, null, 1.0, TimeSpan.FromSeconds(0.6), TimeSpan.FromSeconds(0.1), 48000);
        OperatorMatrixPatch tone = Single(OperatorWaveform.Elliptic, 0.99);
        (string Name, OperatorMatrixPatch Patch)[] patches =
        [
            ("tone with noise burst", tone
                .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.001, 0.08, 0.0, 0.05)) with { Waveform = OperatorWaveform.Noise })
                .WithOutputLevel(1, 0.4) with { Noise = new SharedNoise(0.3, 3000) }),
            ("noise modulator", tone
                .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant(0.5)) with { Waveform = OperatorWaveform.Noise })
                .WithEdge(new ModulationEdge(1, 0, true, 0.6)) with { Noise = new SharedNoise(-0.5, 1500) }),
            ("phase roughness", tone.WithOperator(0, tone.Operators[0] with { NoisePhase = 0.4 }) with { Noise = new SharedNoise(-0.6, 600) }),
            ("m roughness", tone.WithOperator(0, tone.Operators[0] with { NoiseM = -0.2 })),
        ];

        IReadOnlyList<BackendExportValidation> results = validator.Validate([.. patches.Select(p => (p.Patch, request))]);
        for (int i = 0; i < results.Count; i++)
        {
            BackendExportValidation r = results[i];
            output.WriteLine($"{patches[i].Name,-24} overall {r.OverallDistance:F5} spectral {r.SpectralDistance:F5} envelope {r.EnvelopeDistance:F5} partial {r.PartialDistance:F5} (peak {r.PeakLevel:F2})");
        }

        // Another noise sequence, the same sound: the spectra and envelopes agree, the samples do not. Pure noise is
        // compared by level and colour in PureNoise_HasTheLevelAndColourOfSuperCollider.
        Assert.All(results, static r => Assert.True(r.OverallDistance < 0.05, r.ToString()));
    }

    [SuperColliderFact]
    public void PureNoise_HasTheLevelAndColourOfSuperCollider()
    {
        var backend = new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new SuperColliderNome6Exporter(), Nome6);
        // Long enough for the dark noise, whose centroid wanders most from one sequence to the next.
        var request = new RenderRequest(100, null, 1.0, TimeSpan.FromSeconds(1.5), TimeSpan.Zero, 48000);
        (double Color, double Cutoff)[] colours = [(-1.0, 300), (-0.8, 800), (0.0, 8000), (0.8, 4000), (1.0, 1000)];
        IReadOnlyList<RenderedAudio> real = backend.RenderMany([.. colours.Select(c => (NoiseOnly(c.Color, c.Cutoff), request))]);
        for (int i = 0; i < colours.Length; i++)
        {
            float[] ours = Renderer.Render(NoiseOnly(colours[i].Color, colours[i].Cutoff), request).Samples;
            float[] theirs = real[i].Samples;
            double Rms(float[] s) => Math.Sqrt(s.Skip(Settled).Average(static x => (double)x * x));
            double rmsRatio = Rms(ours) / Rms(theirs), centroidRatio = Centroid(ours) / Centroid(theirs);
            output.WriteLine($"colour {colours[i].Color,5:F1} cutoff {colours[i].Cutoff,5:F0}: rms {Rms(ours):F4} vs {Rms(theirs):F4}, centroid {Centroid(ours):F0} vs {Centroid(theirs):F0} Hz");
            Assert.InRange(rmsRatio, 0.92, 1.08);
            Assert.InRange(centroidRatio, 0.92, 1.08);
        }
    }
}
