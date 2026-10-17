using System.Globalization;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>Brightness class of a sound, see <see cref="SoundDescriptors.BrightnessOf"/>.</summary>
public enum SoundBrightness
{
    /// <summary>Below twice the note.</summary>
    Dark,

    /// <summary>Two to four times the note.</summary>
    Mellow,

    /// <summary>Four to eight times the note.</summary>
    Bright,

    /// <summary>From eight times the note.</summary>
    VeryBright,
}

/// <summary>Envelope class of a sound, see <see cref="SoundDescriptors.EnvelopeOf"/>.</summary>
public enum SoundEnvelope
{
    /// <summary>The decay at least 18 dB below the attack.</summary>
    Percussive,

    /// <summary>6 to 18 dB below.</summary>
    Decaying,

    /// <summary>Within 6 dB.</summary>
    Sustained,

    /// <summary>More than 6 dB above.</summary>
    Swelling,
}

/// <summary>A row of the library filter.</summary>
public enum TraitFacet
{
    /// <summary>Waveform of an audible operator.</summary>
    Waveform,

    /// <summary>Filter type of an audible operator, or none.</summary>
    Filter,

    /// <summary>Brightness class.</summary>
    Brightness,

    /// <summary>Envelope class.</summary>
    Envelope,

    /// <summary>Noisy.</summary>
    Noise,

    /// <summary>Number of audible operators.</summary>
    Operators,

    /// <summary>Self-feedback on an audible operator, or none.</summary>
    Feedback,
}

/// <summary>One chip of the library filter: a facet and a value of it.</summary>
/// <param name="Facet">Row.</param>
/// <param name="Value">
/// The waveform or filter type as an integer (<see cref="NoFilter"/> for none), the brightness or envelope class, 1 for
/// noisy, the number of operators, or 1 / 0 for feedback / no feedback.
/// </param>
public readonly record struct TraitValue(TraitFacet Facet, int Value)
{
    /// <summary>The filter value for "no audible operator has a filter".</summary>
    public const int NoFilter = -1;

    /// <summary>Every value, ordered by facet, then value.</summary>
    public static IReadOnlyList<TraitValue> All { get; } =
    [
        .. Enum.GetValues<OperatorWaveform>().Select(Waveform),
        Filter(null),
        .. Enum.GetValues<OperatorFilterType>().Select(static t => Filter(t)),
        .. Enum.GetValues<SoundBrightness>().Select(Of),
        .. Enum.GetValues<SoundEnvelope>().Select(Of),
        Noisy,
        .. Enumerable.Range(1, 6).Select(Operators),
        Feedback(false),
        Feedback(true),
    ];

    /// <summary>Noisy.</summary>
    public static TraitValue Noisy => new(TraitFacet.Noise, 1);

    /// <summary>An audible operator has this waveform.</summary>
    /// <param name="waveform">Waveform.</param>
    public static TraitValue Waveform(OperatorWaveform waveform) => new(TraitFacet.Waveform, (int)waveform);

    /// <summary>An audible operator has a filter of this type; for null, none has a filter.</summary>
    /// <param name="type">Filter type, or null for none.</param>
    public static TraitValue Filter(OperatorFilterType? type) => new(TraitFacet.Filter, type is { } t ? (int)t : NoFilter);

    /// <summary>This brightness class.</summary>
    /// <param name="brightness">Class.</param>
    public static TraitValue Of(SoundBrightness brightness) => new(TraitFacet.Brightness, (int)brightness);

    /// <summary>This envelope class.</summary>
    /// <param name="envelope">Class.</param>
    public static TraitValue Of(SoundEnvelope envelope) => new(TraitFacet.Envelope, (int)envelope);

    /// <summary>This many audible operators.</summary>
    /// <param name="count">Number of operators.</param>
    public static TraitValue Operators(int count) => new(TraitFacet.Operators, count);

    /// <summary>Self-feedback on an audible operator, or none.</summary>
    /// <param name="present">True for feedback.</param>
    public static TraitValue Feedback(bool present) => new(TraitFacet.Feedback, present ? 1 : 0);

    /// <summary>Title of a row.</summary>
    /// <param name="facet">Facet.</param>
    public static string TitleOf(TraitFacet facet) => facet switch
    {
        TraitFacet.Waveform => "Waveform",
        TraitFacet.Filter => "Filter",
        TraitFacet.Brightness => "Brightness",
        TraitFacet.Envelope => "Envelope",
        TraitFacet.Noise => "Noise",
        TraitFacet.Operators => "Audible operators",
        _ => "Feedback",
    };

    /// <summary>Text of the chip.</summary>
    public string Label => Facet switch
    {
        TraitFacet.Waveform => (OperatorWaveform)Value switch
        {
            OperatorWaveform.SampleHoldNoise => "S&H noise",
            OperatorWaveform.FilteredNoise => "filtered noise",
            var w => w.ToString().ToLowerInvariant(),
        },
        TraitFacet.Filter => Value == NoFilter ? "none" : (OperatorFilterType)Value switch
        {
            OperatorFilterType.LowPass => "low-pass",
            OperatorFilterType.HighPass => "high-pass",
            OperatorFilterType.BandPass => "band-pass",
            var t => t.ToString().ToLowerInvariant(),
        },
        TraitFacet.Brightness => SoundDescriptors.Word((SoundBrightness)Value),
        TraitFacet.Envelope => SoundDescriptors.Word((SoundEnvelope)Value),
        TraitFacet.Noise => "noisy",
        TraitFacet.Operators => Value.ToString(CultureInfo.InvariantCulture) + " ops",
        _ => Value == 1 ? "feedback" : "no feedback",
    };
}

