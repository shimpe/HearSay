using HearSay.Features;
using HearSay.Models;

namespace HearSay.Comparison;

/// <summary>All raw comparison measurements from which actionable differences are derived.</summary>
/// <param name="LoudnessDeltaDb">Loudness difference in dB.</param>
/// <param name="PitchDeltaCents">Pitch difference in cents.</param>
/// <param name="Envelope">Envelope comparison.</param>
/// <param name="Harmonics">Per-harmonic comparison.</param>
/// <param name="Descriptors">Scalar descriptor comparisons, keyed by their feature id.</param>
/// <param name="FrequencyBands">Whole-note differences of the configured bands.</param>
/// <param name="TimeSegments">Per-segment comparisons.</param>
/// <param name="Mel">Log-Mel comparison.</param>
/// <param name="MelBandDifferencesDb">Time-averaged difference per Mel band, dB.</param>
/// <param name="MelEdgesHz">Edges of the Mel bands (band count + 2 values).</param>
/// <param name="MeasurementConfidence">Confidence 0..1 shared by all spectral measurements (input quality and F0 confidence).</param>
public sealed record ToneMeasurements(
    double LoudnessDeltaDb,
    double PitchDeltaCents,
    EnvelopeComparison Envelope,
    IReadOnlyList<HarmonicComparison> Harmonics,
    IReadOnlyList<DescriptorComparison> Descriptors,
    IReadOnlyList<FrequencyBandDifference> FrequencyBands,
    IReadOnlyList<TimeSegmentComparison> TimeSegments,
    MelComparison Mel,
    IReadOnlyList<double> MelBandDifferencesDb,
    IReadOnlyList<double> MelEdgesHz,
    double MeasurementConfidence);

/// <summary>Turns raw measurements into ranked, machine-readable differences and generic parameter hints.</summary>
public interface IToneDifferenceAnalyzer
{
    /// <summary>Derives the differences, sorted by descending importance.</summary>
    /// <param name="measurements">Raw measurements.</param>
    /// <param name="options">Comparison configuration.</param>
    IReadOnlyList<ToneDifference> Analyze(ToneMeasurements measurements, ToneComparisonOptions options);

    /// <summary>Creates generic, low-confidence hints for the most important differences.</summary>
    /// <param name="differences">Differences, sorted by importance.</param>
    IReadOnlyList<ParameterHint> CreateHints(IReadOnlyList<ToneDifference> differences);
}

/// <summary>
/// Default difference analyzer. Importance is continuous:
/// <c>importance = (1 - exp(-size / scale)) * perceptualWeight * persistence * sqrt(confidence)</c>,
/// where size is the difference in dB, or a log-ratio for times and frequencies. Stateless and thread-safe.
/// </summary>
public sealed class ToneDifferenceAnalyzer : IToneDifferenceAnalyzer
{
    private const double InaudibleRelativeDb = -60.0;
    private const int MaximumHints = 12;

