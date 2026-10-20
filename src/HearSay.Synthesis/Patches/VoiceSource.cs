namespace HearSay.Synthesis.Patches;

/// <summary>
/// The sources of an operator with a formant filter, which only shapes the harmonics it is given. The voice source, a
/// narrow pulse (<see cref="Width"/>, 1 %) with at least <see cref="Harmonics"/> harmonics and no noise on the elliptic
/// parameter, is the flat source the editor gives (the vowel presets assume one). The rich source is what the library
/// and the search give: a saw or a pulse with many harmonics, which matches recorded vowels better, and the voice source
/// in place of any other tonal waveform. A noise operator with a formant filter stays noise (a whisper).
/// </summary>
public static class VoiceSource
{
    /// <summary>Duty cycle of a voice source: a pulse this narrow has many harmonics of nearly the same strength.</summary>
    public const double Width = 0.01;

    /// <summary>The fewest harmonics of a voice source: enough for the formants of a vowel up to F5 on a low note.</summary>
    public const int Harmonics = RandomPatchGenerator.FormantHarmonics;

    /// <summary>
    /// The operator as a rich source, what the library and the search give an operator with a formant filter: noise
    /// unchanged (a whisper); a saw, or a pulse at its own width, with <see cref="Harmonics"/> harmonics or the more
    /// it has; any other tonal waveform the voice source (<see cref="AsVoiceSource"/>): a sine leaves a formant filter
    /// nothing to shape, a triangle little, and a square or an elliptic only odd harmonics. Everything else stays.
    /// </summary>
    /// <param name="op">The operator.</param>
    public static OperatorPatch AsRichSource(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Waveform switch
        {
            _ when op.Waveform.IsNoise() => op,
            OperatorWaveform.Saw or OperatorWaveform.Pulse => op.HarmonicLimit >= Harmonics ? op : op with { HarmonicLimit = Harmonics },
            _ => AsVoiceSource(op, keepPulseWidth: false),
        };
    }

    /// <summary>
    /// The operator as a voice source: unchanged when it plays noise; otherwise a pulse, <see cref="Width"/> wide (or
    /// at its own width when <paramref name="keepPulseWidth"/> is set and it is a pulse already), with
    /// <see cref="Harmonics"/> harmonics or the more it has, and <c>NoiseM</c> 0. Its filter and everything else stay.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="keepPulseWidth">Keep the width of an operator that is a pulse already, as the search tunes it.</param>
    public static OperatorPatch AsVoiceSource(OperatorPatch op, bool keepPulseWidth)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (op.Waveform.IsNoise())
        {
            return op;
        }

        double width = keepPulseWidth && op.Waveform == OperatorWaveform.Pulse ? op.Shape : Width;
        return op.WithWaveform(OperatorWaveform.Pulse) with
        {
            Shape = width,
            HarmonicLimit = Math.Max(op.HarmonicLimit, Harmonics),
            NoiseM = 0.0,
        };
    }
}
