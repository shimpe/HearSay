using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Oscillators;

/// <summary>
/// Waveform functions of phase for the generic operator equation. The modulation graph never depends on the
/// waveform being a sine: any periodic function of the phase can be an operator.
/// </summary>
public static class WaveformKernel
{
    /// <summary>True when <see cref="Evaluate"/> implements the waveform.</summary>
    /// <param name="waveform">Waveform to test.</param>
    public static bool IsImplemented(OperatorWaveform waveform) =>
        waveform is OperatorWaveform.Sine or OperatorWaveform.Triangle;

    /// <summary>Value of a waveform, -1..1, at a phase in radians. Not band-limited; callers oversample.</summary>
    /// <param name="waveform">Waveform.</param>
    /// <param name="phase">Phase in radians, any range.</param>
    /// <exception cref="NotSupportedException">The waveform is not implemented.</exception>
    public static double Evaluate(OperatorWaveform waveform, double phase) =>
        waveform switch
        {
            OperatorWaveform.Sine => Math.Sin(phase),
            OperatorWaveform.Triangle => (2.0 / Math.PI) * Math.Asin(Math.Sin(phase)),
            _ => throw new NotSupportedException($"Waveform {waveform} is not implemented."),
        };
}
