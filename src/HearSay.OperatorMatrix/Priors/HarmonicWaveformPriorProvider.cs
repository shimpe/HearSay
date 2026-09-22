using HearSay.Analysis;
using HearSay.Partials;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Hypotheses for profiles with band-limited waveforms and noise, read from the partial cloud of the target: all
/// harmonics falling as 1/n suggest a saw operator, odd harmonics only a square (1/n) or a triangle (1/n²), and a
/// pulse has the notches of its duty cycle. Each waveform that resembles the target is proposed alone at the
/// fundamental with several harmonic limits, then with one light sine modulator, so that the search starts from the
/// right waveform instead of having to reach it by mutation from a sine stack.
/// </summary>
/// <remarks>
/// Noise operators get two fixed hypotheses whenever the profile has them, a noise modulator and a parallel noise
/// burst, because the partial cloud says nothing about noise; screening decides.
/// </remarks>
public sealed class HarmonicWaveformPriorProvider : ISynthesisPriorProvider
{
    private const int Harmonics = 24;
    private static readonly int[] HarmonicLimits = [16, 32, 64, 128];
    private static readonly double[] PulseDuties = [0.1, 0.2, 0.3];

    /// <inheritdoc />
    public string Name => "harmonic-waveforms";

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        var hypotheses = new List<PatchHypothesis>();
        if (!capabilities.SupportsRatioFrequency || !capabilities.SupportedModes.Contains(OperatorMode.PhaseModulation) || capabilities.OperatorCount < 1)
        {
            return hypotheses;
        }

        OperatorEnvelope output = TemporalPriors.OutputEnvelope(analysis);
        OperatorEnvelope brightness = TemporalPriors.BrightnessEnvelope(analysis);
        double[] target = HarmonicAmplitudes(analysis);
        bool harmonic = target.Sum() > 0.0;

        var ranked = new List<(OperatorWaveform Waveform, double Shape, double Match)>();
        foreach (OperatorWaveform waveform in capabilities.SupportedWaveforms.Order())
        {
            switch (waveform)
            {
                case OperatorWaveform.Saw or OperatorWaveform.Square or OperatorWaveform.Triangle when harmonic:
                    ranked.Add((waveform, 0.5, Match(target, Ideal(waveform, 0.5))));
                    break;
                case OperatorWaveform.Pulse when harmonic:
                    foreach (double duty in PulseDuties)
                    {
                        ranked.Add((waveform, duty, Match(target, Ideal(waveform, duty))));
                    }

                    break;
            }
        }

