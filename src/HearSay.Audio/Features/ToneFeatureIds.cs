using System.Globalization;

namespace HearSay.Features;

/// <summary>
/// Stable identifiers of every measured feature. Optimization histories, feature vectors and external systems rely on
/// these strings, so existing values must never change; new features get new ids.
/// </summary>
public static class ToneFeatureIds
{
    /// <summary>Loudness (active RMS level), dB.</summary>
    public const string Loudness = "loudness";

    /// <summary>Pitch of the refined fundamental, cents.</summary>
    public const string Pitch = "pitch";

    /// <summary>Attack time, ms.</summary>
    public const string AttackTime = "envelope.attack";

    /// <summary>Decay time, ms.</summary>
    public const string DecayTime = "envelope.decay";

    /// <summary>Sustain level relative to the envelope peak, dB.</summary>
    public const string SustainLevel = "envelope.sustain";

    /// <summary>Release time, ms.</summary>
    public const string ReleaseTime = "envelope.release";

    /// <summary>Spectral centroid, Hz.</summary>
    public const string SpectralCentroid = "spectral.centroid";

    /// <summary>Spectral rolloff (85%), Hz.</summary>
    public const string SpectralRolloff = "spectral.rolloff";

    /// <summary>Spectral bandwidth, Hz.</summary>
    public const string SpectralBandwidth = "spectral.bandwidth";

    /// <summary>Spectral flatness, dB.</summary>
    public const string SpectralFlatness = "spectral.flatness";

    /// <summary>Spectral slope, dB per octave.</summary>
    public const string SpectralSlope = "spectral.slope";

    /// <summary>Spectral flux, unitless.</summary>
    public const string SpectralFlux = "spectral.flux";

    /// <summary>Zero crossing rate, crossings per second.</summary>
    public const string ZeroCrossingRate = "temporal.zcr";

    /// <summary>Crest factor, dB.</summary>
    public const string CrestFactor = "temporal.crest";

    /// <summary>Harmonic-to-noise ratio, dB.</summary>
    public const string HarmonicToNoiseRatio = "noise.hnr";

    /// <summary>Inharmonicity, cents.</summary>
    public const string Inharmonicity = "harmonic.inharmonicity";

    /// <summary>Slope of harmonic levels, dB per octave.</summary>
    public const string HarmonicSlope = "harmonic.slope";

    /// <summary>Even-to-odd harmonic power ratio, dB.</summary>
    public const string EvenOddRatio = "harmonic.evenodd";

    /// <summary>Relative power of harmonics 2..5, dB.</summary>
    public const string LowHarmonics = "harmonic.low";

    /// <summary>Relative power of harmonics 6 and up, dB.</summary>
    public const string HighHarmonics = "harmonic.high";

    /// <summary>Relative energy below the low/mid split, dB.</summary>
    public const string LowBandEnergy = "spectral.band.low";

    /// <summary>Relative energy between the low/mid and mid/high splits, dB.</summary>
    public const string MidBandEnergy = "spectral.band.mid";

    /// <summary>Relative energy above the mid/high split, dB.</summary>
    public const string HighBandEnergy = "spectral.band.high";

    /// <summary>Energy in a range of Mel bands, dB. Only used in <see cref="Models.ToneDifference"/> entries, which carry the range.</summary>
    public const string MelBandEnergy = "mel.band";

    /// <summary>Narrow-band excess or deficit that suggests a resonance, dB. Only used in <see cref="Models.ToneDifference"/> entries.</summary>
    public const string Resonance = "spectral.resonance";

    /// <summary>Overall shape of the envelope. Only used in <see cref="Models.ToneDifference"/> entries.</summary>
    public const string EnvelopeShape = "envelope.shape";

    private const string HarmonicPrefix = "harmonic.";
    private const string DetailBandPrefix = "spectral.detail.";

    /// <summary>Id of the relative level of one harmonic, dB. <c>Harmonic(1)</c> is the fundamental.</summary>
    /// <param name="number">Harmonic number, starting at 1.</param>
    public static string Harmonic(int number) => HarmonicPrefix + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Id of the relative energy in a configured frequency band, dB.</summary>
    /// <param name="bandName">Name of the band as configured in <see cref="Comparison.BandOptions"/>.</param>
    public static string DetailBand(string bandName)
    {
        ArgumentNullException.ThrowIfNull(bandName);
        return DetailBandPrefix + bandName.ToLowerInvariant();
    }

    /// <summary>Id of the spectral centroid within one time segment, Hz.</summary>
    /// <param name="segment">Segment name, see <see cref="Models.TimeSegments"/>.</param>
    public static string SegmentCentroid(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return "segment." + segment.ToLowerInvariant() + ".centroid";
    }

    /// <summary>Returns the harmonic number when the id names a single harmonic, otherwise null.</summary>
    /// <param name="featureId">Feature id.</param>
    public static int? TryGetHarmonicNumber(string featureId)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        return featureId.StartsWith(HarmonicPrefix, StringComparison.Ordinal)
            && int.TryParse(featureId.AsSpan(HarmonicPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number
            : null;
    }
}
