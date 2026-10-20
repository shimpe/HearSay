using HearSay.Analysis;
using HearSay.Objective;
using HearSay.OperatorMatrix.Parameters;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Tests;

public sealed class FilterPriorTests
{
    private static readonly SynthesisCapabilities Fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
    private static readonly Fmx6CompatibleOperatorMatrixRenderer Renderer = new();

    private static TargetAnalysis Analyze(OperatorMatrixPatch patch, double hz)
    {
        RenderedAudio audio = Renderer.Render(patch, RenderRequest.Create(hz, 1.0, 0.3));
        return new TargetAnalyzer().Analyze(new AudioTarget(audio.Samples, audio.SampleRate, hz));
    }

    private static OperatorMatrixPatch Saw(OperatorFilter? filter, OperatorEnvelope? envelope = null) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), envelope ?? OperatorEnvelope.Adsr(0.01, 0.1, 0.8, 0.2)) with { Waveform = OperatorWaveform.Saw, HarmonicLimit = 128, Filter = filter })
            .WithOutputLevel(0, 0.8);

    [Theory]
    [InlineData(110.0, 700.0, 1.7)]
    [InlineData(220.0, 400.0, 5.0)]
    public void Formant_IsRecoveredWithinHalfTheFundamental(double f0, double f1, double ratio)
    {
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.FormantAt(f1, ratio, 0.6, 8)), f0);
        OperatorFilter? found = FilterPriors.Formant(analysis);
        Assert.NotNull(found);
        Assert.InRange(found!.FixedHz, f1 - (f0 / 2), f1 + (f0 / 2));
        Assert.InRange(found.FixedHz * found.FormantRatio, (f1 * ratio) - (f0 / 2), (f1 * ratio) + (f0 / 2));
    }

    [Fact]
    public void FormantPrior_ThreePeaks_GiveF3()
    {
        const double f0 = 110.0, f1 = 400.0, ratio2 = 3.0, ratio3 = 7.0;
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.Formants(f1, 8, new(ratio2, 0.6, 0), new(ratio3, 0.5, 0))), f0);
        Assert.Equal(3, FilterPriors.FormantPeaks(analysis, 3).Count);
        OperatorFilter found = FilterPriors.Formant(analysis)!;

        // F1 is the lowest peak; F2 and F3 are ratios to it, with levels relative to it. F1 and F2 are within half the
        // fundamental; F3 within the fundamental, as the fitted resonance is read from harmonics 110 Hz apart.
        Assert.InRange(found.FixedHz, f1 - (f0 / 2), f1 + (f0 / 2));
        Assert.InRange(found.FixedHz * found.FormantRatio, (f1 * ratio2) - (f0 / 2), (f1 * ratio2) + (f0 / 2));
        Assert.InRange(found.FixedHz * found.Formant3Ratio, (f1 * ratio3) - f0, (f1 * ratio3) + f0);
        Assert.InRange(found.FormantLevel, 0.2, 1.0);
        Assert.InRange(found.Formant3Level, 0.1, 1.0);

        // Own Qs from the peak widths, within the formant range; F4 and F5 are never set.
        Assert.InRange(found.Q, 2.0, OperatorFilter.MaxFormantQ);
        Assert.True(found.FormantQ2 == 0.0 || found.FormantQ2 is >= 2.0 and <= OperatorFilter.MaxFormantQ, $"Q2 {found.FormantQ2}");
        Assert.True(found.Formant3Q == 0.0 || found.Formant3Q is >= 2.0 and <= OperatorFilter.MaxFormantQ, $"Q3 {found.Formant3Q}");
        Assert.Equal((0.0, 0.0), (found.Formant4Level, found.Formant5Level));
        Assert.Empty(Saw(found).Validate());

        // The prior, the provider and the mutation all offer it.
        IReadOnlyList<PatchHypothesis> hypotheses = new FormantPriorProvider().Generate(analysis, Fmx6);
        Assert.Equal(found, hypotheses[0].Patch.Operators[0].Filter);
        Assert.Contains(hypotheses, static h => h.Patch.Operators[0].Filter!.Formant3Level == PatchParameterSchema.MinFormant3Level && h.Patch.Operators[0].Filter!.FormantLevel > 0.0);
        Assert.All(hypotheses, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fmx6).IsCompatible));
        OperatorMatrixPatch plain = Saw(null);
        TopologyMutation formant = Assert.Single(TopologyMutator.FilterVariants(plain, analysis, [0]), static m => m.Description == "OP1 filter formant");
        Assert.Equal(found, formant.Patch.Operators[0].Filter);

        // A two-formant filter can be given the target's third formant, as a ratio to its own F1.
        OperatorMatrixPatch two = Saw(OperatorFilter.FormantAt(380, 3.1, 0.6, 8));
        TopologyMutation third = Assert.Single(TopologyMutator.FilterVariants(two, analysis, [0]), static m => m.Description == "OP1 filter third formant");
        OperatorFilter added = third.Patch.Operators[0].Filter!;
        Assert.Equal((380.0, 3.1, 0.6, 8.0), (added.FixedHz, added.FormantRatio, added.FormantLevel, added.Q));
        Assert.Equal(found.FixedHz * found.Formant3Ratio, 380.0 * added.Formant3Ratio, 6);
        Assert.Equal(found.Formant3Level, added.Formant3Level);
        Assert.Equal(found.Formant3Q > 0 ? found.Formant3Q : found.Q, added.Formant3Q > 0 ? added.Formant3Q : added.Q);
        Assert.DoesNotContain(TopologyMutator.FilterVariants(third.Patch, analysis, [0]), static m => m.Description == "OP1 filter third formant");

        // A filter whose F3 is on elsewhere has it moved to the heard third peak.
        OperatorMatrixPatch elsewhere = Saw(OperatorFilter.Formants(380, 8, new(3.1, 0.6, 0), new(4.0, 0.05, 30)));
        OperatorFilter moved = Assert.Single(TopologyMutator.FilterVariants(elsewhere, analysis, [0]), static m => m.Description == "OP1 filter third formant").Patch.Operators[0].Filter!;
        Assert.Equal(added, moved);
    }

    [Fact]
    public void ThirdFormantVariant_IsNotOffered_WhenTheFiltersF2IsAboveTheTargetsF3()
    {
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.Formants(400, 8, new(3.0, 0.6, 0), new(7.0, 0.5, 0))), 110);
        OperatorFilter heard = FilterPriors.Formant(analysis)!;
        double f3 = heard.FixedHz * heard.Formant3Ratio;

        // F1 above the heard F3, and F1 below it with F2 above it: a third peak there would not be the third.
        foreach (OperatorFilter filter in new[] { OperatorFilter.FormantAt(f3 * 1.2, 1.5, 0.5, 8), OperatorFilter.FormantAt(f3 / 2, 2.5, 0.5, 8), OperatorFilter.FormantAt(f3 / 2, 2.0, 0.5, 8) })
        {
            Assert.DoesNotContain(TopologyMutator.FilterVariants(Saw(filter), analysis, [0]), static m => m.Description == "OP1 filter third formant");
        }

        // Just below it, it is offered.
        Assert.Contains(TopologyMutator.FilterVariants(Saw(OperatorFilter.FormantAt(f3 / 2, 1.9, 0.5, 8)), analysis, [0]), static m => m.Description == "OP1 filter third formant");
    }

    [Fact]
    public void FormantPrior_TwoPeaks_GiveTheDefaultF3()
    {
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 8)), 110);
        Assert.Equal(2, FilterPriors.FormantPeaks(analysis, 3).Count);
        OperatorFilter found = FilterPriors.Formant(analysis)!;
        Assert.True(found.FormantLevel > 0.0);

        // No third peak is heard: F3 is the search's default, 3 x F1 (above F2 here) at the level floor, same Q.
        Assert.Equal((PatchParameterSchema.DefaultFormant3Ratio(found), PatchParameterSchema.MinFormant3Level, 0.0), (found.Formant3Ratio, found.Formant3Level, found.Formant3Q));
        Assert.True(found.Formant3Ratio > found.FormantRatio);
        Assert.Equal((0.0, 0.0), (found.Formant4Level, found.Formant5Level));
        Assert.DoesNotContain(TopologyMutator.FilterVariants(Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 8)), analysis, [0]), static m => m.Description == "OP1 filter third formant");
    }

    [Fact]
    public void FormantHypotheses_AlwaysHaveF3()
    {
        // Three, two and one heard peaks: every formant hypothesis has F3 on, the heard third peak where there is one
        // (the first hypothesis) and the default otherwise; F4 and F5 stay off.
        (OperatorFilter Hidden, int Count)[] cases =
        [
            (OperatorFilter.Formants(400, 8, new(3.0, 0.6, 0), new(7.0, 0.5, 0)), 3),
            (OperatorFilter.FormantAt(700, 1.7, 0.6, 8), 2),
            (OperatorFilter.FormantAt(700, 2.0, 0.0, 8), 1),
        ];
        foreach ((OperatorFilter hidden, int expectedPeaks) in cases)
        {
            TargetAnalysis analysis = Analyze(Saw(hidden), 110);
            IReadOnlyList<SpectralPeak> peaks = FilterPriors.FormantPeaks(analysis, 3);
            Assert.Equal(expectedPeaks, peaks.Count);
            IReadOnlyList<PatchHypothesis> hypotheses = new FormantPriorProvider().Generate(analysis, Fmx6);
            Assert.NotEmpty(hypotheses);
            Assert.All(hypotheses, h =>
            {
                OperatorFilter f = h.Patch.Operators[0].Filter!;
                Assert.True(f.Formant3Level >= PatchParameterSchema.MinFormant3Level, $"{h.Origin}: F3 level {f.Formant3Level}");
                Assert.True(f.Formant3Ratio > f.FormantRatio, $"{h.Origin}: F3 ratio {f.Formant3Ratio}, F2 ratio {f.FormantRatio}");
                Assert.Equal((0.0, 0.0), (f.Formant4Level, f.Formant5Level));
                Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fmx6).IsCompatible);
            });

            OperatorFilter first = hypotheses[0].Patch.Operators[0].Filter!;
            if (peaks.Count == 3)
            {
                double heard = peaks.Max(static p => p.Hz);
                Assert.Equal(heard, first.FixedHz * first.Formant3Ratio, 6);
                Assert.True(first.Formant3Level > PatchParameterSchema.MinFormant3Level);
            }
            else
            {
                Assert.Equal(PatchParameterSchema.MinFormant3Level, first.Formant3Level);
            }
        }

        // The mutation that gives an unfiltered operator a formant filter gives it F3 as well.
        TargetAnalysis two = Analyze(Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 8)), 110);
        TopologyMutation formant = Assert.Single(TopologyMutator.FilterVariants(Saw(null), two, [0]), static m => m.Description == "OP1 filter formant");
        Assert.True(formant.Patch.Operators[0].Filter!.Formant3Level > 0.0);
    }

    /// <summary>
    /// Rows of the spec's Csound formant table (TABLE III): F1..F5 in hertz, their levels in dB and their bandwidths
    /// in hertz.
    /// </summary>
    private static readonly Dictionary<string, (double[] Hz, double[] Db, double[] Bw)> Vowels = new()
    {
        ["bass a"] = ([600, 1040, 2250, 2450, 2750], [0, -7, -9, -9, -20], [60, 70, 110, 120, 130]),
        ["soprano e"] = ([350, 2000, 2800, 3600, 4950], [0, -20, -15, -40, -56], [60, 100, 120, 150, 200]),
        ["soprano i"] = ([270, 2140, 2950, 3900, 4950], [0, -12, -26, -26, -44], [60, 90, 100, 120, 120]),
    };

    /// <summary>
    /// A vowel-like target made without HearSay's filter, as the benchmark made them: 1/n harmonics of the note up to
    /// 20 kHz, each weighted by the sum of the table's five resonances 10^(dB/20) / sqrt(1 + ((f² - F²) / (f·BW))²);
    /// 1 s at 48 kHz with a 20 ms attack and a 100 ms release, peak 0.7. Analysed as the search analyses a target.
    /// </summary>
    private static TargetAnalysis Vowel(string name, double f0)
    {
        const int rate = 48000;
        (double[] hz, double[] db, double[] bw) = Vowels[name];
        var samples = new double[rate];
        for (int n = 1; n * f0 < 20000.0; n++)
        {
            double f = n * f0, weight = 0.0;
            for (int k = 0; k < hz.Length; k++)
            {
                double x = ((f * f) - (hz[k] * hz[k])) / (f * bw[k]);
                weight += Math.Pow(10.0, db[k] / 20.0) / Math.Sqrt(1.0 + (x * x));
            }

            double amplitude = weight / n, step = 2.0 * Math.PI * f / rate;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] += amplitude * Math.Sin(step * i);
            }
        }

        double peak = samples.Max(Math.Abs);
        var output = new float[rate];
        for (int i = 0; i < rate; i++)
        {
            double t = (double)i / rate;
            double envelope = Math.Min(1.0, Math.Min(t / 0.02, (1.0 - t) / 0.1));
            output[i] = (float)(0.7 * envelope * samples[i] / peak);
        }

        return new TargetAnalyzer().Analyze(new AudioTarget(output, rate, f0));
    }

    [Fact]
    public void FormantPeaks_OfVowelTargets_AreWhereTheTableSays()
    {
        // Bass "a" at 130 Hz: harmonics close enough to read all three formants within 15 %, and F3's width: its Q
        // within a factor 2 of the table's 2250 / 110. (Read: 609, 1024 and 2423 Hz, F3's Q 12.5; F3 is pulled up by
        // F4, 200 Hz above it and as strong.)
        TargetAnalysis bassA = Vowel("bass a", 130);
        SpectralPeak[] bass = [.. FilterPriors.FormantPeaks(bassA, 3).OrderBy(static p => p.Hz)];
        Assert.Equal(3, bass.Length);
        Assert.All(new[] { 600.0, 1040.0, 2250.0 }.Zip(bass), static pair => Assert.InRange(pair.Second.Hz, 0.85 * pair.First, 1.15 * pair.First));
        Assert.InRange(bass[2].Q, 2250.0 / 110.0 / 2.0, 2.0 * 2250.0 / 110.0);

        // The formant filter read from it has three peaks at those places, F3 with its own Q.
        OperatorFilter filter = FilterPriors.Formant(bassA)!;
        Assert.InRange(filter.FixedHz * filter.Formant3Ratio, 0.85 * 2250.0, 1.15 * 2250.0);
        Assert.True(filter.Formant3Level > 0.1, $"F3 level {filter.Formant3Level}");
        Assert.True(filter.Formant3Q > 0.0);

        // The old reading of the peaks, which other priors still use, merges and misses formants here (618, 2335 Hz).
        Assert.True(FilterPriors.Peaks(bassA, 3).Count < 3);

        // A soprano at 220 Hz has a harmonic only every 220 Hz, which limits what can be read. Where the formants are
        // several harmonics apart, as in "e", all three are found (read: 351, 1983, 2803 Hz), and F3's Q within a
        // factor 2.5 of the table's (read: 12.9 for 23.3).
        SpectralPeak[] e = [.. FilterPriors.FormantPeaks(Vowel("soprano e", 220), 3).OrderBy(static p => p.Hz)];
        Assert.Equal(3, e.Length);
        Assert.All(new[] { 350.0, 2000.0, 2800.0 }.Zip(e), static pair => Assert.InRange(pair.Second.Hz, 0.85 * pair.First, 1.15 * pair.First));
        Assert.InRange(e[2].Q, 2800.0 / 120.0 / 2.5, 2.5 * 2800.0 / 120.0);

        // In "i" F1 (270 Hz) is read at the fundamental, the lowest harmonic (within half the fundamental), and F2
        // within 15 % (read: 220, 2149 Hz); F3, 26 dB down between the skirts of F2 and F4, is not resolved, so the
        // third peak read is F4 (3907 Hz). Where F1 and F2 are less than two harmonics apart (soprano "a", "o", "u")
        // they merge into one peak.
        SpectralPeak[] i = [.. FilterPriors.FormantPeaks(Vowel("soprano i", 220), 3).OrderBy(static p => p.Hz)];
        Assert.True(i.Length >= 2);
        Assert.InRange(i[0].Hz, 270.0 - 110.0, 270.0 + 110.0);
        Assert.InRange(i[1].Hz, 0.85 * 2140.0, 1.15 * 2140.0);
    }

    [Fact]
    public void LowPass_FindsTheCutoff_AndTheSlope()
    {
        LowPassPrior? two = FilterPriors.LowPass(Analyze(Saw(OperatorFilter.LowPass(6)), 110));
        LowPassPrior? four = FilterPriors.LowPass(Analyze(Saw(OperatorFilter.LowPass(6, poles: 4)), 110));
        Assert.NotNull(two);
        Assert.InRange(two!.CutoffRatio, 3.0, 12.0);
        Assert.Equal(2, two.Poles);
        Assert.Equal(4, four!.Poles);
        Assert.Null(FilterPriors.LowPass(Analyze(Saw(null), 110)));
    }

    [Fact]
    public void Sweep_IsSeenOnlyWhenTheBrightnessMoves()
    {
        var closing = new OperatorEnvelope([new EnvelopePoint(0, 1), new EnvelopePoint(0.3, 0), new EnvelopePoint(0.1, 0)], SustainPoint: 1, ReleasePoint: 2);
        SweepPrior? sweep = FilterPriors.Sweep(Analyze(Saw(OperatorFilter.LowPass(2, poles: 4) with { EnvelopeOctaves = 3, Envelope = closing }), 110));
        Assert.NotNull(sweep);
        Assert.False(sweep!.Rising);
        Assert.InRange(sweep.Octaves, 1.0, 4.0);
        Assert.Null(FilterPriors.Sweep(Analyze(Saw(OperatorFilter.LowPass(4)), 110)));
    }

    [Fact]
    public void Providers_ProposeValidFilteredCarriers_OnlyForProfilesWithFilters()
    {
        TargetAnalysis vowel = Analyze(Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 8)), 110);
        IReadOnlyList<PatchHypothesis> formants = new FormantPriorProvider().Generate(vowel, Fmx6);
        Assert.NotEmpty(formants);
        Assert.All(formants, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fmx6).IsCompatible));
        Assert.Equal(OperatorFilterType.Formant, formants[0].Patch.Operators[0].Filter!.Type);

        TargetAnalysis dark = Analyze(Saw(OperatorFilter.LowPass(5, poles: 4)), 110);
        IReadOnlyList<PatchHypothesis> subtractive = new SubtractivePriorProvider().Generate(dark, Fmx6);
        Assert.NotEmpty(subtractive);
        Assert.All(subtractive, h => Assert.Equal(OperatorFilterType.LowPass, h.Patch.Operators[0].Filter!.Type));

        Assert.Empty(new FormantPriorProvider().Generate(vowel, SynthesisProfiles.SuperColliderFm7.Capabilities));
        Assert.Empty(new SubtractivePriorProvider().Generate(dark, SynthesisProfiles.SuperColliderNome6.Capabilities));
    }

    [Fact]
    public void SweepMutation_ForAnOpeningFilter_LowersTheBaseSoItOpensToTheMeasuredCutoff()
    {
        var opening = new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.25, 1), new EnvelopePoint(0.01, 1), new EnvelopePoint(0.1, 1)], SustainPoint: 2, ReleasePoint: 3);
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.LowPass(1, poles: 4) with { EnvelopeOctaves = 3, Envelope = opening }), 110);
        SweepPrior sweep = FilterPriors.Sweep(analysis)!;
        Assert.True(sweep.Rising);

        OperatorMatrixPatch measured = Saw(OperatorFilter.LowPass(8, poles: 4));
        TopologyMutation variant = Assert.Single(TopologyMutator.FilterVariants(measured, analysis, [0]), static m => m.Description == "OP1 filter sweep");
        OperatorFilter filter = variant.Patch.Operators[0].Filter!;
        Assert.Equal(8.0 / Math.Pow(2.0, sweep.Octaves), filter.CutoffRatio, 10);
        Assert.Equal(sweep.Octaves, filter.EnvelopeOctaves);
    }

    /// <summary>A saw with few harmonics, with or without a filter, as a search may hold it.</summary>
    private static OperatorMatrixPatch DullSaw(OperatorFilter? filter, int harmonics) =>
        Saw(filter).WithOperator(0, Saw(filter).Operators[0] with { HarmonicLimit = harmonics });

    /// <summary>OP1 as a pulse of the given width and harmonics, with or without a filter.</summary>
    private static OperatorMatrixPatch Pulse(OperatorFilter? filter, double width, int harmonics) =>
        Saw(filter).WithOperator(0, Saw(filter).Operators[0] with { Waveform = OperatorWaveform.Pulse, Shape = width, HarmonicLimit = harmonics });

    /// <summary>
    /// Every tonal operator with a formant filter is a rich source, a saw or a pulse with at least 64 harmonics; noise is
    /// left alone.
    /// </summary>
    private static bool FormantOperatorsAreRichSources(OperatorMatrixPatch patch) =>
        patch.Operators.All(static o => o.Filter?.Type != OperatorFilterType.Formant || o.Waveform.IsNoise()
            || (o.Waveform is OperatorWaveform.Saw or OperatorWaveform.Pulse && o.HarmonicLimit >= RandomPatchGenerator.FormantHarmonics));

    private static readonly OperatorFilter Vowel600 = OperatorFilter.FormantAt(600, 1.7, 0.6, 8);

    private static TargetAnalysis TwoFormants() => Analyze(Saw(OperatorFilter.Formants(600, 8, new(1.7, 0.6, 0), new(3.8, 0.4, 0))), 110.0);

    /// <summary>An evaluator for screened proposals against the two-formant target.</summary>
    private static (PreparedObjectiveTarget Prepared, PatchEvaluator Evaluator) Screening()
    {
        RenderRequest request = RenderRequest.Create(110, 1.0, 0.3);
        RenderedAudio audio = Renderer.Render(Saw(OperatorFilter.Formants(600, 8, new(1.7, 0.6, 0), new(3.8, 0.4, 0))), request);
        PreparedObjectiveTarget prepared = new AudioObjective().Prepare(new AudioTarget(audio.Samples, audio.SampleRate, 110.0));
        return (prepared, new PatchEvaluator(Renderer, new AudioObjective(), prepared, request, 2000, 0.02, null, 8));
    }

    [Fact]
    public void FormantMutation_GivesTheOperatorAtLeast64Harmonics()
    {
        TargetAnalysis analysis = TwoFormants();
        List<TopologyMutation> variants = TopologyMutator.FilterVariants(DullSaw(null, 16), analysis, [0]);

        // A formant filter brings at least 64 harmonics; the other filters leave the operator's own.
        Assert.Equal(64, Assert.Single(variants, static m => m.Description == "OP1 filter formant").Patch.Operators[0].HarmonicLimit);
        Assert.Equal(16, Assert.Single(variants, static m => m.Description == "OP1 filter low-pass").Patch.Operators[0].HarmonicLimit);

        // An operator that already had more keeps them.
        Assert.Equal(128, Assert.Single(TopologyMutator.FilterVariants(DullSaw(null, 128), analysis, [0]), static m => m.Description == "OP1 filter formant").Patch.Operators[0].HarmonicLimit);
    }

    [Fact]
    public void FormantMutation_MakesTheOperatorARichSource()
    {
        TargetAnalysis analysis = TwoFormants();

        // A saw given a formant filter stays a saw, with 64 harmonics.
        List<TopologyMutation> variants = TopologyMutator.FilterVariants(DullSaw(null, 16), analysis, [0]);
        OperatorPatch formant = Assert.Single(variants, static m => m.Description == "OP1 filter formant").Patch.Operators[0];
        Assert.Equal((OperatorWaveform.Saw, 64, OperatorFilterType.Formant), (formant.Waveform, formant.HarmonicLimit, formant.Filter!.Type));

        // A pulse keeps its width and gets 64 harmonics.
        OperatorPatch wide = Assert.Single(TopologyMutator.FilterVariants(Pulse(null, 0.4, 32), analysis, [0]), static m => m.Description == "OP1 filter formant").Patch.Operators[0];
        Assert.Equal((OperatorWaveform.Pulse, 0.4, 64), (wide.Waveform, wide.Shape, wide.HarmonicLimit));

        // A sine, which leaves a formant filter nothing to shape, becomes the 1 % voice source.
        OperatorMatrixPatch sine = Saw(null).WithOperator(0, Saw(null).Operators[0] with { Waveform = OperatorWaveform.Sine });
        OperatorPatch source = Assert.Single(TopologyMutator.FilterVariants(sine, analysis, [0]), static m => m.Description == "OP1 filter formant").Patch.Operators[0];
        Assert.Equal((OperatorWaveform.Pulse, VoiceSource.Width, 128), (source.Waveform, source.Shape, source.HarmonicLimit));

        // Noise with a formant filter stays noise: a whisper.
        OperatorMatrixPatch noise = Saw(null).WithOperator(0, Saw(null).Operators[0] with { Waveform = OperatorWaveform.FilteredNoise, Shape = 0.7 });
        OperatorPatch whisper = Assert.Single(TopologyMutator.FilterVariants(noise, analysis, [0]), static m => m.Description == "OP1 filter formant").Patch.Operators[0];
        Assert.Equal((OperatorWaveform.FilteredNoise, 0.7), (whisper.Waveform, whisper.Shape));
    }

    [Fact]
    public void Mutations_NeverLeaveAFormantOperatorWithFewerThan64Harmonics()
    {
        TargetAnalysis analysis = TwoFormants();
        var mutator = new TopologyMutator(Fmx6);

        // A rich source at 64 is never halved, and one with fewer (an older sound) comes out with at least 64.
        foreach (OperatorMatrixPatch start in new[] { DullSaw(Vowel600, 64), Pulse(Vowel600, 0.2, 64), DullSaw(Vowel600, 16) })
        {
            for (int seed = 0; seed < 30; seed++)
            {
                IReadOnlyList<TopologyMutation> proposals = mutator.Propose(start, analysis, new Random(seed), 12);
                Assert.All(proposals, static p => Assert.True(FormantOperatorsAreRichSources(p.Patch), p.Description));
                Assert.DoesNotContain(proposals, static p => p.Description == "OP1 harmonics 64 -> 32");
            }
        }

        // Screened proposals too, waveform changes of a formant operator included: an older sine with a formant filter
        // comes out as a rich source.
        OperatorMatrixPatch sine = DullSaw(Vowel600, 16).WithOperator(0, DullSaw(Vowel600, 16).Operators[0] with { Waveform = OperatorWaveform.Sine });
        (PreparedObjectiveTarget prepared, PatchEvaluator evaluator) = Screening();
        IReadOnlyList<TopologyMutation> screened = mutator.ProposeScreened(sine, prepared.Analysis, evaluator, new Random(1), 12);
        Assert.NotEmpty(screened);
        Assert.All(screened, static p => Assert.True(FormantOperatorsAreRichSources(p.Patch), p.Description));
    }

    [Fact]
    public void WaveformMutation_OfAFormantOperator_ToSawPulseOrNoise()
    {
        // A 1 % pulse with a formant filter: a change to a saw or to noise (a whisper) is kept; a change to a sine,
        // triangle or square would come back as the 1 % pulse, which is the patch itself, and is dropped.
        TargetAnalysis analysis = TwoFormants();
        var mutator = new TopologyMutator(Fmx6);
        OperatorMatrixPatch start = Pulse(Vowel600, VoiceSource.Width, 64);
        static bool IsWaveformChange(TopologyMutation p) => p.Description.StartsWith("OP1 waveform", StringComparison.Ordinal) && !p.Description.Contains("harmonics", StringComparison.Ordinal);
        var waveforms = new List<TopologyMutation>();
        for (int seed = 0; seed < 60; seed++)
        {
            waveforms.AddRange(mutator.Propose(start, analysis, new Random(seed), 12).Where(IsWaveformChange));
        }

        Assert.All(waveforms, static p => Assert.True(p.Patch.Operators[0].Waveform == OperatorWaveform.Saw || p.Patch.Operators[0].Waveform.IsNoise(), $"{p.Description}: {p.Patch.Operators[0].Waveform}"));
        Assert.Contains(waveforms, static p => p.Description == "OP1 waveform Noise");
        Assert.Contains(waveforms, static p => p.Description == "OP1 waveform Saw" && p.Patch.Operators[0].HarmonicLimit == 64);

        // From a saw: a pulse is kept at the width it gets, a sine comes back as the 1 % pulse.
        OperatorMatrixPatch saw = DullSaw(Vowel600, 64);
        var fromSaw = new List<TopologyMutation>();
        for (int seed = 0; seed < 60; seed++)
        {
            fromSaw.AddRange(mutator.Propose(saw, analysis, new Random(seed), 12).Where(IsWaveformChange));
        }

        Assert.Contains(fromSaw, static p => p.Description == "OP1 waveform Pulse" && p.Patch.Operators[0].Waveform == OperatorWaveform.Pulse && p.Patch.Operators[0].Shape == 0.5);
        Assert.Contains(fromSaw, static p => p.Description == "OP1 waveform Sine");
        Assert.All(fromSaw.Where(static p => p.Description is "OP1 waveform Sine" or "OP1 waveform Triangle" or "OP1 waveform Square"), static p =>
            Assert.Equal((OperatorWaveform.Pulse, VoiceSource.Width, 64), (p.Patch.Operators[0].Waveform, p.Patch.Operators[0].Shape, p.Patch.Operators[0].HarmonicLimit)));

        // Screened from the 1 % pulse: the sine, triangle and square candidates come back to the patch itself.
        (PreparedObjectiveTarget prepared, PatchEvaluator evaluator) = Screening();
        IReadOnlyList<TopologyMutation> screened = mutator.ProposeScreened(start, prepared.Analysis, evaluator, new Random(1), 12);
        Assert.NotEmpty(screened);
        Assert.All(screened, p => Assert.NotEqual(start.ComputeHash(), p.Patch.ComputeHash()));
        Assert.All(screened.Where(IsWaveformChange), static p => Assert.True(p.Patch.Operators[0].Waveform == OperatorWaveform.Saw || p.Patch.Operators[0].Waveform.IsNoise(), p.Description));
    }

    [Fact]
    public void TunedPulseWidth_OfAFormantOperator_IsKept()
    {
        // The search may tune the width of a formant pulse: no step but a change to another waveform moves it.
        TargetAnalysis analysis = TwoFormants();
        var mutator = new TopologyMutator(Fmx6);
        OperatorMatrixPatch start = Pulse(Vowel600, 0.3, 64);
        var proposals = new List<TopologyMutation>();
        for (int seed = 0; seed < 60; seed++)
        {
            proposals.AddRange(mutator.Propose(start, analysis, new Random(seed), 12));
        }

        proposals.AddRange(TopologyMutator.FilterVariants(start, analysis, [0]));
        Assert.Contains(proposals, static p => p.Description == "OP1 harmonics 64 -> 128");
        Assert.Contains(proposals, static p => p.Description == "OP1 filter third formant");
        Assert.All(proposals.Where(static p => !p.Description.StartsWith("OP1 waveform", StringComparison.Ordinal)), static p =>
            Assert.Equal((OperatorWaveform.Pulse, 0.3), (p.Patch.Operators[0].Waveform, p.Patch.Operators[0].Shape)));

        // An older tuned pulse with fewer harmonics keeps its width and gets 64; without the filter it is left alone.
        List<TopologyMutation> older = TopologyMutator.FilterVariants(Pulse(Vowel600, 0.3, 16), analysis, [0]);
        OperatorPatch removed = Assert.Single(older, static m => m.Description == "OP1 filter removed").Patch.Operators[0];
        Assert.Equal((0.3, 16), (removed.Shape, removed.HarmonicLimit));
        OperatorPatch third = Assert.Single(older, static m => m.Description == "OP1 filter third formant").Patch.Operators[0];
        Assert.Equal((OperatorWaveform.Pulse, 0.3, 64), (third.Waveform, third.Shape, third.HarmonicLimit));
    }

    [Fact]
    public void FormantHypotheses_AreASawAndAPulse()
    {
        // The formant prior proposes two carriers per filter, a saw and a 20 % pulse, both with 64 harmonics.
        TargetAnalysis analysis = TwoFormants();
        IReadOnlyList<PatchHypothesis> hypotheses = new FormantPriorProvider().Generate(analysis, Fmx6);
        Assert.NotEmpty(hypotheses);
        Assert.Equal(2 * hypotheses.Select(static h => h.Patch.Operators[0].Filter).Distinct().Count(), hypotheses.Count);
        for (int i = 0; i < hypotheses.Count; i += 2)
        {
            OperatorPatch saw = hypotheses[i].Patch.Operators[0];
            OperatorPatch pulse = hypotheses[i + 1].Patch.Operators[0];
            Assert.Equal((OperatorWaveform.Saw, 0.5, 64), (saw.Waveform, saw.Shape, saw.HarmonicLimit));
            Assert.Equal((OperatorWaveform.Pulse, 0.2, 64), (pulse.Waveform, pulse.Shape, pulse.HarmonicLimit));
            Assert.Equal(saw.Filter, pulse.Filter);
            Assert.Equal(OperatorFilterType.Formant, saw.Filter!.Type);
            Assert.StartsWith("saw with ", hypotheses[i].Origin, StringComparison.Ordinal);
            Assert.StartsWith("pulse with ", hypotheses[i + 1].Origin, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ScreenedProposals_AreEachADifferentPatch()
    {
        // An older sine with a formant filter: its triangle and square candidates both become the same 1 % voice source.
        // That patch is screened and proposed once, not once per description.
        OperatorMatrixPatch sine = DullSaw(Vowel600, 16).WithOperator(0, DullSaw(Vowel600, 16).Operators[0] with { Waveform = OperatorWaveform.Sine });
        (PreparedObjectiveTarget prepared, PatchEvaluator evaluator) = Screening();
        IReadOnlyList<TopologyMutation> screened = new TopologyMutator(Fmx6).ProposeScreened(sine, prepared.Analysis, evaluator, new Random(1), 24);
        Assert.NotEmpty(screened);
        Assert.Equal(screened.Count, screened.Select(static p => p.Patch.ComputeHash()).Distinct().Count());
    }
}
