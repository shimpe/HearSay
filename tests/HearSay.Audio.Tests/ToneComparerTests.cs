using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Features;
using HearSay.Models;

namespace HearSay.Tests;

public class ToneComparerTests
{
    private static readonly ToneComparer Comparer = new();
    private static readonly double[] Saw = TestSignals.Rolloff(20, 1.0);

    [Fact]
    public void IdenticalSignals_GiveNearPerfectScore()
    {
        float[] tone = TestSignals.Tone(440, Saw);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(tone), TestSignals.Note((float[])tone.Clone()));

        Assert.True(result.Scores.Overall < 1e-6, $"overall {result.Scores.Overall}");
        Assert.True(result.Scores.Similarity > 0.999);
        Assert.Empty(result.Differences);
        Assert.True(result.Quality.IsUsable);
        Assert.All(result.FeatureVector.Values.ToArray(), v => Assert.Equal(0.0, v, 6));
    }

    [Fact]
    public void AnotherRealizationOfTheSameNoise_IsNearlyTheSameSound()
    {
        float[] tone = TestSignals.Tone(220, TestSignals.Rolloff(8, 1.0), seconds: 1.5);
        float[] target = TestSignals.AddNoise(tone, 0.04, seed: 1);

        double Similarity(float[] candidate) => Comparer.Compare(TestSignals.Note(target, 220), TestSignals.Note(candidate, 220)).Scores.Similarity;
        double realization = Similarity(TestSignals.AddNoise(tone, 0.04, seed: 2));
        double noNoise = Similarity(tone);
        double louderNoise = Similarity(TestSignals.AddNoise(tone, 0.12, seed: 2));

        Assert.True(realization > 0.88, $"another realization {realization:F3}");
        Assert.True(noNoise < realization - 0.05, $"no noise {noNoise:F3}, realization {realization:F3}");
        Assert.True(louderNoise < realization - 0.05, $"louder noise {louderNoise:F3}, realization {realization:F3}");
    }

    [Fact]
    public void PhaseShiftedSine_IsNearlyIdenticalInTimbre()
    {
        float[] target = TestSignals.Tone(440, [1.0]);
        float[] candidate = TestSignals.Tone(440, [1.0], phase: Math.PI / 2);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        Assert.True(result.Scores.Spectral < 0.02, $"spectral {result.Scores.Spectral}");
        Assert.True(result.Scores.Harmonic < 0.02, $"harmonic {result.Scores.Harmonic}");
        Assert.True(result.Scores.Overall < 0.02, $"overall {result.Scores.Overall}");
        Assert.True(Math.Abs(result.DifferenceVector.SpectralCentroidDelta) < 5.0);
    }

    [Fact]
    public void PhaseShiftedComplexTone_IsNearlyIdentical_WhileWaveformsDiffer()
    {
        float[] target = TestSignals.Tone(220, Saw);
        float[] candidate = TestSignals.Tone(220, Saw, phase: 1.3);
        double sampleError = 0;
        for (int i = 0; i < target.Length; i++)
        {
            sampleError += Math.Abs(target[i] - candidate[i]);
        }

        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target, 220), TestSignals.Note(candidate, 220));

        Assert.True(sampleError / target.Length > 0.01, "the waveforms should differ sample by sample");
        Assert.True(result.Scores.Overall < 0.03, $"overall {result.Scores.Overall}");
    }

    [Fact]
    public void LouderCandidate_IsReportedAsLoudness_NotAsTimbre()
    {
        float[] target = TestSignals.Tone(440, Saw);
        float[] candidate = TestSignals.Scale(target, 6.0);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        Assert.InRange(result.DifferenceVector.LoudnessDelta, 5.9, 6.1);
        Assert.True(result.Scores.Spectral < 0.005, $"spectral {result.Scores.Spectral}");
        Assert.True(result.Scores.Harmonic < 0.005);
        Assert.True(result.Scores.Loudness > 0.25);
        ToneDifference loudness = Assert.Single(result.Differences);
        Assert.Equal(ToneFeatureIds.Loudness, loudness.Feature);
        Assert.Equal(DifferenceDirection.TooHigh, loudness.Direction);
    }

    [Fact]
    public void TimbreOnlyMode_IgnoresLoudness_AbsoluteModeIncludesIt()
    {
        float[] target = TestSignals.Tone(440, Saw);
        float[] candidate = TestSignals.Scale(target, 6.0);

        ToneComparisonResult timbre = Comparer.Compare(
            TestSignals.Note(target), TestSignals.Note(candidate), new ToneComparisonOptions { LoudnessMode = LoudnessMode.TimbreOnly });
        ToneComparisonResult absolute = Comparer.Compare(
            TestSignals.Note(target), TestSignals.Note(candidate), new ToneComparisonOptions { LoudnessMode = LoudnessMode.Absolute });

        Assert.True(timbre.Scores.Overall < 0.005);
        Assert.Empty(timbre.Differences);
        Assert.InRange(timbre.DifferenceVector.LoudnessDelta, 5.9, 6.1);
        Assert.True(absolute.Scores.Spectral > 0.2, $"absolute spectral {absolute.Scores.Spectral}");
    }

    [Fact]
    public void StrongerSecondHarmonic_IsReportedForThatHarmonic()
    {
        float[] target = TestSignals.Tone(440, [1.0, 0.3]);
        float[] candidate = TestSignals.Tone(440, [1.0, 0.6]);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        HarmonicComparison second = result.Harmonics[1];
        Assert.Equal(2, second.HarmonicNumber);
        Assert.InRange(second.FrequencyHz, 878, 882);
        Assert.InRange(second.DifferenceDb, 4.0, 6.5);
        Assert.True(result.Harmonics[0].DifferenceDb < 0, "the fundamental is relatively weaker");

        ToneDifference difference = Assert.Single(result.Differences, d => d.Feature == ToneFeatureIds.Harmonic(2));
        Assert.Equal(DifferenceDirection.TooStrong, difference.Direction);
        Assert.InRange(result.DifferenceVector.HarmonicDeltas[1], 4.0, 6.5);
    }

    [Fact]
    public void BrighterCandidate_RaisesCentroidAndHighBand()
    {
        float[] target = TestSignals.Tone(220, TestSignals.Rolloff(30, 2.0));
        float[] candidate = TestSignals.Tone(220, TestSignals.Rolloff(30, 1.0));
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target, 220), TestSignals.Note(candidate, 220));

        Assert.True(result.DifferenceVector.SpectralCentroidDelta > 100, $"centroid {result.DifferenceVector.SpectralCentroidDelta}");
        Assert.True(result.DifferenceVector.HighBandDelta > 3, $"high band {result.DifferenceVector.HighBandDelta}");
        Assert.True(result.DifferenceVector.HarmonicSlopeDelta > 1);
        Assert.Contains(result.Differences, d => d.Feature == ToneFeatureIds.SpectralCentroid && d.Direction == DifferenceDirection.TooHigh);
        Assert.Contains(result.Differences, d => d.FrequencyStartHz >= 2000 && d.Direction == DifferenceDirection.TooStrong);
        Assert.Contains(result.ParameterHints, h =>
            h.SuggestedDirection == ParameterAdjustmentDirection.DecreaseBrightness
            && h.PotentialParameterCategories.Contains(SynthParameterCategories.FilterCutoff)
            && h.Confidence <= 0.5);
    }

    [Fact]
    public void SlowerAttack_IsDetected()
    {
        float[] target = TestSignals.Tone(440, Saw, attackMs: 10);
        float[] candidate = TestSignals.Tone(440, Saw, attackMs: 80);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        Assert.InRange(result.Envelope.Attack.Difference!.Value, 55, 85);
        Assert.InRange(result.DifferenceVector.AttackDelta, 55, 85);
        Assert.Equal(ToneFeatureIds.AttackTime, result.Differences[0].Feature);
        Assert.Equal(DifferenceDirection.TooLong, result.Differences[0].Direction);
        Assert.True(result.Scores.Envelope > result.Scores.Harmonic);
    }

    [Fact]
    public void LongerRelease_IsDetected()
    {
        float[] target = TestSignals.Tone(440, Saw, seconds: 1.0, releaseMs: 50);
        float[] candidate = TestSignals.Tone(440, Saw, seconds: 1.25, releaseMs: 300);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        Assert.True(result.Envelope.Release.Difference > 150, $"release delta {result.Envelope.Release.Difference}");
        ToneDifference release = Assert.Single(result.Differences, d => d.Feature == ToneFeatureIds.ReleaseTime);
        Assert.Equal(DifferenceDirection.TooLong, release.Direction);
        Assert.True(Math.Abs(result.Envelope.Attack.Difference!.Value) < 3);
    }

    [Fact]
    public void BroadbandNoise_LowersHnrAndRaisesFlatness()
    {
        float[] target = TestSignals.Tone(440, Saw);
        float[] candidate = TestSignals.AddNoise(target, 0.01);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        Assert.True(result.DifferenceVector.HnrDelta < -10, $"hnr {result.DifferenceVector.HnrDelta}");
        Assert.True(result.DifferenceVector.SpectralFlatnessDelta > 5, $"flatness {result.DifferenceVector.SpectralFlatnessDelta}");
        Assert.Contains(result.Differences, d => d.Feature == ToneFeatureIds.HarmonicToNoiseRatio && d.Direction == DifferenceDirection.TooLow);
    }

    [Fact]
    public void SmallTimingOffset_DoesNotCauseFalseDifference()
    {
        float[] target = TestSignals.Tone(440, Saw);
        float[] candidate = TestSignals.Delay(target, 7.0);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        Assert.True(result.Scores.Overall < 0.02, $"overall {result.Scores.Overall}");
        Assert.InRange(result.Quality.CandidateOnsetMs - result.Quality.TargetOnsetMs, 5.0, 9.0);
        Assert.True(Math.Abs(result.Envelope.Attack.Difference!.Value) < 2);
    }

    [Fact]
    public void SpectralMatchWithDifferentEnvelope_IsSeparatedIntoComponents()
    {
        float[] target = TestSignals.Tone(440, Saw, attackMs: 5, decayMs: 100, sustain: 0.5);
        float[] candidate = TestSignals.Tone(440, Saw, attackMs: 5, decayMs: 100, sustain: 0.9);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        Assert.InRange(result.Envelope.Sustain.Difference!.Value, 4.0, 6.0);
        Assert.True(result.Scores.Harmonic < 0.01, $"harmonic {result.Scores.Harmonic}");
        Assert.Contains(result.Differences, d => d.Feature == ToneFeatureIds.SustainLevel && d.Direction == DifferenceDirection.TooHigh);
    }

    [Fact]
    public void BrightAttackOnly_IsLocalizedInTime()
    {
        const double F0 = 220;
        float[] target = TestSignals.Tone(F0, TestSignals.Rolloff(8, 2.0), attackMs: 5);
        var candidate = (float[])target.Clone();
        for (int i = 0; i < candidate.Length; i++)
        {
            double t = (double)i / TestSignals.SampleRate;
            double burst = Math.Exp(-t / 0.03) * Math.Min(1.0, t / 0.005);
            candidate[i] += (float)(0.1 * burst * (Math.Sin(2 * Math.PI * F0 * 20 * t) + Math.Sin(2 * Math.PI * F0 * 24 * t)));
        }

        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target, F0), TestSignals.Note(candidate, F0));
        TimeSegmentComparison attack = Assert.Single(result.TimeSegments, s => s.Segment == TimeSegments.Attack);
        TimeSegmentComparison sustain = Assert.Single(result.TimeSegments, s => s.Segment == TimeSegments.Sustain);

        Assert.True(attack.CentroidDifferenceHz > 5 * Math.Abs(sustain.CentroidDifferenceHz) + 100, $"attack {attack.CentroidDifferenceHz}, sustain {sustain.CentroidDifferenceHz}");
        Assert.Contains(result.Differences, d => d.TimeRegion == TimeSegments.Attack && d.Direction is DifferenceDirection.TooStrong or DifferenceDirection.TooHigh);
    }

    [Fact]
    public void Differences_AreSortedByImportance_AndBounded()
    {
        float[] target = TestSignals.Tone(220, TestSignals.Rolloff(30, 2.0), attackMs: 10);
        float[] candidate = TestSignals.AddNoise(TestSignals.Tone(220, TestSignals.Rolloff(30, 1.0), attackMs: 60), 0.003);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target, 220), TestSignals.Note(candidate, 220));

        Assert.NotEmpty(result.Differences);
        for (int i = 1; i < result.Differences.Count; i++)
        {
            Assert.True(result.Differences[i - 1].Importance >= result.Differences[i].Importance);
        }

        Assert.All(result.Differences, d =>
        {
            Assert.InRange(d.Importance, 0.0, 1.0);
            Assert.InRange(d.Confidence, 0.0, 1.0);
        });
    }

    [Fact]
    public void FundamentalIsEstimated_WhenNotProvided()
    {
        float[] tone = TestSignals.Tone(220, Saw);
        PreparedTarget prepared = Comparer.PrepareTarget(TestSignals.Note(tone, null));

        Assert.InRange(prepared.FundamentalHz, 218, 222);
        Assert.True(prepared.FundamentalConfidence > 0.8);
    }

    [Fact]
    public void DifferentSampleRates_AreResampled()
    {
        float[] target = TestSignals.Tone(440, TestSignals.Rolloff(10, 1.5));
        float[] candidate = TestSignals.Tone(440, TestSignals.Rolloff(10, 1.5), sampleRate: 48000);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate, 440, 48000));

        Assert.True(result.Scores.Overall < 0.03, $"overall {result.Scores.Overall}");
        Assert.Contains(result.Quality.Warnings, w => w.Code == AnalysisWarningCodes.Resampled && w.Source == "candidate");
    }

    [Fact]
    public void UnusableCandidate_IsReportedWithoutThrowing()
    {
        PreparedTarget prepared = Comparer.PrepareTarget(TestSignals.Note(TestSignals.Tone(440, Saw)));

        ToneComparisonResult silent = Comparer.Compare(prepared, TestSignals.Note(new float[44100]));
        ToneComparisonResult tooShort = Comparer.Compare(prepared, TestSignals.Note(TestSignals.Tone(440, Saw, seconds: 0.01, attackMs: 1, releaseMs: 1)));
        var invalid = new float[44100];
        Array.Fill(invalid, float.NaN);
        ToneComparisonResult nan = Comparer.Compare(prepared, TestSignals.Note(invalid));

        Assert.False(silent.Quality.IsUsable);
        Assert.Equal(prepared.Options.UnusableCandidateDistance, silent.Scores.Overall);
        Assert.Contains(silent.Quality.Warnings, w => w.Code == AnalysisWarningCodes.Silent);
        Assert.False(tooShort.Quality.IsUsable);
        Assert.Contains(tooShort.Quality.Warnings, w => w.Code == AnalysisWarningCodes.TooShort);
        Assert.False(nan.Quality.IsUsable);
        Assert.NotNull(silent.ToString());
    }

    [Fact]
    public void UnusableTarget_Throws()
    {
        Assert.Throws<ArgumentException>(() => Comparer.PrepareTarget(TestSignals.Note(new float[44100])));
    }

    [Fact]
    public void ClippedCandidate_RaisesWarning()
    {
        float[] target = TestSignals.Tone(440, [1.0], gain: 0.5);
        float[] candidate = TestSignals.Scale(target, 12.0);
        for (int i = 0; i < candidate.Length; i++)
        {
            candidate[i] = Math.Clamp(candidate[i], -1f, 1f);
        }

        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        Assert.Contains(result.Quality.Warnings, w => w.Code == AnalysisWarningCodes.Clipping && w.Source == "candidate");
        Assert.True(result.Quality.OverallConfidence < 1.0);
        Assert.Contains(result.Differences, d => d.Feature == ToneFeatureIds.Harmonic(3) && d.Direction == DifferenceDirection.TooStrong);
    }

    [Fact]
    public void Result_RoundTripsThroughJson()
    {
        float[] target = TestSignals.Tone(440, Saw, attackMs: 10);
        float[] candidate = TestSignals.Tone(440, TestSignals.Rolloff(20, 1.5), attackMs: 40);
        ToneComparisonResult result = Comparer.Compare(TestSignals.Note(target), TestSignals.Note(candidate));

        string json = HearSayJson.Serialize(result);
        ToneComparisonResult restored = HearSayJson.Deserialize<ToneComparisonResult>(json);

        Assert.Equal(result.Scores, restored.Scores);
        Assert.Equal(result.Differences.Count, restored.Differences.Count);
        Assert.Equal(result.Differences[0], restored.Differences[0]);
        Assert.Equal(result.FeatureVector.Values.ToArray(), restored.FeatureVector.Values.ToArray());
        Assert.Equal(json, HearSayJson.Serialize(restored));
        Assert.Contains("\"differenceVector\"", json);
    }

    [Fact]
    public void Comparison_IsDeterministic_AndThreadSafe()
    {
        PreparedTarget prepared = Comparer.PrepareTarget(TestSignals.Note(TestSignals.Tone(440, Saw)));
        AudioNote candidate = TestSignals.Note(TestSignals.AddNoise(TestSignals.Tone(440, TestSignals.Rolloff(20, 1.4), attackMs: 30), 0.002));
        string expected = HearSayJson.Serialize(Comparer.Compare(prepared, candidate));

        var results = new string[16];
        Parallel.For(0, results.Length, i => results[i] = HearSayJson.Serialize(Comparer.Compare(prepared, candidate)));

        Assert.All(results, json => Assert.Equal(expected, json));
    }

    [Fact]
    public void FeatureVector_FollowsSchema_AndNormalization()
    {
        var options = new ToneComparisonOptions();
        PreparedTarget prepared = Comparer.PrepareTarget(TestSignals.Note(TestSignals.Tone(440, Saw)), options);
        ToneComparisonResult result = Comparer.Compare(prepared, TestSignals.Note(TestSignals.Tone(440, TestSignals.Rolloff(20, 2.0), attackMs: 50)));
        FeatureVectorSchema schema = prepared.Schema;

        Assert.Equal(schema.Features.Select(f => f.Id), result.FeatureVector.FeatureIds);
        Assert.Equal(schema.Features.Select(f => f.Id), FeatureSchemaBuilder.Build(options).Features.Select(f => f.Id));
        Assert.Equal(schema.Features.Count, schema.Features.Select(f => f.Id).Distinct().Count());
        for (int i = 0; i < schema.Features.Count; i++)
        {
            double expected = Math.Clamp(result.FeatureVector.Values.Span[i] / schema.Features[i].TypicalScale, -10, 10);
            Assert.Equal(expected, result.NormalizedDifferenceVector.Values.Span[i], 9);
        }

        Assert.Equal(result.DifferenceVector.AttackDelta, result.FeatureVector.Get(ToneFeatureIds.AttackTime));
        Assert.Equal(result.DifferenceVector.HighBandDelta, result.FeatureVector.Get(ToneFeatureIds.HighBandEnergy));

        NormalizedFeatureVector weighted = FeatureSchemaBuilder.ApplyWeights(
            result.NormalizedDifferenceVector, [new OptimizationFeatureWeight(ToneFeatureIds.AttackTime, 0.0)], 2.0);
        int attackIndex = schema.IndexOf(ToneFeatureIds.AttackTime);
        Assert.Equal(0.0, weighted.Values.Span[attackIndex]);
        Assert.Equal(2.0 * result.NormalizedDifferenceVector.Values.Span[0], weighted.Values.Span[0], 9);
    }
}
