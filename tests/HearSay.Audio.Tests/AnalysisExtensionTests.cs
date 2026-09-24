using HearSay.Analysis;
using HearSay.Audio;
using HearSay.Envelope;
using HearSay.Objective;
using HearSay.Partials;
using HearSay.Pitch;
using HearSay.Spectral;

namespace HearSay.Tests;

public sealed class AnalysisExtensionTests
{
    private const int SampleRate = 48000;
    private static readonly TargetAnalyzer Analyzer = new();

    /// <summary>Sum of sinusoids with individual exponential decays and an optional pitch function in cents.</summary>
    private static float[] Partials(
        double seconds,
        (double Hz, double Amplitude, double DecayDbPerSecond)[] partials,
        Func<double, double>? cents = null,
        double releaseAt = double.MaxValue,
        double startPhase = 0.0)
    {
        int n = (int)(seconds * SampleRate);
        var samples = new float[n];
        var phases = new double[partials.Length];
        for (int p = 0; p < phases.Length; p++)
        {
            phases[p] = startPhase * (p + 1);
        }

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            double ratio = cents is null ? 1.0 : Math.Pow(2.0, cents(t) / 1200.0);
            double attack = Math.Min(1.0, t / 0.004);
            double release = t > releaseAt ? Math.Pow(10.0, -120.0 * (t - releaseAt) / 20.0) : 1.0;
            double sum = 0;
            for (int p = 0; p < partials.Length; p++)
            {
                phases[p] += 2.0 * Math.PI * partials[p].Hz * ratio / SampleRate;
                sum += partials[p].Amplitude * Math.Pow(10.0, -partials[p].DecayDbPerSecond * t / 20.0) * Math.Sin(phases[p]);
            }

            samples[i] = (float)(0.3 * attack * release * sum);
        }

