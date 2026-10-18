using System.Globalization;
using HearSay.Synthesis.Patches;

namespace HearSay;

/// <summary>Human-readable descriptions of patches, shared by the command line and the desktop app.</summary>
public static class PatchDescription
{
    /// <summary>
    /// The waveform parameters of an operator that its waveform uses, empty for the others: the duty cycle of a pulse,
    /// the response of filtered noise, the elliptic parameter m (with its nome q), the sn-to-sd morph, and the roughness
    /// the shared noise gives the phase or the elliptic parameter, and the operator filter.
    /// </summary>
    /// <param name="op">Operator.</param>
    public static string WaveformDetail(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        string waveform = op.Waveform switch
        {
            OperatorWaveform.Pulse => string.Create(CultureInfo.InvariantCulture, $"duty {op.Shape:0.###}"),
            OperatorWaveform.FilteredNoise => string.Create(CultureInfo.InvariantCulture, $"response {op.Shape:0.###}"),
            OperatorWaveform.Elliptic => string.Create(CultureInfo.InvariantCulture, $"m {op.Shape:0.######} (q {Synthesis.Oscillators.JacobiNome.FromParameter(op.Shape):0.###})  morph {op.Morph:0.##}"),
            _ => string.Empty,
        };
        string phase = op.NoisePhase != 0.0 && op.Waveform != OperatorWaveform.Noise ? string.Create(CultureInfo.InvariantCulture, $"phase noise {op.NoisePhase:0.###} rad") : string.Empty;
        string m = op.NoiseM != 0.0 && op.Waveform == OperatorWaveform.Elliptic ? string.Create(CultureInfo.InvariantCulture, $"m noise {op.NoiseM:0.###}") : string.Empty;
        string filter = op.Filter is null ? string.Empty : FilterDetail(op.Filter);
        return string.Join("  ", new[] { waveform, phase, m, filter }.Where(static s => s.Length > 0));
    }

