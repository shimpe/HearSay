using HearSay.Features;
using HearSay.Models;

namespace HearSay.Comparison;

/// <summary>Names of generic synth parameter categories used by hints and by prior-based guidance.</summary>
public static class SynthParameterCategories
{
    /// <summary>Low-pass filter cutoff.</summary>
    public const string FilterCutoff = "FilterCutoff";

    /// <summary>Filter resonance.</summary>
    public const string FilterResonance = "FilterResonance";

    /// <summary>Depth of the filter envelope.</summary>
    public const string FilterEnvelopeAmount = "FilterEnvelopeAmount";

    /// <summary>Attack time of the filter envelope.</summary>
    public const string FilterEnvelopeAttack = "FilterEnvelopeAttack";

    /// <summary>Decay time of the filter envelope.</summary>
    public const string FilterEnvelopeDecay = "FilterEnvelopeDecay";

    /// <summary>High-pass filter cutoff.</summary>
    public const string HighPassCutoff = "HighPassCutoff";

    /// <summary>Any oscillator control that adds upper harmonics (waveform morph, wavetable position, sync).</summary>
    public const string OscillatorBrightness = "OscillatorBrightness";

    /// <summary>Oscillator waveform or waveshaper selection.</summary>
    public const string Waveshape = "Waveshape";

    /// <summary>Balance between oscillators.</summary>
    public const string OscillatorMix = "OscillatorMix";

    /// <summary>Pulse width.</summary>
    public const string PulseWidth = "PulseWidth";

    /// <summary>Distortion, drive or saturation amount.</summary>
    public const string DistortionAmount = "DistortionAmount";

    /// <summary>Frequency- or phase-modulation amount.</summary>
    public const string FmAmount = "FmAmount";

    /// <summary>Noise oscillator level.</summary>
    public const string NoiseLevel = "NoiseLevel";

    /// <summary>Detune or unison spread.</summary>
    public const string Detune = "Detune";

    /// <summary>Coarse or fine tuning.</summary>
    public const string Tuning = "Tuning";

    /// <summary>Attack time of the amplitude envelope.</summary>
    public const string AmpEnvelopeAttack = "AmpEnvelopeAttack";

    /// <summary>Decay time of the amplitude envelope.</summary>
    public const string AmpEnvelopeDecay = "AmpEnvelopeDecay";

    /// <summary>Sustain level of the amplitude envelope.</summary>
    public const string AmpEnvelopeSustain = "AmpEnvelopeSustain";

    /// <summary>Release time of the amplitude envelope.</summary>
    public const string AmpEnvelopeRelease = "AmpEnvelopeRelease";

    /// <summary>Output or amplifier level.</summary>
    public const string OutputLevel = "OutputLevel";
}

/// <summary>A generic, synth-independent relation between a parameter category and a feature.</summary>
/// <param name="Category">Parameter category, see <see cref="SynthParameterCategories"/>.</param>
/// <param name="FeatureId">Feature the category usually influences.</param>
/// <param name="Sign">+1 when raising the parameter usually raises the feature, -1 for the opposite, 0 when the direction is unknown.</param>
/// <param name="Strength">How typical the relation is, 0..1.</param>
public sealed record GenericParameterRelation(string Category, string FeatureId, int Sign, double Strength);

/// <summary>
/// Generic prior knowledge about synthesizers. This is deliberately coarse: it only says which kinds of parameters
/// commonly relate to a feature. It is used for low-confidence hints and as a fallback when nothing has been observed
/// yet; empirically learned effects always take priority.
/// </summary>
public static class GenericSynthPriors
{
    /// <summary>All known generic relations.</summary>
    public static IReadOnlyList<GenericParameterRelation> Relations { get; } = Build();

    /// <summary>Relations that involve a feature. Per-harmonic and per-band ids are mapped onto their generic counterparts.</summary>
    /// <param name="featureId">Feature id.</param>
    /// <param name="frequencyStartHz">Lower edge of the affected frequency range, when the feature is a band.</param>
    /// <param name="frequencyEndHz">Upper edge of the affected frequency range, when the feature is a band.</param>
    public static IReadOnlyList<GenericParameterRelation> ForFeature(string featureId, double? frequencyStartHz = null, double? frequencyEndHz = null)
    {
        string generic = Generalize(featureId, frequencyStartHz, frequencyEndHz);
        var result = new List<GenericParameterRelation>();
        foreach (GenericParameterRelation relation in Relations)
        {
            if (relation.FeatureId == generic)
            {
                result.Add(relation);
            }
        }

        return result;
    }

    /// <summary>Maps per-harmonic, per-band and per-segment ids to the generic feature the priors are expressed in.</summary>
    /// <param name="featureId">Feature id.</param>
    /// <param name="frequencyStartHz">Lower edge of the affected frequency range, when known.</param>
    /// <param name="frequencyEndHz">Upper edge of the affected frequency range, when known.</param>
    public static string Generalize(string featureId, double? frequencyStartHz = null, double? frequencyEndHz = null)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        if (ToneFeatureIds.TryGetHarmonicNumber(featureId) is int number)
        {
            return number >= 6 ? ToneFeatureIds.HighHarmonics : number >= 2 ? ToneFeatureIds.LowHarmonics : featureId;
        }

        if (featureId.StartsWith("segment.", StringComparison.Ordinal) && featureId.EndsWith(".centroid", StringComparison.Ordinal))
        {
            return featureId == ToneFeatureIds.SegmentCentroid(TimeSegments.Attack) ? featureId : ToneFeatureIds.SpectralCentroid;
        }