    /// <inheritdoc />
    public IReadOnlyList<ToneDifference> Analyze(ToneMeasurements measurements, ToneComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(options);
        DifferenceOptions thresholds = options.Differences;
        double confidence = measurements.MeasurementConfidence;
        var result = new List<ToneDifference>();

        // Loudness and pitch.
        if (options.LoudnessMode != LoudnessMode.TimbreOnly && Math.Abs(measurements.LoudnessDeltaDb) >= thresholds.MinimumLevelDifferenceDb)
        {
            AddLevel(result, ToneFeatureIds.Loudness, measurements.LoudnessDeltaDb, "dB", 6.0, 0.6, confidence, false);
        }

        if (Math.Abs(measurements.PitchDeltaCents) >= thresholds.MinimumPitchDifferenceCents)
        {
            AddLevel(result, ToneFeatureIds.Pitch, measurements.PitchDeltaCents, "cents", 15.0, 0.9, confidence, false);
        }

        // Envelope.
        EnvelopeComparison envelope = measurements.Envelope;
        AddTime(result, ToneFeatureIds.AttackTime, envelope.Attack, 5.0, 1.0, thresholds);
        AddTime(result, ToneFeatureIds.DecayTime, envelope.Decay, 20.0, 0.7, thresholds);
        AddTime(result, ToneFeatureIds.ReleaseTime, envelope.Release, 20.0, 0.7, thresholds);
        if (envelope.Sustain.Difference is double sustainDelta && Math.Abs(sustainDelta) >= thresholds.MinimumLevelDifferenceDb)
        {
            AddLevel(result, ToneFeatureIds.SustainLevel, sustainDelta, "dB", 6.0, 0.8, envelope.Sustain.Confidence, false);
        }

        if (envelope.TargetHasSustain != envelope.CandidateHasSustain)
        {
            result.Add(new ToneDifference(ToneFeatureIds.EnvelopeShape, DifferenceDirection.Different, 0.0, string.Empty, 0.8 * Math.Sqrt(confidence), confidence, 0.0));
        }

        // Scalar descriptors.
        foreach (DescriptorComparison d in measurements.Descriptors)
        {
            switch (d.FeatureId)
            {
                case ToneFeatureIds.SpectralCentroid:
                    AddFrequency(result, d, 0.9, confidence, thresholds);
                    break;
                case ToneFeatureIds.SpectralRolloff:
                    AddFrequency(result, d, 0.5, confidence, thresholds);
                    break;
                case ToneFeatureIds.SpectralBandwidth:
                    AddFrequency(result, d, 0.4, confidence, thresholds);
                    break;
                case ToneFeatureIds.SpectralFlatness:
                    AddThresholded(result, d, 2.0, 6.0, 0.5, confidence);
                    break;
                case ToneFeatureIds.SpectralSlope:
                    AddThresholded(result, d, 1.0, 3.0, 0.6, confidence);
                    break;
                case ToneFeatureIds.SpectralFlux:
                    AddThresholded(result, d, 0.03, 0.1, 0.4, confidence);
                    break;
                case ToneFeatureIds.HarmonicToNoiseRatio:
                    AddThresholded(result, d, 3.0, 10.0, 0.8, confidence);
                    break;
                case ToneFeatureIds.Inharmonicity:
                    AddThresholded(result, d, 3.0, 10.0, 0.6, confidence);
                    break;
                case ToneFeatureIds.HarmonicSlope:
                    AddThresholded(result, d, 1.0, 3.0, 0.7, confidence);
                    break;
                case ToneFeatureIds.EvenOddRatio:
                    AddThresholded(result, d, 2.0, 6.0, 0.7, confidence, energy: true);
                    break;
                case ToneFeatureIds.LowHarmonics:
                case ToneFeatureIds.HighHarmonics:
                    if (Math.Max(d.Target, d.Candidate) > InaudibleRelativeDb)
                    {
                        AddThresholded(result, d, 2.0, 6.0, 0.7, confidence, energy: true);
                    }

                    break;
                default:
                    break;
            }
        }

        // Individual harmonics, weighted by how prominent the harmonic is in either signal.
        foreach (HarmonicComparison h in measurements.Harmonics)
        {
            double strongest = Math.Max(h.TargetLevelDb, h.CandidateLevelDb);
            if (Math.Abs(h.DifferenceDb) < thresholds.MinimumHarmonicDifferenceDb || strongest < InaudibleRelativeDb)
            {
                continue;
            }

            double salience = MathUtil01(1.0 + (strongest / 40.0));
            AddLevel(result, ToneFeatureIds.Harmonic(h.HarmonicNumber), h.DifferenceDb, "dB", 8.0, 0.8 * salience, confidence, true, h.FrequencyHz, h.FrequencyHz);
        }

        // Frequency bands: whole note first, then segments that deviate from the whole-note picture.
        bool absolute = options.LoudnessMode == LoudnessMode.Absolute;
        double audible = absolute ? options.Bands.FloorDb : InaudibleRelativeDb;
        foreach (FrequencyBandDifference band in measurements.FrequencyBands)
        {
            if (Math.Abs(band.DifferenceDb) >= thresholds.MinimumLevelDifferenceDb && Math.Max(band.TargetEnergyDb, band.CandidateEnergyDb) > audible)
            {
                AddLevel(result, ToneFeatureIds.DetailBand(band.Name), band.DifferenceDb, "dB", 6.0, 0.85, confidence, true, band.StartHz, band.EndHz);
            }
        }

        double totalMs = Math.Max(1.0, envelope.TargetDurationMs);
        foreach (TimeSegmentComparison segment in measurements.TimeSegments)
        {
            double share = MathUtil01((segment.TargetEndMs - segment.TargetStartMs) / totalMs);
            double persistence = 0.4 + (0.6 * Math.Sqrt(share));
            if (segment.Segment == TimeSegments.Attack)
            {
                persistence = Math.Max(persistence, 0.75);
            }

            for (int i = 0; i < segment.Bands.Count; i++)
            {
                FrequencyBandDifference band = segment.Bands[i];
                double whole = i < measurements.FrequencyBands.Count ? measurements.FrequencyBands[i].DifferenceDb : 0.0;
                bool duplicatesWholeNote = Math.Abs(whole) >= thresholds.MinimumLevelDifferenceDb && Math.Abs(band.DifferenceDb - whole) < 1.5;
                if (Math.Abs(band.DifferenceDb) < 2.0 * thresholds.MinimumLevelDifferenceDb
                    || duplicatesWholeNote
                    || Math.Max(band.TargetEnergyDb, band.CandidateEnergyDb) <= audible)
                {
                    continue;
                }

                AddLevel(result, ToneFeatureIds.DetailBand(band.Name), band.DifferenceDb, "dB", 6.0, 0.85 * persistence, confidence * 0.9, true, band.StartHz, band.EndHz, segment.Segment);
            }

            double centroidRatio = segment.TargetCentroidHz > 0 && segment.CandidateCentroidHz > 0
                ? Math.Abs(Math.Log(segment.CandidateCentroidHz / segment.TargetCentroidHz))
                : 0.0;
            if (centroidRatio >= Math.Log(1.0 + (2.0 * thresholds.MinimumFrequencyRatio)))
            {
                double importance = Importance(centroidRatio, 0.25, 0.7 * persistence, confidence * 0.9);
                result.Add(new ToneDifference(
                    ToneFeatureIds.SegmentCentroid(segment.Segment),
                    segment.CentroidDifferenceHz > 0 ? DifferenceDirection.TooHigh : DifferenceDirection.TooLow,
                    Math.Abs(segment.CentroidDifferenceHz),
                    "Hz",
                    importance,
                    confidence * 0.9,
                    segment.CentroidDifferenceHz,
                    TimeRegion: segment.Segment));
            }
        }

        // Perceptual bands and resonances.
        foreach (MelBandDifference range in measurements.Mel.ExcessBands)
        {
            AddLevel(result, ToneFeatureIds.MelBandEnergy, range.MeanDifferenceDb, "dB", 6.0, 0.6, confidence, true, range.StartHz, range.EndHz);
        }

        foreach (MelBandDifference range in measurements.Mel.DeficientBands)
        {
            AddLevel(result, ToneFeatureIds.MelBandEnergy, range.MeanDifferenceDb, "dB", 6.0, 0.6, confidence, true, range.StartHz, range.EndHz);
        }

        AddResonances(result, measurements, confidence);

        result.RemoveAll(d => d.Importance < thresholds.MinimumImportance);
        result.Sort(static (a, b) =>
        {
            int byImportance = b.Importance.CompareTo(a.Importance);
            return byImportance != 0 ? byImportance : string.CompareOrdinal(a.Feature, b.Feature);
        });
        if (result.Count > thresholds.MaximumDifferences)
        {
            result.RemoveRange(thresholds.MaximumDifferences, result.Count - thresholds.MaximumDifferences);
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<ParameterHint> CreateHints(IReadOnlyList<ToneDifference> differences)
    {
        ArgumentNullException.ThrowIfNull(differences);
        var hints = new List<ParameterHint>();
        var seen = new HashSet<(string, int, string?)>();
        foreach (ToneDifference difference in differences)
        {
            if (hints.Count >= MaximumHints)
            {
                break;
            }

            if (difference.Direction == DifferenceDirection.Different)
            {
                continue;
            }

            IReadOnlyList<GenericParameterRelation> relations = GenericSynthPriors.ForFeature(difference.Feature, difference.FrequencyStartHz, difference.FrequencyEndHz);
            string generic = GenericSynthPriors.Generalize(difference.Feature, difference.FrequencyStartHz, difference.FrequencyEndHz);

            // Categories whose parameter must move the same way are grouped into one hint.
            foreach (int sign in (ReadOnlySpan<int>)[+1, -1, 0])
            {
                var categories = new List<string>();
                double strength = 0;
                foreach (GenericParameterRelation relation in relations)
                {
                    if (relation.Sign == sign)
                    {
                        categories.Add(relation.Category);
                        strength = Math.Max(strength, relation.Strength);
                    }
                }

                if (categories.Count == 0 || !seen.Add((generic, sign, difference.TimeRegion)))
                {
                    continue;
                }

                if (difference.TimeRegion is TimeSegments.Attack or TimeSegments.Decay && IsBrightness(generic))
                {
                    categories.Add(SynthParameterCategories.FilterEnvelopeAmount);
                    categories.Add(SynthParameterCategories.FilterEnvelopeDecay);
                }

                hints.Add(new ParameterHint(
                    difference.Feature,
                    categories,
                    Direction(generic, sign, difference.SignedDelta),
                    Math.Min(0.5, 0.6 * strength * difference.Importance),
                    difference.TimeRegion));
            }
        }

        return hints;
    }

    private static ParameterAdjustmentDirection Direction(string genericFeature, int relationSign, double delta)
    {
        if (relationSign == 0 || delta == 0)
        {
            return ParameterAdjustmentDirection.Investigate;
        }

        // The parameter has to move against the measured error.
        bool raise = (delta > 0) != (relationSign > 0);
        if (IsBrightness(genericFeature))
        {
            return raise ? ParameterAdjustmentDirection.IncreaseBrightness : ParameterAdjustmentDirection.DecreaseBrightness;
        }

        if (genericFeature is ToneFeatureIds.AttackTime or ToneFeatureIds.DecayTime or ToneFeatureIds.ReleaseTime)
        {
            return raise ? ParameterAdjustmentDirection.Lengthen : ParameterAdjustmentDirection.Shorten;
        }

        return raise ? ParameterAdjustmentDirection.Increase : ParameterAdjustmentDirection.Decrease;
    }

    private static bool IsBrightness(string genericFeature) =>
        genericFeature is ToneFeatureIds.SpectralCentroid or ToneFeatureIds.SpectralRolloff or ToneFeatureIds.HighBandEnergy
            or ToneFeatureIds.HighHarmonics or ToneFeatureIds.HarmonicSlope or ToneFeatureIds.SpectralSlope
        || genericFeature.EndsWith(".centroid", StringComparison.Ordinal);

    private static void AddResonances(List<ToneDifference> result, ToneMeasurements measurements, double confidence)
    {
        IReadOnlyList<double> diff = measurements.MelBandDifferencesDb;
        IReadOnlyList<double> edges = measurements.MelEdgesHz;
        const int Reach = 4;
        for (int b = 1; b < diff.Count - 1; b++)
        {
            // Prominence: how far this band sticks out from its neighbourhood on both sides. Requiring both sides
            // keeps the edge of a broad difference (a shelf) from being mistaken for a narrow peak.
            double left = 0, right = 0;
            int leftCount = 0, rightCount = 0;
            for (int j = Math.Max(0, b - Reach); j <= Math.Min(diff.Count - 1, b + Reach); j++)
            {
                if (j <= b - 2)
                {
                    left += diff[j];
                    leftCount++;
                }
                else if (j >= b + 2)
                {
                    right += diff[j];
                    rightCount++;
                }
            }

            if (leftCount == 0 || rightCount == 0)
            {
                continue;
            }

            double prominenceLeft = diff[b] - (left / leftCount), prominenceRight = diff[b] - (right / rightCount);
            double prominence = Math.Abs(prominenceLeft) < Math.Abs(prominenceRight) ? prominenceLeft : prominenceRight;
            bool sameSide = Math.Sign(prominenceLeft) == Math.Sign(prominenceRight);
            bool isLocalExtreme = Math.Abs(diff[b]) >= Math.Abs(diff[b - 1]) && Math.Abs(diff[b]) >= Math.Abs(diff[b + 1]);
            if (sameSide && Math.Abs(prominence) >= 4.0 && Math.Abs(diff[b]) >= 4.0 && isLocalExtreme && Math.Sign(prominence) == Math.Sign(diff[b]))
            {
                AddLevel(result, ToneFeatureIds.Resonance, diff[b], "dB", 6.0, 0.75, confidence * 0.8, true, edges[b], edges[b + 2]);
            }
        }
    }

    private static void AddLevel(
        List<ToneDifference> result,
        string feature,
        double delta,
        string unit,
        double scale,
        double weight,
        double confidence,
        bool energy,
        double? startHz = null,
        double? endHz = null,
        string? region = null)
    {
        DifferenceDirection direction = energy
            ? (delta > 0 ? DifferenceDirection.TooStrong : DifferenceDirection.TooWeak)
            : (delta > 0 ? DifferenceDirection.TooHigh : DifferenceDirection.TooLow);
        result.Add(new ToneDifference(feature, direction, Math.Abs(delta), unit, Importance(Math.Abs(delta), scale, weight, confidence), confidence, delta, startHz, endHz, region));
    }

    private static void AddThresholded(List<ToneDifference> result, DescriptorComparison d, double threshold, double scale, double weight, double confidence, bool energy = false)
    {
        if (Math.Abs(d.Difference) >= threshold)
        {
            AddLevel(result, d.FeatureId, d.Difference, d.Unit, scale, weight, confidence, energy);
        }
    }

    private static void AddFrequency(List<ToneDifference> result, DescriptorComparison d, double weight, double confidence, DifferenceOptions thresholds)
    {
        if (d.Target <= 0 || d.Candidate <= 0)
        {
            return;
        }

        double logRatio = Math.Abs(Math.Log(d.Candidate / d.Target));
        if (logRatio < Math.Log(1.0 + thresholds.MinimumFrequencyRatio))
        {
            return;
        }

        result.Add(new ToneDifference(
            d.FeatureId,
            d.Difference > 0 ? DifferenceDirection.TooHigh : DifferenceDirection.TooLow,
            Math.Abs(d.Difference),
            d.Unit,
            Importance(logRatio, 0.25, weight, confidence),
            confidence,
            d.Difference));
    }

    private static void AddTime(List<ToneDifference> result, string feature, EnvelopeStageComparison stage, double offsetMs, double weight, DifferenceOptions thresholds)
    {
        if (stage.Difference is not double delta || stage.Target is not double target || stage.Candidate is not double candidate)
        {
            return;
        }

        double larger = Math.Max(target, candidate) + offsetMs, smaller = Math.Min(target, candidate) + offsetMs;
        if (Math.Abs(delta) < thresholds.MinimumTimeDifferenceMs || larger / smaller < thresholds.MinimumTimeRatio)
        {
            return;
        }

        result.Add(new ToneDifference(
            feature,
            delta > 0 ? DifferenceDirection.TooLong : DifferenceDirection.TooShort,
            Math.Abs(delta),
            "ms",
            Importance(Math.Log(larger / smaller), 0.5, weight, stage.Confidence),
            stage.Confidence,
            delta));
    }

    private static double Importance(double size, double scale, double weight, double confidence) =>
        MathUtil01((1.0 - Math.Exp(-size / scale)) * weight * Math.Sqrt(MathUtil01(confidence)));

    private static double MathUtil01(double value) => Dsp.MathUtil.Clamp01(value);
}