    /// <summary>A few characters for an operator filter, as in the block diagram: <c>LP4 x4</c>, <c>fmt 700/1.8</c> (F1 and F2 / F1).</summary>
    /// <param name="filter">Filter.</param>
    public static string FilterLabel(OperatorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Type switch
        {
            OperatorFilterType.LowPass => string.Create(CultureInfo.InvariantCulture, $"LP{filter.Poles} x{filter.CutoffRatio:0.##}"),
            OperatorFilterType.HighPass => string.Create(CultureInfo.InvariantCulture, $"HP{filter.Poles} x{filter.CutoffRatio:0.##}"),
            OperatorFilterType.BandPass => string.Create(CultureInfo.InvariantCulture, $"BP x{filter.CutoffRatio:0.##}"),
            OperatorFilterType.Notch => string.Create(CultureInfo.InvariantCulture, $"notch x{filter.CutoffRatio:0.##}"),
            OperatorFilterType.Peak => string.Create(CultureInfo.InvariantCulture, $"peak {filter.FixedHz:0} {(filter.PeakGainDb >= 0 ? "+" : string.Empty)}{filter.PeakGainDb:0.#}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"fmt {filter.FixedHz:0}/{filter.FormantRatio:0.##}"),
        };
    }

    /// <summary>An operator filter in words, with its sweep when it has one.</summary>
    /// <param name="filter">Filter.</param>
    public static string FilterDetail(OperatorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        string text = filter.Type switch
        {
            OperatorFilterType.LowPass => string.Create(CultureInfo.InvariantCulture, $"low-pass {filter.Poles} poles, cutoff {filter.CutoffRatio:0.##}x"),
            OperatorFilterType.HighPass => string.Create(CultureInfo.InvariantCulture, $"high-pass {filter.Poles} poles, cutoff {filter.CutoffRatio:0.##}x"),
            OperatorFilterType.BandPass => string.Create(CultureInfo.InvariantCulture, $"band-pass, centre {filter.CutoffRatio:0.##}x"),
            OperatorFilterType.Notch => string.Create(CultureInfo.InvariantCulture, $"notch, centre {filter.CutoffRatio:0.##}x"),
            OperatorFilterType.Peak => string.Create(CultureInfo.InvariantCulture, $"peak {filter.FixedHz:0} Hz {filter.PeakGainDb:+0.#;-0.#;0} dB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"formant F1 {filter.FixedHz:0} Hz, F2 {filter.FixedHz * filter.FormantRatio:0} Hz at {filter.FormantLevel:0.##}"),
        };
        string sweep = filter.Envelope is null || filter.EnvelopeOctaves == 0.0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", sweep {filter.EnvelopeOctaves:0.##} oct");
        return string.Create(CultureInfo.InvariantCulture, $"filter {text}, Q {filter.Q:0.##}{sweep}");
    }

    /// <summary>
    /// Everything about one operator, a line per aspect, for the tooltip of its box in the block diagram: carrier or
    /// modulator, frequency and tuning, waveform with its parameters, filter, shared noise, amplitude envelope, and how
    /// it plays (velocity, key scaling, start phase). Lines with nothing to say are left out.
    /// </summary>
    /// <param name="patch">Patch.</param>
    /// <param name="index">Zero-based operator index.</param>
    public static string OperatorDetail(OperatorMatrixPatch patch, int index)
    {
        ArgumentNullException.ThrowIfNull(patch);
        OperatorPatch op = patch.Operators[index];
        var lines = new List<string>
        {
            patch.OutputLevels[index] != 0.0
                ? string.Create(CultureInfo.InvariantCulture, $"OP{index + 1} · carrier, output {patch.OutputLevels[index]:0.###}")
                : $"OP{index + 1} · modulator",
        };

        var tuning = new List<string>
        {
            op.Frequency.Mode == OperatorFrequencyMode.Ratio
                ? string.Create(CultureInfo.InvariantCulture, $"ratio ×{op.Frequency.Ratio:0.###}")
                : string.Create(CultureInfo.InvariantCulture, $"fixed {op.Frequency.FixedFrequencyHz:0.#} Hz"),
        };
        if (op.FineTuneCents != 0.0)
        {
            tuning.Add(string.Create(CultureInfo.InvariantCulture, $"fine {op.FineTuneCents:0.#} ct"));
        }

        if (op.DetuneHz != 0.0)
        {
            tuning.Add(string.Create(CultureInfo.InvariantCulture, $"detune {op.DetuneHz:0.##} Hz"));
        }

        lines.Add(string.Join(", ", tuning));
        string shape = op.Waveform switch
        {
            OperatorWaveform.Pulse => string.Create(CultureInfo.InvariantCulture, $", duty {op.Shape:0.###}"),
            OperatorWaveform.FilteredNoise => string.Create(CultureInfo.InvariantCulture, $", response {op.Shape:0.###} (0 low-pass, 1 high-pass)"),
            OperatorWaveform.Elliptic => string.Create(CultureInfo.InvariantCulture, $", m {op.Shape:0.######} (q {Synthesis.Oscillators.JacobiNome.FromParameter(op.Shape):0.###}), morph {op.Morph:0.##}"),
            _ => string.Empty,
        };
        lines.Add(WaveformName(op.Waveform) + shape);
        if (op.Filter is { } filter)
        {
            lines.Add(FilterDetail(filter));
        }

        var noise = new List<string>();
        if (op.NoisePhase != 0.0 && op.Waveform != OperatorWaveform.Noise)
        {
            noise.Add(string.Create(CultureInfo.InvariantCulture, $"phase {op.NoisePhase:0.###} rad"));
        }

        if (op.NoiseM != 0.0 && op.Waveform == OperatorWaveform.Elliptic)
        {
            noise.Add(string.Create(CultureInfo.InvariantCulture, $"m {op.NoiseM:0.###}"));
        }

        if (noise.Count > 0)
        {
            lines.Add("shared noise: " + string.Join(", ", noise));
        }

        lines.Add("envelope " + EnvelopeText(op.AmplitudeEnvelope));
        var playing = new List<string>();
        if (op.VelocitySensitivity != 0.0)
        {
            playing.Add(string.Create(CultureInfo.InvariantCulture, $"velocity {op.VelocitySensitivity:0.##}"));
        }

        if (op.KeyScaling.LevelDbPerOctave != 0.0 || op.KeyScaling.RatePerOctave != 0.0)
        {
            playing.Add(string.Create(CultureInfo.InvariantCulture, $"key scaling {op.KeyScaling.LevelDbPerOctave:0.##} dB/oct, rate {op.KeyScaling.RatePerOctave:0.##}/oct"));
        }

        if (op.InitialPhase != 0.0)
        {
            playing.Add(string.Create(CultureInfo.InvariantCulture, $"start phase {op.InitialPhase:0.##} rad"));
        }

        if (playing.Count > 0)
        {
            lines.Add(string.Join(", ", playing));
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The pitch envelope and the vibrato of a patch, for the tooltip of the pitch box in the block diagram; empty when
    /// the pitch never moves.
    /// </summary>
    /// <param name="pitch">Pitch model.</param>
    public static string PitchDetail(PitchModel pitch)
    {
        ArgumentNullException.ThrowIfNull(pitch);
        if (pitch.IsStatic)
        {
            return string.Empty;
        }

        var lines = new List<string> { "Pitch of the ratio operators (fixed ones keep their frequency)" };
        if (pitch.PitchEnvelope is { } envelope)
        {
            lines.Add("envelope in cents: " + EnvelopeText(envelope));
        }

        if (pitch.Vibrato is { DepthCents: not 0.0 } vibrato)
        {
            string text = string.Create(CultureInfo.InvariantCulture, $"vibrato {vibrato.RateHz:0.##} Hz, ±{vibrato.DepthCents:0.#} ct");
            if (vibrato.DelaySeconds > 0.0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $", after {vibrato.DelaySeconds:0.##} s");
            }

            if (vibrato.FadeInSeconds > 0.0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $", fading in over {vibrato.FadeInSeconds:0.##} s");
            }

            lines.Add(text);
        }

        return string.Join("\n", lines);
    }

    /// <summary>A waveform in words: <c>sine</c>, <c>sample-and-hold noise</c>, …</summary>
    /// <param name="waveform">Waveform.</param>
    public static string WaveformName(OperatorWaveform waveform) => waveform switch
    {
        OperatorWaveform.SampleHoldNoise => "sample-and-hold noise",
        OperatorWaveform.FilteredNoise => "filtered noise",
        _ => waveform.ToString().ToLowerInvariant(),
    };

    /// <summary>The breakpoints of an envelope: <c>0 → 1 in 5 ms → 0.5 in 200 ms, held; release → 0 in 100 ms</c>.</summary>
    /// <param name="envelope">Envelope.</param>
    public static string EnvelopeText(OperatorEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var text = new System.Text.StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{envelope.Points[0].Level:0.##}"));
        for (int i = 1; i < envelope.Points.Count; i++)
        {
            EnvelopePoint point = envelope.Points[i];
            if (i == envelope.ReleasePoint)
            {
                text.Append("; release");
            }

            double seconds = point.TimeSeconds;
            string time = seconds < 1.0
                ? string.Create(CultureInfo.InvariantCulture, $"{seconds * 1000:0} ms")
                : string.Create(CultureInfo.InvariantCulture, $"{seconds:0.##} s");
            text.Append(CultureInfo.InvariantCulture, $" → {point.Level:0.##} in {time}");
            if (i == envelope.SustainPoint)
            {
                text.Append(", held");
            }
        }

        return text.ToString();
    }

    /// <summary>Audible operators and the modulation matrix as text.</summary>
    /// <param name="patch">Patch to describe.</param>
    public static string Routing(OperatorMatrixPatch patch)
    {
        bool[] audible = patch.FindAudibleOperators();
        ArgumentNullException.ThrowIfNull(patch);
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!audible[i])
            {
                continue;
            }

            OperatorPatch op = patch.Operators[i];
            string frequency = op.Frequency.Mode == OperatorFrequencyMode.Ratio
                ? string.Create(CultureInfo.InvariantCulture, $"ratio {op.Frequency.Ratio:0.###}")
                : string.Create(CultureInfo.InvariantCulture, $"fixed {op.Frequency.FixedFrequencyHz:0.#} Hz");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"OP{i + 1}  {op.Waveform,-8} {frequency,-16} fine {op.FineTuneCents,6:F1} ct  detune {op.DetuneHz,5:F2} Hz  output {patch.OutputLevels[i]:F3}") + (WaveformDetail(op) is { Length: > 0 } detail ? "  " + detail : string.Empty));
        }

        if (patch.Noise != SharedNoise.White)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"shared noise  colour {patch.Noise.Color:0.##} (-1 low-passed, 1 high-passed)  cutoff {patch.Noise.CutoffHz:0} Hz"));
        }

        text.AppendLine();
        text.AppendLine("modulation matrix (rows: destination, columns: source, radians)");
        text.Append("      ");
        for (int s = 0; s < patch.OperatorCount; s++)
        {
            text.Append(CultureInfo.InvariantCulture, $"   OP{s + 1} ");
        }

        text.AppendLine();
        for (int d = 0; d < patch.OperatorCount; d++)
        {
            text.Append(CultureInfo.InvariantCulture, $"  OP{d + 1} ");
            for (int s = 0; s < patch.OperatorCount; s++)
            {
                double amount = audible[d] && audible[s] ? patch.ModulationMatrix[d, s] : 0.0;
                text.Append(amount == 0.0 ? "     . " : string.Create(CultureInfo.InvariantCulture, $" {amount,6:F2}"));
            }

            text.AppendLine();
        }

        return text.ToString();
    }
}
