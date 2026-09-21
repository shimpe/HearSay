using HearSay.Comparison;
using HearSay.Models;

namespace HearSay.Features;

/// <summary>Builds the deterministic feature-vector schema for a configuration and converts between vector forms.</summary>
public static class FeatureSchemaBuilder
{
    /// <summary>
    /// Builds the schema. The order is fixed: scalar features, aggregate bands, configured bands, segment centroids
    /// and finally the per-harmonic entries. Only the configured band names and the harmonic count influence it.
    /// </summary>
    /// <param name="options">Comparison configuration.</param>
    public static FeatureVectorSchema Build(ToneComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var features = new List<FeatureDefinition>
        {
            new(ToneFeatureIds.Loudness, "dB", 6.0, true),
            new(ToneFeatureIds.Pitch, "cents", 10.0, true),
            new(ToneFeatureIds.AttackTime, "ms", 10.0, false),
            new(ToneFeatureIds.DecayTime, "ms", 50.0, false),
            new(ToneFeatureIds.SustainLevel, "dB", 6.0, true),
            new(ToneFeatureIds.ReleaseTime, "ms", 50.0, false),
            new(ToneFeatureIds.SpectralCentroid, "Hz", 500.0, false),
            new(ToneFeatureIds.SpectralRolloff, "Hz", 1000.0, false),
            new(ToneFeatureIds.SpectralBandwidth, "Hz", 500.0, false),
            new(ToneFeatureIds.SpectralFlatness, "dB", 6.0, true),
            new(ToneFeatureIds.SpectralSlope, "dB/oct", 3.0, true),
            new(ToneFeatureIds.SpectralFlux, "", 0.1, false),
            new(ToneFeatureIds.ZeroCrossingRate, "Hz", 2000.0, false),
            new(ToneFeatureIds.CrestFactor, "dB", 3.0, true),
            new(ToneFeatureIds.HarmonicToNoiseRatio, "dB", 10.0, true),
            new(ToneFeatureIds.Inharmonicity, "cents", 10.0, true),
            new(ToneFeatureIds.HarmonicSlope, "dB/oct", 3.0, true),
            new(ToneFeatureIds.EvenOddRatio, "dB", 6.0, true),
            new(ToneFeatureIds.LowHarmonics, "dB", 6.0, true),
            new(ToneFeatureIds.HighHarmonics, "dB", 6.0, true),
            new(ToneFeatureIds.LowBandEnergy, "dB", 3.0, true),
            new(ToneFeatureIds.MidBandEnergy, "dB", 3.0, true),
            new(ToneFeatureIds.HighBandEnergy, "dB", 3.0, true),
        };

        foreach (FrequencyBand band in options.Bands.Bands)
        {
            features.Add(new FeatureDefinition(ToneFeatureIds.DetailBand(band.Name), "dB", 3.0, true));
        }

        foreach (string segment in TimeSegments.All)
        {
            features.Add(new FeatureDefinition(ToneFeatureIds.SegmentCentroid(segment), "Hz", 500.0, false));
        }

        for (int k = 1; k <= options.FeatureVector.HarmonicCount; k++)
        {
            features.Add(new FeatureDefinition(ToneFeatureIds.Harmonic(k), "dB", 6.0, true));
        }

        IReadOnlyDictionary<string, double>? overrides = options.FeatureVector.TypicalScaleOverrides;
        if (overrides is not null)
        {
            for (int i = 0; i < features.Count; i++)
            {
                if (overrides.TryGetValue(features[i].Id, out double scale) && scale > 0)
                {
                    features[i] = features[i] with { TypicalScale = scale };
                }
            }
        }

        // Default weights: every feature group gets the same total (squared) weight.
        var groupSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (FeatureDefinition feature in features)
        {
            string group = GroupOf(feature.Id);
            groupSizes[group] = groupSizes.GetValueOrDefault(group) + 1;
        }

        for (int i = 0; i < features.Count; i++)
        {
            // The zero-crossing rate of a harmonic sound jumps when the waveform shape changes slightly. It stays in
            // the vector for diagnostics and learning, but does not steer guidance unless a weight is given explicitly.
            double weight = features[i].Id == ToneFeatureIds.ZeroCrossingRate
                ? 0.0
                : 1.0 / Math.Sqrt(groupSizes[GroupOf(features[i].Id)]);
            features[i] = features[i] with { DefaultWeight = weight };
        }

        return new FeatureVectorSchema(features);
    }

    /// <summary>
    /// Name of the group a feature belongs to: loudness, pitch, envelope, shape (scalar spectral and temporal
    /// descriptors), noise, harmonicshape, bands, segments or harmonics. Groups are used for the default weights.
    /// </summary>
    /// <param name="featureId">Feature id.</param>
    public static string GroupOf(string featureId)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        if (ToneFeatureIds.TryGetHarmonicNumber(featureId) is not null)
        {
            return "harmonics";
        }

        return featureId switch
        {
            ToneFeatureIds.Loudness => "loudness",
            ToneFeatureIds.Pitch => "pitch",
            ToneFeatureIds.HarmonicToNoiseRatio or ToneFeatureIds.Inharmonicity => "noise",
            _ when featureId.StartsWith("envelope.", StringComparison.Ordinal) => "envelope",
            _ when featureId.StartsWith("harmonic.", StringComparison.Ordinal) => "harmonicshape",
            _ when featureId.StartsWith("spectral.band.", StringComparison.Ordinal) => "bands",
            _ when featureId.StartsWith("spectral.detail.", StringComparison.Ordinal) => "bands",
            _ when featureId.StartsWith("segment.", StringComparison.Ordinal) => "segments",
            _ => "shape",
        };
    }

    /// <summary>Normalizes a raw vector: <c>normalized = raw / typicalScale</c>, optionally clipped.</summary>
    /// <param name="vector">Raw vector in schema order.</param>
    /// <param name="schema">Schema of the vector.</param>
    /// <param name="clip">Clip normalized values to plus or minus this value; null disables clipping.</param>
    public static NormalizedFeatureVector Normalize(FeatureVector vector, FeatureVectorSchema schema, double? clip = null)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(schema);
        if (vector.Values.Length != schema.Features.Count)
        {
            throw new ArgumentException("Vector and schema have different lengths.", nameof(vector));
        }

        ReadOnlySpan<double> raw = vector.Values.Span;
        var values = new double[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            double v = raw[i] / schema.Features[i].TypicalScale;
            values[i] = clip is double c ? Math.Clamp(v, -c, c) : v;
        }

        return new NormalizedFeatureVector(vector.FeatureIds, values);
    }

    /// <summary>
    /// Applies per-feature weights to a normalized vector: <c>e_weighted = W * e</c>. Features without an explicit
    /// weight use <paramref name="defaultWeight"/>.
    /// </summary>
    /// <param name="vector">Normalized error vector.</param>
    /// <param name="weights">Weights per feature id.</param>
    /// <param name="defaultWeight">Weight of features that are not listed.</param>
    public static NormalizedFeatureVector ApplyWeights(NormalizedFeatureVector vector, IEnumerable<OptimizationFeatureWeight> weights, double defaultWeight = 1.0)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(weights);
        var lookup = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (OptimizationFeatureWeight weight in weights)
        {
            lookup[weight.FeatureId] = weight.Weight;
        }

        ReadOnlySpan<double> source = vector.Values.Span;
        var values = new double[source.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = source[i] * (lookup.TryGetValue(vector.FeatureIds[i], out double w) ? w : defaultWeight);
        }

        return new NormalizedFeatureVector(vector.FeatureIds, values);
    }
}
