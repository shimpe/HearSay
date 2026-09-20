namespace HearSay.Models;

/// <summary>Describes one entry of a dense feature vector.</summary>
/// <param name="Id">Stable feature id, see <see cref="Features.ToneFeatureIds"/>.</param>
/// <param name="Unit">Unit of the raw value, for example "dB", "ms", "Hz" or "cents".</param>
/// <param name="TypicalScale">
/// Size of a clearly audible difference in raw units. A normalized value is the raw value divided by this scale, so a
/// normalized magnitude of 1 means "clearly different" for every feature.
/// </param>
/// <param name="IsLogarithmic">True when the raw value is on a logarithmic scale (dB, cents).</param>
public sealed record FeatureDefinition(string Id, string Unit, double TypicalScale, bool IsLogarithmic);

/// <summary>Ordered description of a dense feature vector. The order is deterministic for a given configuration.</summary>
/// <param name="Features">Definitions in vector order.</param>
public sealed record FeatureVectorSchema(IReadOnlyList<FeatureDefinition> Features)
{
    /// <summary>Returns the position of a feature in the vector, or -1 when it is not part of the schema.</summary>
    /// <param name="featureId">Feature id.</param>
    public int IndexOf(string featureId)
    {
        for (int i = 0; i < Features.Count; i++)
        {
            if (Features[i].Id == featureId)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Dense vector of raw feature values in schema order. In comparison results the values are differences using the
/// library-wide convention <c>delta = candidate - target</c>.
/// </summary>
/// <param name="FeatureIds">Feature ids in vector order.</param>
/// <param name="Values">Raw values in the units given by the schema.</param>
public sealed record FeatureVector(IReadOnlyList<string> FeatureIds, ReadOnlyMemory<double> Values)
{
    /// <summary>Returns the value of a feature, or null when the vector does not contain it.</summary>
    /// <param name="featureId">Feature id.</param>
    public double? Get(string featureId)
    {
        for (int i = 0; i < FeatureIds.Count; i++)
        {
            if (FeatureIds[i] == featureId)
            {
                return Values.Span[i];
            }
        }

        return null;
    }
}

/// <summary>
/// Dense vector of normalized feature differences: <c>raw / typicalScale</c>, optionally clipped. Unitless and
/// comparable across features, which makes it suitable as the error vector of an optimizer.
/// </summary>
/// <param name="FeatureIds">Feature ids in vector order.</param>
/// <param name="Values">Normalized values.</param>
public sealed record NormalizedFeatureVector(IReadOnlyList<string> FeatureIds, ReadOnlyMemory<double> Values)
{
    /// <summary>Euclidean length of the vector.</summary>
    public double Norm()
    {
        double sum = 0;
        foreach (double v in Values.Span)
        {
            sum += v * v;
        }

        return Math.Sqrt(sum);
    }
}

/// <summary>Weight of one feature in a weighted optimization error vector.</summary>
/// <param name="FeatureId">Feature id. Weights for ids that are not in the schema are ignored.</param>
/// <param name="Weight">Multiplier; 0 removes the feature from the error, 1 leaves it unchanged.</param>
public sealed record OptimizationFeatureWeight(string FeatureId, double Weight);

/// <summary>
/// Compact, strongly typed difference vector for optimizers. Every value follows <c>delta = candidate - target</c>:
/// a positive <see cref="HighBandDelta"/> means the candidate has more high-frequency energy than the target.
/// An optimizer tries to drive every entry to zero. Entries that could not be measured are 0 and are listed in
/// <see cref="AnalysisQuality.UnreliableFeatures"/>.
/// </summary>
/// <param name="LoudnessDelta">Loudness difference in dB.</param>
/// <param name="PitchDelta">Pitch difference of the refined fundamentals in cents.</param>
/// <param name="AttackDelta">Attack-time difference in ms.</param>
/// <param name="DecayDelta">Decay-time difference in ms.</param>
/// <param name="SustainDelta">Sustain-level difference in dB.</param>
/// <param name="ReleaseDelta">Release-time difference in ms.</param>
/// <param name="SpectralCentroidDelta">Spectral-centroid difference in Hz.</param>
/// <param name="SpectralRolloffDelta">Spectral-rolloff difference in Hz.</param>
/// <param name="SpectralBandwidthDelta">Spectral-bandwidth difference in Hz.</param>
/// <param name="SpectralFlatnessDelta">Spectral-flatness difference in dB.</param>
/// <param name="SpectralSlopeDelta">Spectral-slope difference in dB per octave.</param>
/// <param name="HnrDelta">Harmonic-to-noise-ratio difference in dB (negative: the candidate is noisier).</param>
/// <param name="InharmonicityDelta">Inharmonicity difference in cents.</param>
/// <param name="HarmonicSlopeDelta">Harmonic-slope difference in dB per octave.</param>
/// <param name="EvenOddDelta">Even/odd balance difference in dB.</param>
/// <param name="LowBandDelta">Relative low-band energy difference in dB.</param>
/// <param name="MidBandDelta">Relative mid-band energy difference in dB.</param>
/// <param name="HighBandDelta">Relative high-band energy difference in dB.</param>
/// <param name="HarmonicDeltas">Relative level difference per harmonic in dB; index 0 is the fundamental.</param>
public sealed record ToneDifferenceVector(
    double LoudnessDelta,
    double PitchDelta,
    double AttackDelta,
    double DecayDelta,
    double SustainDelta,
    double ReleaseDelta,
    double SpectralCentroidDelta,
    double SpectralRolloffDelta,
    double SpectralBandwidthDelta,
    double SpectralFlatnessDelta,
    double SpectralSlopeDelta,
    double HnrDelta,
    double InharmonicityDelta,
    double HarmonicSlopeDelta,
    double EvenOddDelta,
    double LowBandDelta,
    double MidBandDelta,
    double HighBandDelta,
    IReadOnlyList<double> HarmonicDeltas);
