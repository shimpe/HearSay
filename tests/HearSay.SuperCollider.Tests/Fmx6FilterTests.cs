using HearSay.SuperCollider.FMX6;
using HearSay.SuperCollider.Validation;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.SuperCollider.Tests;

/// <summary>Operator filters in the FMX6-compatible renderer.</summary>
public sealed class Fmx6FilterTests(ITestOutputHelper output)
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
    public void FilterOnOneOperator_LeavesTheOthersUntouched()
    {
        // The unfiltered saw sounds the same whether or not another operator of the patch has a filter.
        OperatorPatch saw = OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Saw, HarmonicLimit = 64 };
        OperatorPatch filtered = OperatorPatch.Sine(OperatorFrequency.AtRatio(3), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Square, Filter = OperatorFilter.LowPass(2, 4, poles: 4) };
        OperatorMatrixPatch silent = OperatorMatrixPatch.Silent(6);
        float[] both = Render(silent.WithOperator(0, saw).WithOutputLevel(0, 0.5).WithOperator(1, filtered).WithOutputLevel(1, 0.5));
        float[] sawAlone = Render(silent.WithOperator(0, saw).WithOutputLevel(0, 0.5));
        float[] filteredAlone = Render(silent.WithOperator(1, filtered).WithOutputLevel(1, 0.5));
        Assert.Equal(both.Length, sawAlone.Length);
        for (int i = 0; i < both.Length; i++)
        {
            Assert.True(Math.Abs(both[i] - (sawAlone[i] + filteredAlone[i])) < 1e-6f, FormattableString.Invariant($"sample {i}"));
        }

        Assert.Null(HearSay.Synthesis.Compilation.PatchCompiler.Compile(Saw(null), RenderRequest.Create(100, 0.4, 0.05)).Filters);
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

        // Review focus 2: a resonant filter inside a self-feedback loop stays bounded, also the ones with most gain: a
        // 4-pole low-pass at Q 20 tuned to the note (about 14 times) and a +24 dB peak (about 16 times).
        foreach (OperatorFilter filter in new[] { OperatorFilter.BandPass(3, 20), OperatorFilter.LowPass(1, 20, poles: 4), OperatorFilter.PeakAt(100, 24, 20) })
        {
            OperatorMatrixPatch loop = OperatorMatrixPatch.Silent(6)
                .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Filter = filter })
                .WithOutputLevel(0, 1.0)
                .WithEdge(new ModulationEdge(0, 0, true, 3.0));
            float[] samples = Render(loop);
            Assert.All(samples, static s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 50f));
            Assert.True(samples.Max(Math.Abs) > 1.5f || filter.Type == OperatorFilterType.BandPass, $"{filter.Type} should resonate");
        }
    }

    [Fact]
    public void Exporter_WritesFiltersOnlyWhenThereAreAny()
    {
        var exporter = new SuperColliderFmx6Exporter();
        Assert.DoesNotContain("filters:", exporter.Export(Saw(null)), StringComparison.Ordinal);

        var sweep = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1);
        OperatorMatrixPatch patch = Saw(OperatorFilter.LowPass(4, 2, poles: 4) with { EnvelopeOctaves = 1.5, Envelope = sweep })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Filter = OperatorFilter.FormantAt(700, 1.8, 0.5, 8) })
            .WithOutputLevel(1, 0.5)
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3)) with { Enabled = false, Filter = OperatorFilter.Notch(2, 2) });
        string scd = exporter.Export(patch);

        Assert.Contains("filters: [", scd, StringComparison.Ordinal);
        Assert.Contains("[2, (freq * 4.0), (1.5 * EnvGen.kr(", scd, StringComparison.Ordinal);
        Assert.Contains("[8, 700.0, 0, 8.0, 1.8, 0.5, 6.0]", scd, StringComparison.Ordinal);

        // Review focus 4: the disabled operator's filter is written as off.
        Assert.Contains("[0, 0, 0, 1, 1, 0, 0]", scd, StringComparison.Ordinal);
    }

    /// <summary>A sawtooth through a lowpass sweep, a formant filter on a second carrier, and a disabled notch.</summary>
    private static OperatorMatrixPatch ThreeFilters(OperatorFilter formant) =>
        Saw(OperatorFilter.LowPass(4, 2, poles: 4) with { EnvelopeOctaves = 1.5, Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1) })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Filter = formant })
            .WithOutputLevel(1, 0.5)
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3)) with { Enabled = false, Filter = OperatorFilter.Notch(2, 2) });

    private static string Sha256(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Export_WithoutExtras_IsUnchanged()
    {
        var exporter = new SuperColliderFmx6Exporter();

        // Captured from the exporter of the two-formant model.
        const string Before = "C7628EB4669C1C69E1C581802AB62E5531493DA5F1162FD973B3F474CA6BD5F7";
        Assert.Equal(Before, Sha256(exporter.Export(ThreeFilters(OperatorFilter.FormantAt(700, 1.8, 0.5, 8)))));

        // F3..F5 with a level of 0 are off and the Q of a formant filter is in the 7-field row, so these are 7-field
        // rows too: a disabled operator's five formants are never heard, and neither are a ratio or a Q of a peak that is off.
        OperatorMatrixPatch quiet = ThreeFilters(OperatorFilter.FormantAt(700, 1.8, 0.5, 41) with { Formant3Ratio = 3, Formant4Q = 20 })
            .WithOperator(3, OperatorPatch.Sine(OperatorFrequency.AtRatio(5)) with { Enabled = false, Filter = OperatorFilter.Formants(500, 9, new FormantPeak(2, 1, 5), new FormantPeak(3, 1, 0)) });
        string scd = exporter.Export(quiet);
        Assert.Contains("[8, 700.0, 0, 41.0, 1.8, 0.5, 6.0], [0, 0, 0, 1, 1, 0, 0], [0, 0, 0, 1, 1, 0, 0], [0, 0, 0, 1, 1, 0, 0], [0, 0, 0, 1, 1, 0, 0]] /* [type, freq, octaves, q, formantRatio, formantLevel, gainDb] */", scd, StringComparison.Ordinal);
        Assert.DoesNotContain("q2", scd, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_WithFiveFormants_Writes17FieldRows()
    {
        OperatorFilter five = OperatorFilter.Formants(800, 41, new FormantPeak(1.4375, 0.5, 12.8), new FormantPeak(3.625, 0.025, 0), new FormantPeak(4.875, 0.1, 30));
        string scd = new SuperColliderFmx6Exporter().Export(ThreeFilters(five));

        // Every row has 17 fields: the low-pass and the off rows get the defaults of the extra fields.
        Assert.Contains("filters: [[2, (freq * 4.0), (1.5 * EnvGen.kr(", scd, StringComparison.Ordinal);
        Assert.Contains("2.0, 2.0, 0.5, 6.0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0], [8, 800.0, 0, 41.0, 1.4375, 0.5, 6.0, 12.8, 3.625, 0.025, 0, 4.875, 0.1, 30.0, 1, 0, 0], ", scd, StringComparison.Ordinal);
        Assert.Contains(
            "[0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0], [0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0]] /* [type, freq, octaves, q, ratio2, level2, gainDb, q2, ratio3, level3, q3, ratio4, level4, q4, ratio5, level5, q5] */",
            scd,
            StringComparison.Ordinal);

        // An own Q for F2 alone is enough.
        Assert.Contains("[8, 700.0, 0, 8.0, 1.8, 0.5, 6.0, 16.0, 1, 0, 0, 1, 0, 0, 1, 0, 0]", new SuperColliderFmx6Exporter().Export(ThreeFilters(OperatorFilter.FormantAt(700, 1.8, 0.5, 8) with { FormantQ2 = 16 })), StringComparison.Ordinal);
    }

    /// <summary>The FMX6 SynthDef of a patch with the UGen at another oversampling factor.</summary>
    private sealed class OversampledExporter(int factor) : ISynthDefExporter
    {
        private readonly SuperColliderFmx6Exporter _inner = new();

        public int ControlBlockSize => _inner.ControlBlockSize;

        public string ExportSynthDef(OperatorMatrixPatch patch, string synthDefName = "HearSayPatch") =>
            _inner.ExportSynthDef(patch, synthDefName).Replace("FMX6.ar(ctlMatrix, modMatrix,", $"FMX6.ar(ctlMatrix, modMatrix, oversample: {factor},", StringComparison.Ordinal);
    }

    [SuperColliderFact]
    public void Filters_AreSampleExactInSuperCollider()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var validator = new BackendEquivalenceValidator(
            Renderer,
            new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new SuperColliderFmx6Exporter(), fmx6));
        var request = new RenderRequest(220, null, 1.0, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.1), 48000);
        var sweep = OperatorEnvelope.Adsr(0.02, 0.2, 0.2, 0.1);
        OperatorMatrixPatch modulated = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Square, Filter = OperatorFilter.LowPass(3, 4, poles: 4) })
            .WithOutputLevel(0, 1.0)
            .WithEdge(new ModulationEdge(1, 0, true, 1.2));
        (string Name, OperatorMatrixPatch Patch)[] cases =
        [
            ("lp2", Saw(OperatorFilter.LowPass(4, 2))),
            ("lp4 sweep", Saw(OperatorFilter.LowPass(2, 3, poles: 4) with { EnvelopeOctaves = 3, Envelope = sweep })),
            ("hp2", Saw(OperatorFilter.HighPass(3, 1.5))),
            ("hp4", Saw(OperatorFilter.HighPass(3, 1.5, poles: 4))),
            ("bp", Saw(OperatorFilter.BandPass(5, 6))),
            ("notch", Saw(OperatorFilter.Notch(3, 3))),
            ("peak", Saw(OperatorFilter.PeakAt(1500, 12, 4))),
            ("formant", Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 10))),
            ("formant sweep", Saw(OperatorFilter.FormantAt(500, 2.2, 0.4, 8) with { EnvelopeOctaves = -1, Envelope = sweep })),
            ("filtered modulator", modulated),

            // Review focus 3: the envelope pushes the cutoff below 10 Hz, where both sides clamp.
            ("clamped low", Saw(OperatorFilter.LowPass(0.25, 2) with { EnvelopeOctaves = -4, Envelope = sweep })),

            // Five formants, each with its own Q and F1 at Q 41: 17-field rows, the 175-input layout.
            ("five formants", Saw(OperatorFilter.Formants(800, 41, new FormantPeak(1.4375, 0.5, 12.8), new FormantPeak(3.625, 0.025, 24.2), new FormantPeak(4.875, 0.1, 0), new FormantPeak(6.1875, 0.003, 35.4)))),

            // A 7-field row with a formant Q above 20: both sides clamp at 50.
            ("formant q 41", Saw(OperatorFilter.FormantAt(500, 2.2, 0.4, 41))),

            // A swept five-formant carrier and a low-passed modulator: a 17-field low-pass row and off rows.
            ("five formants fm", modulated.WithOperator(0, modulated.Operators[0] with
            {
                Filter = OperatorFilter.Formants(600, 9, new FormantPeak(1.8, 0.7, 0), new FormantPeak(4.2, 0.3, 45)) with { EnvelopeOctaves = 0.8, Envelope = sweep },
            })),
        ];

        IReadOnlyList<BackendExportValidation> results = validator.Validate([.. cases.Select(c => (c.Patch, request))]);
        for (int i = 0; i < results.Count; i++)
        {
            BackendExportValidation r = results[i];
            output.WriteLine($"{cases[i].Name,-20} max sample diff {r.MaxSampleDifference:E2} (peak {r.PeakLevel:F2}), overall {r.OverallDistance:F5}");
            // Sample-exact means bit-identical: the renderer and the UGen do the same double-precision operations.
            Assert.True(r.MaxSampleDifference == 0.0, $"{cases[i].Name}: {r}");
        }

        // At 2x oversampling the UGen runs and stays close to the 1x sound HearSay optimizes.
        var oversampled = new BackendEquivalenceValidator(
            Renderer,
            new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new OversampledExporter(2), fmx6));
        foreach (BackendExportValidation r in oversampled.Validate([(cases[1].Patch, request), (cases[7].Patch, request)]))
        {
            output.WriteLine($"2x: overall {r.OverallDistance:F5}");
            Assert.True(r.OverallDistance < 0.1, r.ToString());
        }
    }
}
