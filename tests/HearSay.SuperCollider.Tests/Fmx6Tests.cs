using HearSay.SuperCollider.FM7;
using HearSay.SuperCollider.FMX6;
using HearSay.SuperCollider.Validation;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.SuperCollider.Tests;

/// <summary>The FMX6-compatible renderer and exporter, and their agreement with the real FMX6 UGen.</summary>
public sealed class Fmx6Tests(ITestOutputHelper output)
{
    private static readonly SynthesisCapabilities Fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
    private static readonly Fmx6CompatibleOperatorMatrixRenderer Renderer = new();

    /// <summary>Samples into a render after which the constant envelope has reached its level; a whole number of 100 Hz cycles.</summary>
    private const int Settled = 9600;

    private static OperatorMatrixPatch Single(OperatorWaveform waveform, double shape = 0.5, int harmonicLimit = 32) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Waveform = waveform, Shape = shape, HarmonicLimit = harmonicLimit })
            .WithOutputLevel(0, 1.0);

    private static float[] Render(OperatorMatrixPatch patch, double hz = 100, int sampleRate = 48000) =>
        Renderer.Render(patch, new RenderRequest(hz, null, 1.0, TimeSpan.FromSeconds(0.25), TimeSpan.Zero, sampleRate)).Samples;

    [Fact]
    public void Saw_IsTheBandLimitedFourierSeriesFromPhaseZero()
    {
        float[] samples = Render(Single(OperatorWaveform.Saw, harmonicLimit: 32));

        // No warm-up sample: the first output is at phase zero, unlike FM7, and the constant envelope starts from silence.
        Assert.Equal(0f, samples[0]);
        Assert.Equal(0f, samples[1]);
        for (int k = Settled; k < Settled + 480; k++)
        {
            double phase = 2.0 * Math.PI * k * 100.0 / 48000.0;
            double expected = 0.0;
            for (int n = 1; n <= 32; n++)
            {
                expected += -Math.Sin(n * phase) / n;
            }

            expected *= 2.0 / Math.PI;
            Assert.InRange(samples[k], expected - 1e-4, expected + 1e-4);
        }
    }

    [Fact]
    public void HarmonicsAreCappedBelowSafeNyquist()
    {
        // 0.45 * 48000 / 10000 = 2.16, so only two harmonics remain of the 128 requested; a square has only the first.
        float[] samples = Render(Single(OperatorWaveform.Square, harmonicLimit: 128), hz: 10000);
        int k = Settled + 3;
        double phase = 2.0 * Math.PI * k * 10000.0 / 48000.0;
        double expected = (4.0 / Math.PI) * Math.Sin(phase);

        // The phase increment is single precision in the UGen, so the phase has drifted a little by now.
        Assert.InRange(samples[k], expected - 2e-3, expected + 2e-3);
    }

    [Fact]
    public void PulseWithHalfDutyCycle_IsASquare()
    {
        float[] pulse = Render(Single(OperatorWaveform.Pulse, shape: 0.5));
        float[] square = Render(Single(OperatorWaveform.Square));
        for (int k = 0; k < pulse.Length; k++)
        {
            Assert.InRange(pulse[k], square[k] - 1e-4, square[k] + 1e-4);
        }

        float[] narrow = Render(Single(OperatorWaveform.Pulse, shape: 0.1));
        Assert.InRange(narrow.Skip(Settled).Average(), -0.85, -0.75);
    }

    [Fact]
    public void Noise_IsDeterministicPerOperatorSlot()
    {
        OperatorMatrixPatch white = Single(OperatorWaveform.Noise);
        float[] a = Render(white);
        float[] b = Render(white);
        Assert.Equal(a, b);
        Assert.True(a.Skip(Settled).Take(100).Distinct().Count() > 90);
        Assert.All(a, static s => Assert.InRange(s, -1f, 1f));

        // The same noise in another slot is another sequence.
        OperatorMatrixPatch otherSlot = OperatorMatrixPatch.Silent(6)
            .WithOperator(3, white.Operators[0])
            .WithOutputLevel(3, 1.0);
        Assert.NotEqual(a, Render(otherSlot));

        // Sample-and-hold noise changes only when the base phase wraps: every 480 samples at 100 Hz.
        float[] held = Render(Single(OperatorWaveform.SampleHoldNoise));
        var runs = new List<int>();
        int run = 1;
        for (int k = Settled + 1; k < Settled + 2400; k++)
        {
            if (held[k] == held[k - 1])
            {
                run++;
            }
            else
            {
                runs.Add(run);
                run = 1;
            }
        }

        Assert.InRange(runs.Count, 4, 6);
        Assert.All(runs.Skip(1), static r => Assert.InRange(r, 479, 481));
    }

    private static OperatorMatrixPatch FilteredNoise(double cutoffHz, double shape) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtFixed(cutoffHz), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.FilteredNoise, Shape = shape })
            .WithOutputLevel(1, 1.0);

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
    public void FilteredNoise_HasTheColourOfItsShape_AndTheCutoffOfItsFrequency()
    {
        double low = Centroid(Render(FilteredNoise(3000, 0.0)));
        double band = Centroid(Render(FilteredNoise(3000, 0.5)));
        double high = Centroid(Render(FilteredNoise(3000, 1.0)));
        double higherBand = Centroid(Render(FilteredNoise(8000, 0.5)));

        Assert.True(low < 2500 && band > low && high > band && high > 8000, $"low {low:F0}, band {band:F0}, high {high:F0}");
        Assert.True(higherBand > band + 2000, $"band at 3 kHz {band:F0}, at 8 kHz {higherBand:F0}");
        Assert.Equal(Render(FilteredNoise(3000, 0.5)), Render(FilteredNoise(3000, 0.5)));

        // The full range of the shape is valid for filtered noise; a pulse keeps its duty cycle away from 0 and 1.
        Assert.Empty(FilteredNoise(3000, 1.0).Validate());
        Assert.NotEmpty(Single(OperatorWaveform.Pulse, shape: 1.0).Validate());
        Assert.Contains("waves: [0, 7, 0, 0, 0, 0]", new SuperColliderFmx6Exporter().Export(FilteredNoise(3000, 1.0)));
    }

    [SuperColliderFact]
    public void FilteredNoise_IsSampleExactInSuperCollider()
    {
        var validator = new BackendEquivalenceValidator(
            Renderer,
            new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new SuperColliderFmx6Exporter(), Fmx6));
        var request = new RenderRequest(220, null, 1.0, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.1), 48000);

        // Low-, band- and high-pass, and a band whose cutoff follows the note while noise modulates a sine.
        OperatorMatrixPatch following = FilteredNoise(3000, 0.5)
            .WithOperator(1, FilteredNoise(3000, 0.5).Operators[1] with { Frequency = OperatorFrequency.AtRatio(20) })
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()))
            .WithOutputLevel(0, 0.5)
            .WithEdge(new ModulationEdge(1, 0, true, 0.8));
        IReadOnlyList<BackendExportValidation> results = validator.Validate(
            [(FilteredNoise(500, 0.0), request), (FilteredNoise(3000, 0.5), request), (FilteredNoise(11000, 1.0), request), (following, request)]);

        foreach (BackendExportValidation r in results)
        {
            output.WriteLine($"max sample diff {r.MaxSampleDifference:E2} (peak {r.PeakLevel:F2}), overall {r.OverallDistance:F5}");
            Assert.True(r.MaxSampleDifference < 1e-4 * Math.Max(0.05, r.PeakLevel), r.ToString());
        }
    }

    [Fact]
    public void Triangle_PeaksAtQuarterCycle()
    {
        float[] samples = Render(Single(OperatorWaveform.Triangle));
        Assert.InRange(samples[Settled + 120], 0.97, 1.03);
        Assert.InRange(samples[Settled + 360], -1.03, -0.97);
        Assert.InRange(samples[Settled + 240], -0.05, 0.05);
    }

    [Fact]
    public void Exporter_WritesWavesShapesAndHarmonicLimits()
    {
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.3)) with { Waveform = OperatorWaveform.Saw, HarmonicLimit = 64 })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.3)) with { Waveform = OperatorWaveform.Pulse, Shape = 0.25 })
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtFixed(90), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Noise })
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 1.5))
            .WithEdge(new ModulationEdge(2, 0, true, 0.2));

        string scd = new SuperColliderFmx6Exporter().Export(patch, new PatchExportOptions { InstrumentName = "Fmx6Test" });

        Assert.Contains("Needs SuperCollider with the FMX6 plugin", scd);
        Assert.Contains("operators = FMX6.ar(ctlMatrix, modMatrix,", scd);
        Assert.Contains("waves: [2, 4, 6, 0, 0, 0]", scd);
        Assert.Contains("shapes: [0.5, 0.25, 0.5, 0.5, 0.5, 0.5]", scd);
        Assert.Contains("harmonicLimits: [64, 32, 32, 32, 32, 32]", scd);
        Assert.DoesNotContain("FM7", scd.Replace("FMX6", string.Empty));

        // FM7 cannot play it and says so; FMX6 plays every FM7 patch.
        Assert.Throws<PatchNotSupportedException>(() => new SuperColliderFm7Exporter().Export(patch));
        OperatorMatrixPatch sines = ReferencePatches.Find("electric-piano")!.Patch;
        Assert.Contains("waves: [0, 0, 0, 0, 0, 0]", new SuperColliderFmx6Exporter().Export(sines));
    }

    [SuperColliderFact]
    public void RandomPatches_SoundTheSameInSuperCollider()
    {
        var validator = new BackendEquivalenceValidator(
            Renderer,
            new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new SuperColliderFmx6Exporter(), Fmx6));
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
        var waveforms = new HashSet<OperatorWaveform>();
        foreach ((string name, RandomPatchOptions options) in families)
        {
            var generator = new RandomPatchGenerator(Fmx6, options);
            for (int seed = 0; seed < 5; seed++)
            {
                OperatorMatrixPatch patch = generator.Generate(seed);
                bool[] audible = patch.FindAudibleOperators();
                for (int i = 0; i < patch.OperatorCount; i++)
                {
                    if (audible[i])
                    {
                        waveforms.Add(patch.Operators[i].Waveform);
                    }
                }

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

        output.WriteLine($"{sampleExact} of {results.Count} patches match sample by sample; waveforms heard: {string.Join(", ", waveforms.Order())}");
        Assert.True(waveforms.Count >= 5, "the random patches should cover most waveforms");
        Assert.True(sampleExact >= results.Count * 3 / 4, $"only {sampleExact} of {results.Count} are sample-exact");

        // Feedback is chaotic, and the harmonic count of a band-limited operator follows its instantaneous frequency,
        // so a rounding difference of one ulp in a control signal grows faster than with sine operators. Every patch
        // without a feedback loop must be the same sound; a looped one may have parted ways while staying similar.
        int equivalent = 0;
        for (int i = 0; i < results.Count; i++)
        {
            equivalent += results[i].IsEquivalent ? 1 : 0;
            Assert.True(results[i].IsEquivalent || HasFeedbackLoop(jobs[i].Item1), $"{names[i]} has no feedback loop yet differs: {results[i]}");
            Assert.True(results[i].OverallDistance < 0.1, $"{names[i]} is another sound: {results[i]}");
        }

        Assert.True(equivalent >= results.Count * 4 / 5, $"only {equivalent} of {results.Count} are equivalent");
    }

    private static bool HasFeedbackLoop(OperatorMatrixPatch patch)
    {
        int n = patch.OperatorCount;
        bool[] audible = patch.FindAudibleOperators();
        var live = new bool[n, n];
        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            live[edge.SourceOperator, edge.DestinationOperator] = edge.Enabled && edge.Amount != 0.0 && audible[edge.SourceOperator] && audible[edge.DestinationOperator];
        }

        // Transitive closure; a loop is a path from an operator back to itself.
        for (int k = 0; k < n; k++)
        {
            for (int s = 0; s < n; s++)
            {
                for (int d = 0; d < n; d++)
                {
                    live[s, d] |= live[s, k] && live[k, d];
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (live[i, i])
            {
                return true;
            }
        }

        return false;
    }
}