        bool isBand = featureId == ToneFeatureIds.MelBandEnergy || featureId.StartsWith("spectral.detail.", StringComparison.Ordinal);
        if (isBand && frequencyStartHz is double start && frequencyEndHz is double end)
        {
            if (start >= 2000.0)
            {
                return ToneFeatureIds.HighBandEnergy;
            }

            return end <= 300.0 ? ToneFeatureIds.LowBandEnergy : ToneFeatureIds.MidBandEnergy;
        }

        return featureId;
    }

    private static List<GenericParameterRelation> Build()
    {
        var list = new List<GenericParameterRelation>();
        string[] brightness =
        [
            ToneFeatureIds.SpectralCentroid, ToneFeatureIds.SpectralRolloff, ToneFeatureIds.HighBandEnergy,
            ToneFeatureIds.HighHarmonics, ToneFeatureIds.HarmonicSlope, ToneFeatureIds.SpectralSlope,
        ];
        foreach (string feature in brightness)
        {
            list.Add(new(SynthParameterCategories.FilterCutoff, feature, +1, 0.9));
            list.Add(new(SynthParameterCategories.OscillatorBrightness, feature, +1, 0.7));
            list.Add(new(SynthParameterCategories.Waveshape, feature, 0, 0.5));
            list.Add(new(SynthParameterCategories.DistortionAmount, feature, +1, 0.5));
            list.Add(new(SynthParameterCategories.FmAmount, feature, +1, 0.4));
        }

        list.Add(new(SynthParameterCategories.FilterCutoff, ToneFeatureIds.SpectralBandwidth, +1, 0.6));
        list.Add(new(SynthParameterCategories.OscillatorBrightness, ToneFeatureIds.SpectralBandwidth, +1, 0.5));

        list.Add(new(SynthParameterCategories.Waveshape, ToneFeatureIds.LowHarmonics, 0, 0.6));
        list.Add(new(SynthParameterCategories.OscillatorMix, ToneFeatureIds.LowHarmonics, 0, 0.6));
        list.Add(new(SynthParameterCategories.PulseWidth, ToneFeatureIds.LowHarmonics, 0, 0.4));
        list.Add(new(SynthParameterCategories.Waveshape, ToneFeatureIds.EvenOddRatio, 0, 0.7));
        list.Add(new(SynthParameterCategories.PulseWidth, ToneFeatureIds.EvenOddRatio, 0, 0.6));
        list.Add(new(SynthParameterCategories.OscillatorMix, ToneFeatureIds.EvenOddRatio, 0, 0.5));

        list.Add(new(SynthParameterCategories.FilterCutoff, ToneFeatureIds.MidBandEnergy, 0, 0.4));
        list.Add(new(SynthParameterCategories.FilterResonance, ToneFeatureIds.MidBandEnergy, 0, 0.4));
        list.Add(new(SynthParameterCategories.Waveshape, ToneFeatureIds.MidBandEnergy, 0, 0.4));
        list.Add(new(SynthParameterCategories.HighPassCutoff, ToneFeatureIds.LowBandEnergy, -1, 0.6));
        list.Add(new(SynthParameterCategories.OscillatorMix, ToneFeatureIds.LowBandEnergy, 0, 0.5));
        list.Add(new(SynthParameterCategories.FilterResonance, ToneFeatureIds.Resonance, +1, 0.8));
        list.Add(new(SynthParameterCategories.FilterCutoff, ToneFeatureIds.Resonance, 0, 0.4));

        string attackCentroid = ToneFeatureIds.SegmentCentroid(TimeSegments.Attack);
        list.Add(new(SynthParameterCategories.FilterEnvelopeAmount, attackCentroid, +1, 0.7));
        list.Add(new(SynthParameterCategories.FilterEnvelopeDecay, attackCentroid, +1, 0.4));
        list.Add(new(SynthParameterCategories.FilterCutoff, attackCentroid, +1, 0.5));

        list.Add(new(SynthParameterCategories.AmpEnvelopeAttack, ToneFeatureIds.AttackTime, +1, 0.9));
        list.Add(new(SynthParameterCategories.FilterEnvelopeAttack, ToneFeatureIds.AttackTime, +1, 0.4));
        list.Add(new(SynthParameterCategories.AmpEnvelopeDecay, ToneFeatureIds.DecayTime, +1, 0.9));
        list.Add(new(SynthParameterCategories.FilterEnvelopeDecay, ToneFeatureIds.DecayTime, +1, 0.4));
        list.Add(new(SynthParameterCategories.AmpEnvelopeSustain, ToneFeatureIds.SustainLevel, +1, 0.9));
        list.Add(new(SynthParameterCategories.AmpEnvelopeRelease, ToneFeatureIds.ReleaseTime, +1, 0.9));
        list.Add(new(SynthParameterCategories.OutputLevel, ToneFeatureIds.Loudness, +1, 0.9));

        list.Add(new(SynthParameterCategories.NoiseLevel, ToneFeatureIds.HarmonicToNoiseRatio, -1, 0.8));
        list.Add(new(SynthParameterCategories.DistortionAmount, ToneFeatureIds.HarmonicToNoiseRatio, 0, 0.3));
        list.Add(new(SynthParameterCategories.NoiseLevel, ToneFeatureIds.SpectralFlatness, +1, 0.8));
        list.Add(new(SynthParameterCategories.Detune, ToneFeatureIds.Inharmonicity, +1, 0.6));
        list.Add(new(SynthParameterCategories.FmAmount, ToneFeatureIds.Inharmonicity, +1, 0.5));
        list.Add(new(SynthParameterCategories.Tuning, ToneFeatureIds.Pitch, +1, 0.9));
        list.Add(new(SynthParameterCategories.Detune, ToneFeatureIds.SpectralFlux, +1, 0.5));
        return list;
    }
}