/// <summary>What the library filter knows about a sound.</summary>
/// <param name="Waveforms">Bit mask over <see cref="OperatorWaveform"/> of the audible operators.</param>
/// <param name="Filters">Bit mask over <see cref="OperatorFilterType"/> of their filters; <see cref="NoFilterBit"/> when none has one.</param>
/// <param name="Brightness">Brightness class.</param>
/// <param name="Envelope">Envelope class.</param>
/// <param name="Noisy">An audible operator is noise or roughened by the shared noise.</param>
/// <param name="Feedback">An audible operator has self-feedback.</param>
/// <param name="AudibleOperators">Number of audible operators.</param>
public readonly record struct SoundTraits(int Waveforms, int Filters, SoundBrightness Brightness, SoundEnvelope Envelope, bool Noisy, bool Feedback, int AudibleOperators)
{
    /// <summary>The bit of <see cref="Filters"/> for "no filter".</summary>
    public const int NoFilterBit = 1 << 15;

    /// <summary>The traits of a sound.</summary>
    /// <param name="print">Its fingerprint.</param>
    /// <param name="patch">Its patch.</param>
    public static SoundTraits Of(ReadOnlySpan<byte> print, OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        bool[] audible = patch.FindAudibleOperators();
        int waveforms = 0, filters = 0, heard = 0;
        bool feedback = false;
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!audible[i])
            {
                continue;
            }

            heard++;
            OperatorPatch op = patch.Operators[i];
            waveforms |= 1 << (int)op.Waveform;
            if (op.Filter is { } filter)
            {
                filters |= 1 << (int)filter.Type;
            }

            feedback |= patch.ModulationMatrix[i, i] != 0.0;
        }

        return new SoundTraits(
            waveforms,
            filters == 0 ? NoFilterBit : filters,
            SoundDescriptors.BrightnessOf(print),
            SoundDescriptors.EnvelopeOf(print),
            SoundDescriptors.IsNoisy(patch, audible),
            feedback,
            heard);
    }

    /// <summary>The traits of every sound of a library, by index; every patch is generated again from its seed.</summary>
    /// <param name="library">Library.</param>
    public static SoundTraits[] Of(StructureLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var traits = new SoundTraits[library.Count];
        Parallel.For(0, library.Count, i => traits[i] = Of(library.Fingerprint(i), library.Patch(library.Seed(i))));
        return traits;
    }

    /// <summary>True when the sound has the value.</summary>
    /// <param name="value">Chip value.</param>
    public bool Has(TraitValue value) => value.Facet switch
    {
        TraitFacet.Waveform => (Waveforms & (1 << value.Value)) != 0,
        TraitFacet.Filter => (Filters & (value.Value == TraitValue.NoFilter ? NoFilterBit : 1 << value.Value)) != 0,
        TraitFacet.Brightness => (int)Brightness == value.Value,
        TraitFacet.Envelope => (int)Envelope == value.Value,
        TraitFacet.Noise => Noisy == (value.Value == 1),
        TraitFacet.Operators => AudibleOperators == value.Value,
        _ => Feedback == (value.Value == 1),
    };
}