        return samples;
    }

    private static TargetAnalysis Analyze(float[] samples, double f0) =>
        Analyzer.Analyze(new AudioTarget(samples, SampleRate, f0));

    [Fact]
    public void InharmonicPartials_ArePreservedAsTheyAre()
    {
        double[] relative = [1.00, 2.73, 4.41, 6.82];
        float[] samples = Partials(1.5, [.. relative.Select(r => (220.0 * r, 1.0 / r, 6.0))]);

        TargetAnalysis analysis = Analyze(samples, 220.0);

        Assert.Equal(relative.Length, analysis.Partials.Count);
        for (int i = 0; i < relative.Length; i++)
        {
            Assert.InRange(analysis.Partials[i].RelativeFrequency, relative[i] - 0.01, relative[i] + 0.01);
        }

        Assert.True(analysis.Inharmonicity > 0.3, $"inharmonicity {analysis.Inharmonicity}");
    }

    [Fact]
    public void HarmonicTone_HasLowInharmonicityAndCorrectLevels()
    {
        float[] samples = Partials(1.0, [(220, 1.0, 0), (440, 0.5, 0), (660, 0.25, 0)]);

        TargetAnalysis analysis = Analyze(samples, 220.0);

        Assert.Equal(3, analysis.Partials.Count);
        Assert.True(analysis.Inharmonicity < 0.05);
        Assert.InRange(analysis.Partials[0].LevelDb - analysis.Partials[1].LevelDb, 5.0, 7.0);
        Assert.InRange(analysis.Partials[1].LevelDb - analysis.Partials[2].LevelDb, 5.0, 7.0);
    }

    /// <summary>Gaussian-like noise with a first-order filter: y[n] = x[n] + a·x[n−1] + b·y[n−1].</summary>
    private static float[] Noise(double seconds, double rms, double a = 0.0, double b = 0.0, int seed = 7)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * SampleRate)];
        double previousX = 0, previousY = 0, power = 0;
        var raw = new double[samples.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            double x = random.NextDouble() + random.NextDouble() + random.NextDouble() + random.NextDouble() - 2.0;
            double y = x + (a * previousX) + (b * previousY);
            previousX = x;
            previousY = y;
            raw[i] = y;
            power += y * y;
        }

        double gain = rms / Math.Sqrt(power / raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            samples[i] = (float)(raw[i] * gain);
        }

        return samples;
    }

    private static float[] Mix(float[] x, float[] y) => [.. x.Select((v, i) => v + y[i])];

    private static double Rms(float[] x) => Math.Sqrt(x.Sum(static v => (double)v * v) / x.Length);

    [Fact]
    public void Noise_IsSeparatedFromThePartials()
    {
        float[] tone = Partials(1.5, [(220, 1.0, 0), (440, 0.5, 0), (990, 0.3, 0), (1650, 0.2, 0)]);
        double toneRms = Rms(tone);

        NoiseProfile clean = Analyze(tone, 220.0).Noise;
        // Noise with a third of the power of the tone: a quarter of the total.
        NoiseProfile noisy = Analyze(Mix(tone, Noise(1.5, toneRms / Math.Sqrt(3.0))), 220.0).Noise;
        NoiseProfile pure = Analyze(Noise(1.5, toneRms), 220.0).Noise;

        Assert.InRange(clean.Fraction, 0.0, 0.03);
        Assert.InRange(noisy.Fraction, 0.17, 0.33);
        Assert.InRange(pure.Fraction, 0.85, 1.0);
    }

    [Fact]
    public void Noise_HasTheColourOfItsFilter()
    {
        float[] tone = Partials(1.5, [(220, 1.0, 0), (440, 0.5, 0)]);
        double rms = Rms(tone);

        // The high-passed wash of a cymbal (first difference) and a low-passed rumble (one-pole low-pass).
        NoiseProfile high = Analyze(Mix(tone, Noise(1.5, rms, a: -0.94)), 220.0).Noise;
        NoiseProfile low = Analyze(Mix(tone, Noise(1.5, rms, b: 0.97)), 220.0).Noise;

        Assert.True(high.CentroidHz > 8000, $"high-passed centroid {high.CentroidHz:F0} Hz");
        Assert.True(low.CentroidHz < 2000, $"low-passed centroid {low.CentroidHz:F0} Hz");
    }

    [Fact]
    public void WithoutAKnownFundamental_ItIsEstimated()
    {
        float[] samples = Partials(1.0, [(196, 1.0, 0), (392, 0.5, 0), (588, 0.3, 0)]);

        TargetAnalysis analysis = Analyzer.Analyze(new AudioTarget(samples, SampleRate));

        Assert.InRange(analysis.FundamentalHz, 194.0, 198.0);
        Assert.Equal(1.0, Analyzer.Analyze(new AudioTarget(samples, SampleRate, MidiNote: 55)).FundamentalConfidence);
    }

    [Fact]
    public void PartialTracks_MeasureIndividualDecaysBirthAndDeath()
    {
        float[] samples = Partials(2.0, [(220, 1.0, 6.0), (880, 0.8, 40.0)]);

        TargetAnalysis analysis = Analyze(samples, 220.0);

        PartialTrack low = analysis.PartialTracks.First(t => Math.Abs(t.RelativeFrequency - 1.0) < 0.05);
        PartialTrack high = analysis.PartialTracks.First(t => Math.Abs(t.RelativeFrequency - 4.0) < 0.05);
        Assert.InRange(low.Decay.FastRateDbPerSecond, -8.0, -4.5);
        Assert.InRange(high.Decay.FastRateDbPerSecond, -46.0, -34.0);
        Assert.True(high.DeathSeconds < low.DeathSeconds);
        Assert.True(low.BirthSeconds < 0.05);
        Assert.InRange(Math.Abs(low.FrequencyDriftCents), 0.0, 3.0);
    }

    [Fact]
    public void PartialTracks_MeasureFrequencyDrift()
    {
        float[] samples = Partials(1.5, [(330, 1.0, 3.0)], t => 40.0 * t / 1.5);

        TargetAnalysis analysis = Analyze(samples, 330.0);

        PartialTrack track = Assert.Single(analysis.PartialTracks);
        Assert.InRange(track.FrequencyDriftCents, 28.0, 45.0);
    }

    [Fact]
    public void PartialSetDistance_SeparatesWrongLevelsFromWrongStructure()
    {
        IReadOnlyList<SpectralPartial> target = Set((1, -10), (2, -16), (3, -22), (4, -28));
        IReadOnlyList<SpectralPartial> wrongLevels = Set((1, -10), (2, -28), (3, -12), (4, -20));
        IReadOnlyList<SpectralPartial> wrongStructure = Set((1, -10), (2.4, -16), (3.7, -22), (5.3, -28));

        PartialSetComparison same = PartialSetDistance.Compare(target, target);
        PartialSetComparison levels = PartialSetDistance.Compare(target, wrongLevels);
        PartialSetComparison structure = PartialSetDistance.Compare(target, wrongStructure);

        Assert.Equal(0.0, same.Distance, 9);
        Assert.True(levels.StructureMatches);
        Assert.True(levels.LevelDistance > 0.1 && levels.FrequencyDistance < 0.01);
        Assert.False(structure.StructureMatches);
        Assert.True(structure.Distance > levels.Distance);
        Assert.True(structure.MissingShare > 0.3 && structure.ExtraShare > 0.3);

        static IReadOnlyList<SpectralPartial> Set(params (double Relative, double Db)[] partials) =>
            [.. partials.Select(p => new SpectralPartial(200 * p.Relative, p.Relative, p.Db, 1.0))];
    }

    [Fact]
    public void Trajectories_ShowTheBrightnessDecayThatAnAverageWouldHide()
    {
        // A dark body with bright partials that die within a fraction of a second.
        float[] samples = Partials(1.5, [(220, 1.0, 4.0), (2200, 0.7, 60.0), (3300, 0.6, 60.0)]);

        SpectralTrajectories trajectories = Analyze(samples, 220.0).Trajectories;

        double early = trajectories.Mean(trajectories.CentroidHz, 0.02, 0.1);
        double late = trajectories.Mean(trajectories.CentroidHz, 1.0, 1.4);
        Assert.True(early > 800 && late < 400, $"early {early}, late {late}");
        Assert.True(trajectories.Mean(trajectories.BandwidthHz, 0.02, 0.1) > 3 * trajectories.Mean(trajectories.BandwidthHz, 1.0, 1.4));
        Assert.True(trajectories.Mean(trajectories.SlopeDbPerOctave, 0.02, 0.1) > trajectories.Mean(trajectories.SlopeDbPerOctave, 1.0, 1.4));

        BandTrajectory upper = trajectories.Bands.First(b => b.Band.Name == "mid");
        BandTrajectory bass = trajectories.Bands.First(b => b.Band.Name == "bass");
        Assert.InRange(upper.Decay.FastRateDbPerSecond, -70.0, -50.0);
        Assert.InRange(bass.Decay.FastRateDbPerSecond, -6.0, -2.5);
        Assert.True(upper.LateDb < upper.InitialDb - 30);
    }

    [Fact]
    public void Flux_IsHighWhereTheSpectrumChanges()
    {
        var samples = new float[SampleRate];
        for (int i = 0; i < samples.Length; i++)
        {
            double hz = i < samples.Length / 2 ? 300 : 2400;
            samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / SampleRate));
        }

        SpectralTrajectories trajectories = Analyze(samples, 300.0).Trajectories;

        int change = (int)(0.5 / trajectories.FrameSeconds);
        float atChange = trajectories.Flux.Skip(change - 2).Take(5).Max();
        float steady = trajectories.Flux.Skip(10).Take(change - 20).Max();
        Assert.True(atChange > 0.3 && steady < 0.05, $"at change {atChange}, steady {steady}");
    }

    [Fact]
    public void MultiStageDecay_FindsFastAndSlowStage()
    {
        // Fast 60 dB/s for 0.25 s, then slow 5 dB/s: the electric-piano shape.
        var levels = new float[400];
        for (int i = 0; i < levels.Length; i++)
        {
            double t = i * 0.005;
            levels[i] = (float)(t < 0.25 ? -60.0 * t : -15.0 - (5.0 * (t - 0.25)));
        }

        DecayModel two = MultiStageDecay.Fit(levels, 0.005);
        DecayModel one = MultiStageDecay.Fit(levels.AsSpan(60), 0.005);

        Assert.True(two.IsTwoStage);
        Assert.InRange(two.FastRateDbPerSecond, -63.0, -57.0);
        Assert.InRange(two.SlowRateDbPerSecond, -5.5, -4.5);
        Assert.InRange(two.BreakSeconds, 0.22, 0.28);
        Assert.InRange(two.BreakLevelDb, -16.5, -13.5);
        Assert.False(one.IsTwoStage);
        Assert.InRange(one.FastRateDbPerSecond, -5.5, -4.5);
    }

    [Fact]
    public void AmplitudeDecayOfATarget_IsTwoStageForAStruckSound()
    {
        float[] samples = Partials(2.0, [(220, 0.25, 3.0), (440, 1.0, 50.0)]);

        TargetAnalysis analysis = Analyze(samples, 220.0);

        Assert.True(analysis.AmplitudeDecay.IsTwoStage);
        Assert.True(analysis.AmplitudeDecay.FastRateDbPerSecond < -25);
        Assert.InRange(analysis.AmplitudeDecay.SlowRateDbPerSecond, -6.0, -1.5);
    }

    [Fact]
    public void Pitch_MeasuresAttackOffsetAndItsDecay()
    {
        float[] samples = Partials(1.0, [(220, 1.0, 0), (440, 0.4, 0)], t => 60.0 * Math.Exp(-t / 0.04));

        PitchTrajectory pitch = Analyze(samples, 220.0).Pitch;

        Assert.InRange(pitch.AttackOffsetCents, 25.0, 60.0);
        Assert.InRange(pitch.PitchDecaySeconds, 0.05, 0.2);
        Assert.InRange(pitch.ReferenceHz, 219.5, 220.5);
        Assert.True(pitch.HasMovement);
        Assert.Equal(0.0, pitch.VibratoDepthCents);
    }

    [Fact]
    public void Pitch_MeasuresVibratoRateAndDepth()
    {
        float[] samples = Partials(2.0, [(330, 1.0, 0), (660, 0.5, 0)], t => 20.0 * Math.Sin(2 * Math.PI * 5.5 * t));

        PitchTrajectory pitch = Analyze(samples, 330.0).Pitch;

        Assert.InRange(pitch.VibratoRateHz, 5.2, 5.8);
        Assert.InRange(pitch.VibratoDepthCents, 16.0, 24.0);
        Assert.True(pitch.VibratoConfidence > 0.7);
        Assert.True(pitch.HasMovement);
    }

    [Fact]
    public void Pitch_OfASteadyToneHasNoMovementAndDriftIsMeasured()
    {
        PitchTrajectory steady = Analyze(Partials(1.0, [(220, 1.0, 0)]), 220.0).Pitch;
        PitchTrajectory drifting = Analyze(Partials(2.0, [(220, 1.0, 0)], t => 15.0 * t), 220.0).Pitch;

        Assert.False(steady.HasMovement);
        Assert.InRange(steady.DriftCentsPerSecond, -1.0, 1.0);
        Assert.InRange(drifting.DriftCentsPerSecond, 12.0, 18.0);
    }

    [Fact]
    public void PitchMovement_IsNotBelievedForInharmonicNotes()
    {
        // Partials that decay at different speeds move the period of an inharmonic note around; that is not a pitch.
        float[] bell = Partials(2.0, [(330, 1.0, 4), (911, 0.6, 9), (1455, 0.5, 14), (2251, 0.4, 20)]);
        float[] gliding = Partials(1.0, [(220, 1.0, 0), (440, 0.4, 0)], t => 60.0 * Math.Exp(-t / 0.04));

        TargetAnalysis inharmonic = Analyze(bell, 330.0);

        Assert.True(inharmonic.Inharmonicity > 0.15);
        Assert.False(inharmonic.HasPitchMovement);
        Assert.True(Analyze(gliding, 220.0).HasPitchMovement);
    }

    [Fact]
    public void Beating_OfTwoDetunedPartialsIsDetected()
    {
        TargetAnalysis beating = Analyze(Partials(2.5, [(220, 1.0, 0), (222.5, 0.6, 0)]), 220.0);
        TargetAnalysis steady = Analyze(Partials(2.5, [(220, 1.0, 0), (440, 0.6, 0)]), 220.0);

        Assert.True(beating.Beating.IsPresent);
        Assert.InRange(beating.Beating.RateHz, 2.3, 2.7);
        Assert.True(beating.Beating.DepthDb > 3);
        Assert.False(steady.Beating.IsPresent);
    }

    [Fact]
    public void NoteOffAndReleaseTail_AreDetected()
    {
        float[] samples = Partials(1.2, [(220, 1.0, 0), (440, 0.5, 0)], releaseAt: 0.8);

        TargetAnalysis analysis = Analyze(samples, 220.0);

        Assert.InRange(analysis.NoteOffSeconds, 0.74, 0.86);
        Assert.True(analysis.ReleaseTailSeconds > 0.2);
    }

    [Fact]
    public void Ltas_IgnoresPhaseButNotSpectrum()
    {
        var objective = new AudioObjective();
        float[] reference = Partials(1.0, [(220, 1.0, 3), (228, 0.7, 3), (660, 0.4, 3)]);
        float[] shifted = Partials(1.0, [(220, 1.0, 3), (228, 0.7, 3), (660, 0.4, 3)], startPhase: 1.9);
        float[] brighter = Partials(1.0, [(220, 1.0, 3), (228, 0.7, 3), (660, 0.4, 3), (2640, 0.8, 3)]);

        LongTermAverageSpectrum a = Analyze(reference, 220).Ltas;
        LongTermAverageSpectrum b = Analyze(shifted, 220).Ltas;
        LongTermAverageSpectrum c = Analyze(brighter, 220).Ltas;

        Assert.True(a.DistanceTo(b) < 0.05, $"shifted {a.DistanceTo(b)}");
        Assert.True(a.DistanceTo(c) > 3 * a.DistanceTo(b), $"brighter {a.DistanceTo(c)}");
        Assert.NotNull(objective);
    }

    [Fact]
    public void Objective_IsZeroForTheTargetItselfAndGrowsWithTheDifference()
    {
        var objective = new AudioObjective();
        (double, double, double)[] target = [(220, 1.0, 5), (440, 0.6, 12), (660, 0.4, 20), (880, 0.2, 30)];
        (double, double, double)[] close = [(220, 1.0, 5), (440, 0.5, 12), (660, 0.4, 24), (880, 0.2, 30)];
        (double, double, double)[] far = [(220, 1.0, 1), (587, 0.9, 2), (1340, 0.8, 2)];
        PreparedObjectiveTarget prepared = objective.Prepare(new AudioTarget(Partials(1.5, target), SampleRate, 220.0));

        foreach (EvaluationLevel level in Enum.GetValues<EvaluationLevel>())
        {
            double same = objective.Evaluate(prepared, new AudioNote(Partials(1.5, target), SampleRate, 220.0), level).Loss;
            double near = objective.Evaluate(prepared, new AudioNote(Partials(1.5, close), SampleRate, 220.0), level).Loss;
            double distant = objective.Evaluate(prepared, new AudioNote(Partials(1.5, far), SampleRate, 220.0), level).Loss;

            Assert.True(same < 1e-6, $"{level}: same {same}");
            Assert.True(near > same && distant > 2 * near, $"{level}: near {near}, distant {distant}");
        }
    }

    [Fact]
    public void Objective_ComparesThePartialsOfTheBody_NotOnlyThoseOfTheWholeNote()
    {
        // A bell: sustained inharmonic partials, and a burst of other partials that is over within 0.1 s.
        (double, double, double)[] body = [(520, 1.0, 30), (941, 0.8, 32), (1733, 0.7, 33), (2399, 0.6, 34)];
        (double, double, double)[] burst = [(731, 1.0, 400), (1140, 1.0, 400), (1511, 1.0, 400), (2671, 1.0, 400), (3539, 1.0, 400)];
        var objective = new AudioObjective();
        PreparedObjectiveTarget prepared = objective.Prepare(new AudioTarget(Partials(1.5, [.. body, .. burst]), SampleRate, 220.0));

        ObjectiveTerms Terms((double, double, double)[] partials) =>
            objective.Evaluate(prepared, new AudioNote(Partials(1.5, partials), SampleRate, 220.0), EvaluationLevel.Normal).Terms;
        ObjectiveTerms same = Terms([.. body, .. burst]);
        ObjectiveTerms bodyOnly = Terms(body);
        ObjectiveTerms burstHeld = Terms([.. burst.Select(static p => (p.Item1, p.Item2, 30.0))]);

        Assert.True(same.BodyPartial < 1e-6, $"same {same.BodyPartial}");
        Assert.True(bodyOnly.BodyPartial < 0.1, $"body only {bodyOnly.BodyPartial}");
        Assert.True(burstHeld.BodyPartial > 0.8, $"burst held {burstHeld.BodyPartial}");
        Assert.True(double.IsNaN(objective.Evaluate(prepared, new AudioNote(Partials(1.5, body), SampleRate, 220.0), EvaluationLevel.Coarse).Terms.BodyPartial));
    }

    [Fact]
    public void Objective_PenalizesUnusableCandidatesAndIgnoresLevelWhenTimbreNormalized()
    {
        var objective = new AudioObjective();
        float[] tone = Partials(1.0, [(220, 1.0, 5), (440, 0.6, 12)]);
        PreparedObjectiveTarget prepared = objective.Prepare(new AudioTarget(tone, SampleRate, 220.0));
        float[] quiet = [.. tone.Select(static s => s * 0.1f)];

        ObjectiveResult silent = objective.Evaluate(prepared, new AudioNote(new float[SampleRate], SampleRate, 220.0));
        ObjectiveResult scaled = objective.Evaluate(prepared, new AudioNote(quiet, SampleRate, 220.0));
        PreparedObjectiveTarget absolute = objective.Prepare(new AudioTarget(tone, SampleRate, 220.0), new ObjectiveOptions { TimbreNormalized = false });
        ObjectiveResult scaledAbsolute = objective.Evaluate(absolute, new AudioNote(quiet, SampleRate, 220.0));

        Assert.False(silent.IsUsable);
        Assert.Equal(10.0, silent.Loss);
        Assert.True(scaled.Loss < 1e-4, $"scaled {scaled.Loss}");
        Assert.True(scaledAbsolute.Loss > 0.2, $"absolute {scaledAbsolute.Loss}");
    }
}
