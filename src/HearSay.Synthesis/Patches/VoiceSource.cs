namespace HearSay.Synthesis.Patches;

/// <summary>
/// The voice source: what an operator with a formant filter plays. A formant filter only shapes the harmonics it is
/// given, so a vowel needs many of about the same strength: a narrow pulse (<see cref="Width"/>, 1 %) with at least
/// <see cref="Harmonics"/> harmonics and no noise on the elliptic parameter. The library, the search and the editor
/// share this rule; a noise operator with a formant filter stays noise (a whisper).
/// </summary>
public static class VoiceSource
{
    /// <summary>Duty cycle of a voice source: a pulse this narrow has many harmonics of nearly the same strength.</summary>
    public const double Width = 0.01;

    /// <summary>The fewest harmonics of a voice source: enough for the formants of a vowel up to F5 on a low note.</summary>
    public const int Harmonics = RandomPatchGenerator.FormantHarmonics;

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