        // A waveform is only worth proposing when it explains the spectrum better than a plain sine would.
        double sineMatch = harmonic ? Match(target, Ideal(OperatorWaveform.Sine, 0.5)) : 1.0;
        ranked.Sort(static (a, b) => b.Match.CompareTo(a.Match));
        int proposed = 0;
        foreach ((OperatorWaveform waveform, double shape, double match) in ranked)
        {
            if (match < 0.35 || match <= sineMatch + 0.01 || proposed >= 3)
            {
                break;
            }

            proposed++;
            double score = 0.45 + (0.3 * match);
            string name = waveform == OperatorWaveform.Pulse ? FormattableString.Invariant($"pulse {shape:0.##}") : waveform.ToString().ToLowerInvariant();
            foreach (int limit in HarmonicLimits)
            {
                OperatorPatch op = Carrier(waveform, shape, limit, output);
                OperatorMatrixPatch single = OperatorMatrixPatch.Silent(capabilities.OperatorCount).WithOperator(0, op).WithOutputLevel(0, 0.8);
                hypotheses.Add(new PatchHypothesis(single, score - (0.005 * Array.IndexOf(HarmonicLimits, limit)), $"single {name} ({limit} harmonics)"));
            }

            if (capabilities.OperatorCount >= 2 && proposed == 1)
            {
                foreach (double ratio in new[] { 1.0, 2.0, 3.0 })
                {
                    OperatorMatrixPatch pair = OperatorMatrixPatch.Silent(capabilities.OperatorCount)
                        .WithOperator(0, Carrier(waveform, shape, 32, output))
                        .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), brightness))
                        .WithOutputLevel(0, 0.8)
                        .WithEdge(new ModulationEdge(1, 0, true, 0.3));
                    hypotheses.Add(new PatchHypothesis(pair, score - 0.02, FormattableString.Invariant($"{name} carrier with sine modulator {ratio:0.#}")));
                }

                if (capabilities.SupportsSelfFeedback)
                {
                    OperatorMatrixPatch feedback = OperatorMatrixPatch.Silent(capabilities.OperatorCount)
                        .WithOperator(0, Carrier(waveform, shape, 32, output))
                        .WithOutputLevel(0, 0.8)
                        .WithEdge(new ModulationEdge(0, 0, true, 0.2));
                    hypotheses.Add(new PatchHypothesis(feedback, score - 0.03, $"{name} carrier with feedback"));
                }
            }
        }

        if (capabilities.OperatorCount >= 2 && capabilities.SupportedWaveforms.Contains(OperatorWaveform.Sine))
        {
            foreach (OperatorWaveform noise in new[] { OperatorWaveform.Noise, OperatorWaveform.SampleHoldNoise })
            {
                if (!capabilities.SupportedWaveforms.Contains(noise))
                {
                    continue;
                }

                string name = noise == OperatorWaveform.Noise ? "white noise" : "sample-and-hold noise";
                OperatorPatch noiseOp = OperatorPatch.Sine(OperatorFrequency.AtRatio(noise == OperatorWaveform.Noise ? 1.0 : 7.0), brightness) with { Waveform = noise };
                OperatorMatrixPatch modulator = OperatorMatrixPatch.Silent(capabilities.OperatorCount)
                    .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), output))
                    .WithOperator(1, noiseOp)
                    .WithOutputLevel(0, 0.8)
                    .WithEdge(new ModulationEdge(1, 0, true, 1.0));
                hypotheses.Add(new PatchHypothesis(modulator, 0.5, $"sine with {name} modulator"));
                if (capabilities.SupportsIndependentOperatorOutputs)
                {
                    OperatorMatrixPatch burst = OperatorMatrixPatch.Silent(capabilities.OperatorCount)
                        .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), output))
                        .WithOperator(1, noiseOp)
                        .WithOutputLevel(0, 0.8)
                        .WithOutputLevel(1, 0.3);
                    hypotheses.Add(new PatchHypothesis(burst, 0.5, $"sine with parallel {name}"));
                }
            }
        }

        return hypotheses;
    }

    /// <summary>Amplitudes of the first harmonics of the fundamental, linear, the strongest one at 1; zero where no partial sits.</summary>
    /// <param name="analysis">Analysis of the target.</param>
    public static double[] HarmonicAmplitudes(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var amplitudes = new double[Harmonics];
        double max = analysis.Partials.Count > 0 ? analysis.Partials.Max(static p => p.LevelDb) : 0.0;
        foreach (SpectralPartial partial in analysis.Partials)
        {
            int n = (int)Math.Round(partial.RelativeFrequency);
            if (n >= 1 && n <= Harmonics && Math.Abs(partial.RelativeFrequency - n) <= 0.06)
            {
                amplitudes[n - 1] = Math.Max(amplitudes[n - 1], Math.Pow(10.0, (partial.LevelDb - max) / 20.0));
            }
        }

        return amplitudes;
    }

    /// <summary>Ideal harmonic amplitudes of a waveform, first harmonic at index 0.</summary>
    /// <param name="waveform">Band-limited waveform.</param>
    /// <param name="shape">Duty cycle of a pulse; ignored otherwise.</param>
    public static double[] Ideal(OperatorWaveform waveform, double shape)
    {
        var ideal = new double[Harmonics];
        for (int n = 1; n <= Harmonics; n++)
        {
            ideal[n - 1] = waveform switch
            {
                OperatorWaveform.Saw => 1.0 / n,
                OperatorWaveform.Square => (n & 1) != 0 ? 1.0 / n : 0.0,
                OperatorWaveform.Triangle => (n & 1) != 0 ? 1.0 / (n * (double)n) : 0.0,
                OperatorWaveform.Pulse => Math.Abs(Math.Sin(Math.PI * n * shape)) / n,
                _ => n == 1 ? 1.0 : 0.0,
            };
        }

        return ideal;
    }

    /// <summary>Cosine similarity of two harmonic amplitude patterns, on a compressed (square-root) scale so that weak high harmonics count.</summary>
    /// <param name="a">First pattern.</param>
    /// <param name="b">Second pattern.</param>
    public static double Match(double[] a, double[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            double x = Math.Sqrt(Math.Max(0.0, a[i]));
            double y = Math.Sqrt(Math.Max(0.0, b[i]));
            dot += x * y;
            na += x * x;
            nb += y * y;
        }

        return na > 0 && nb > 0 ? dot / Math.Sqrt(na * nb) : 0.0;
    }

    private static OperatorPatch Carrier(OperatorWaveform waveform, double shape, int harmonicLimit, OperatorEnvelope envelope) =>
        OperatorPatch.Sine(OperatorFrequency.AtRatio(1), envelope) with { Waveform = waveform, Shape = shape, HarmonicLimit = harmonicLimit };
}
