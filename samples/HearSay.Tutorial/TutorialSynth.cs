using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Optimization.Parameters;

namespace HearSay.Tutorial;

/// <summary>
/// Stand-in for your synthesizer. HearSay never looks inside it: all it ever sees is the rendered audio and the
/// parameter values you report. Replace <see cref="Render"/> with a call into your own synth or plug-in host.
/// </summary>
internal static class TutorialSynth
{
    public const int SampleRate = 44100;
    public const double NoteHz = 220.0;

    /// <summary>Parameter definitions in native units, exactly as you would describe a real synth.</summary>
    public static IReadOnlyList<SynthParameter> Parameters { get; } =
    [
        new("cutoff", "Filter cutoff", Value: 2000, MinValue: 200, MaxValue: 12000,
            RecommendedMinStep: 100, RecommendedMaxStep: 2500,
            Categories: [SynthParameterCategories.FilterCutoff]),
        new("attack", "Amp attack", Value: 20, MinValue: 1, MaxValue: 300,
            RecommendedMinStep: 3,
            Categories: [SynthParameterCategories.AmpEnvelopeAttack]),
        new("waveform", "Waveform (0 = saw, 1 = square)", Value: 0, MinValue: 0, MaxValue: 1,
            Kind: SynthParameterKind.Categorical),
    ];

    public static AudioNote Render(SynthParameterState state)
    {
        double cutoffHz = state.Values["cutoff"];
        double attackMs = state.Values["attack"];
        bool square = state.Values["waveform"] >= 0.5;

        const double HoldMs = 500, ReleaseMs = 150;
        int length = (int)((HoldMs + ReleaseMs) * SampleRate / 1000.0);
        var samples = new float[length];

        // Additive oscillator followed by a 12 dB/octave low-pass "filter" applied to the harmonic amplitudes.
        const int Harmonics = 40;
        var amplitudes = new double[Harmonics];
        double norm = 0;
        for (int k = 1; k <= Harmonics; k++)
        {
            double oscillator = square ? (k % 2 == 1 ? 1.0 / k : 0.0) : 1.0 / k;
            double ratio = k * NoteHz / cutoffHz;
            amplitudes[k - 1] = oscillator / Math.Sqrt(1.0 + Math.Pow(ratio, 4));
            norm += amplitudes[k - 1];
        }

        for (int i = 0; i < length; i++)
        {
            double t = (double)i / SampleRate, ms = t * 1000.0;
            double envelope = Math.Min(1.0, ms / attackMs);
            if (ms > HoldMs)
            {
                envelope *= Math.Max(0.0, 1.0 - ((ms - HoldMs) / ReleaseMs));
            }

            double value = 0;
            for (int k = 1; k <= Harmonics; k++)
            {
                value += amplitudes[k - 1] * Math.Sin(2.0 * Math.PI * NoteHz * k * t);
            }

            samples[i] = (float)(0.5 * envelope * value / norm);
        }

        return new AudioNote(samples, SampleRate, NoteHz);
    }

    public static SynthParameterState State(double cutoff, double attack, double waveform) =>
        new(new Dictionary<string, double> { ["cutoff"] = cutoff, ["attack"] = attack, ["waveform"] = waveform });
}
