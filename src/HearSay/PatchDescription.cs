using System.Globalization;
using HearSay.Synthesis.Patches;

namespace HearSay;

/// <summary>Human-readable descriptions of patches, shared by the command line and the desktop app.</summary>
public static class PatchDescription
{
    /// <summary>
    /// The waveform parameters of an operator that its waveform uses, empty for the others: the duty cycle of a pulse,
    /// the response of filtered noise, the elliptic parameter m (with its nome q), the sn-to-sd morph, and the roughness
    /// the shared noise gives the phase or the elliptic parameter.
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
        return string.Join("  ", new[] { waveform, phase, m }.Where(static s => s.Length > 0));
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
