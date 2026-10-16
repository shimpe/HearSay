using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace HearSay.Synthesis.Patches;

/// <summary>Low-frequency sine modulation of the pitch.</summary>
/// <param name="RateHz">Vibrato rate.</param>
/// <param name="DepthCents">Peak deviation in cents.</param>
/// <param name="DelaySeconds">Time after note start during which the vibrato is absent.</param>
/// <param name="FadeInSeconds">Time over which the depth rises linearly after the delay.</param>
public sealed record LfoSettings(double RateHz, double DepthCents, double DelaySeconds = 0.0, double FadeInSeconds = 0.0);

/// <summary>
/// Backend-independent pitch behaviour of a patch. It applies to ratio operators only; fixed-frequency operators
/// keep their frequency.
/// </summary>
/// <param name="PitchEnvelope">
/// Pitch offset over time with levels in cents. It covers an initial pitch offset, a pitch decay and slow drift.
/// </param>
/// <param name="Vibrato">Optional vibrato.</param>
public sealed record PitchModel(OperatorEnvelope? PitchEnvelope = null, LfoSettings? Vibrato = null)
{
    /// <summary>Constant pitch.</summary>
    public static PitchModel None { get; } = new();

    /// <summary>True when the pitch never moves.</summary>
    [JsonIgnore]
    public bool IsStatic => PitchEnvelope is null && (Vibrato is null || Vibrato.DepthCents == 0.0);
}

/// <summary>
/// The noise source that all operators of a patch share in backends that have one: white noise through a one-pole
/// crossover, low-passed towards <paramref name="Color"/> -1, high-passed towards +1.
/// </summary>
/// <param name="Color">-1 (low-passed) through 0 (white) to 1 (high-passed).</param>
/// <param name="CutoffHz">Crossover frequency of the one-pole filter, 5..20000 Hz.</param>
public sealed record SharedNoise(double Color = 0.0, double CutoffHz = 8000.0)
{
    /// <summary>White noise; the cutoff is the UGen default.</summary>
    public static SharedNoise White { get; } = new();
}

/// <summary>
/// Canonical description of an operator-matrix synthesizer patch and the single source of truth for the search, the
/// reference renderer, the JSON format and every backend exporter. Which operator counts, waveforms, modes and
/// routings are allowed is decided by a <see cref="Profiles.SynthesisProfile"/>, never by this model.
/// </summary>
/// <param name="Operators">Operators; index <c>i</c> is shown as <c>OP(i+1)</c>.</param>
/// <param name="ModulationMatrix">Directed phase-modulation routing between the operators.</param>
/// <param name="OutputLevels">Linear gain with which each operator feeds the output mix; one entry per operator.</param>
/// <param name="Pitch">Pitch envelope and vibrato.</param>
public sealed record OperatorMatrixPatch(
    IReadOnlyList<OperatorPatch> Operators,
    PhaseModulationMatrix ModulationMatrix,
    IReadOnlyList<double> OutputLevels,
    PitchModel Pitch)
{
    /// <summary>Number of operators.</summary>
    [JsonIgnore]
    public int OperatorCount => Operators.Count;

    /// <summary>
    /// Colour of the noise source that all operators share, for backends that have one
    /// (<see cref="Profiles.SynthesisCapabilities.SupportsSharedNoise"/>); other backends ignore it.
    /// </summary>
    public SharedNoise Noise { get; init; } = SharedNoise.White;

    /// <summary>Creates a patch of disabled sine operators without routing.</summary>
    /// <param name="operatorCount">Number of operators.</param>
    public static OperatorMatrixPatch Silent(int operatorCount)
    {
        var operators = new OperatorPatch[operatorCount];
        Array.Fill(operators, OperatorPatch.Disabled);
        return new OperatorMatrixPatch(operators, PhaseModulationMatrix.Empty(operatorCount), new double[operatorCount], PitchModel.None);
    }

    /// <summary>Returns a copy with one operator replaced.</summary>
    /// <param name="index">Zero-based operator index.</param>
    /// <param name="value">New operator.</param>
    public OperatorMatrixPatch WithOperator(int index, OperatorPatch value)
    {
        var operators = Operators.ToArray();
        operators[index] = value;
        return this with { Operators = operators };
    }

    /// <summary>Returns a copy with one output level replaced.</summary>
    /// <param name="index">Zero-based operator index.</param>
    /// <param name="level">Linear output gain.</param>
    public OperatorMatrixPatch WithOutputLevel(int index, double level)
    {
        var levels = OutputLevels.ToArray();
        levels[index] = level;
        return this with { OutputLevels = levels };
    }

    /// <summary>Returns a copy with an edge added or replaced.</summary>
    /// <param name="edge">Edge to add or replace.</param>
    public OperatorMatrixPatch WithEdge(ModulationEdge edge) => this with { ModulationMatrix = ModulationMatrix.With(edge) };

    /// <summary>
    /// Operators that can be heard: enabled, and either mixed into the output or modulating, directly or through
    /// other operators, one that is. Everything else can be skipped by a renderer without changing the sound.
    /// </summary>
    public bool[] FindAudibleOperators()
    {
        int n = OperatorCount;
        var audible = new bool[n];
        for (int i = 0; i < n; i++)
        {
            audible[i] = Operators[i].Enabled && i < OutputLevels.Count && OutputLevels[i] != 0.0;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (ModulationEdge edge in ModulationMatrix.Edges)
            {
                if (edge.Enabled
                    && edge.Amount != 0.0
                    && audible[edge.DestinationOperator]
                    && !audible[edge.SourceOperator]
                    && Operators[edge.SourceOperator].Enabled)
                {
                    audible[edge.SourceOperator] = true;
                    changed = true;
                }
            }
        }

        return audible;
    }

    /// <summary>
    /// Returns the problems that make the patch internally inconsistent; empty when it is well formed. Whether a
    /// backend can play the patch is a separate question answered by a compatibility validator.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Operators is null || Operators.Count == 0)
        {
            problems.Add("A patch needs at least one operator.");
            return problems;
        }

        if (OutputLevels is null || OutputLevels.Count != Operators.Count)
        {
            problems.Add("There must be exactly one output level per operator.");
        }
        else
        {
            for (int i = 0; i < OutputLevels.Count; i++)
            {
                if (!double.IsFinite(OutputLevels[i]))
                {
                    problems.Add($"Output level of OP{i + 1} is not finite.");
                }
            }
        }

        if (Noise is not { Color: >= -1.0 and <= 1.0, CutoffHz: >= 5.0 and <= 20000.0 })
        {
            problems.Add("The shared noise needs a colour between -1 and 1 and a cutoff between 5 and 20000 Hz.");
        }

        if (ModulationMatrix is null || ModulationMatrix.OperatorCount != Operators.Count)
        {
            problems.Add("The modulation matrix must have one row and one column per operator.");
        }
        else
        {
            foreach (ModulationEdge edge in ModulationMatrix.Edges)
            {
                if (!double.IsFinite(edge.Amount))
                {
                    problems.Add($"Amount of edge OP{edge.SourceOperator + 1} -> OP{edge.DestinationOperator + 1} is not finite.");
                }

                if (edge.AmountEnvelope is not null)
                {
                    foreach (string problem in edge.AmountEnvelope.Validate())
                    {
                        problems.Add($"Edge OP{edge.SourceOperator + 1} -> OP{edge.DestinationOperator + 1} envelope: {problem}");
                    }
                }
            }
        }

        for (int i = 0; i < Operators.Count; i++)
        {
            OperatorPatch op = Operators[i];
            if (op is null)
            {
                problems.Add($"OP{i + 1} is null.");
                continue;
            }

            foreach (string problem in op.AmplitudeEnvelope.Validate())
            {
                problems.Add($"OP{i + 1} amplitude envelope: {problem}");
            }

            if (op.Frequency.Mode == OperatorFrequencyMode.Ratio && !(op.Frequency.Ratio > 0))
            {
                problems.Add($"OP{i + 1} needs a positive frequency ratio.");
            }

            if (op.Frequency.Mode == OperatorFrequencyMode.Fixed && !(op.Frequency.FixedFrequencyHz >= 0))
            {
                problems.Add($"OP{i + 1} needs a fixed frequency of zero or more.");
            }

            if (op.Waveform switch
                {
                    OperatorWaveform.FilteredNoise => op.Shape is < 0.0 or > 1.0 || !double.IsFinite(op.Shape),
                    OperatorWaveform.Elliptic => op.Shape is < 0.0 or > OperatorPatch.MaxEllipticM || !double.IsFinite(op.Shape),
                    _ => op.Shape is < 0.01 or > 0.99 || !double.IsFinite(op.Shape),
                })
            {
                problems.Add(op.Waveform switch
                {
                    OperatorWaveform.FilteredNoise => $"OP{i + 1} filter shape must be between 0 and 1.",
                    OperatorWaveform.Elliptic => $"OP{i + 1} elliptic parameter m must be between 0 and {OperatorPatch.MaxEllipticM}.",
                    _ => $"OP{i + 1} shape must be between 0.01 and 0.99.",
                });
            }

            if (op.Morph is < 0.0 or > 1.0 || !double.IsFinite(op.Morph))
            {
                problems.Add($"OP{i + 1} morph must be between 0 and 1.");
            }

            if (op.NoiseM is < -1.0 or > 1.0 || !double.IsFinite(op.NoiseM) || !double.IsFinite(op.NoisePhase))
            {
                problems.Add($"OP{i + 1} needs a finite phase noise and an m noise between -1 and 1.");
            }

            if (op.HarmonicLimit is < 1 or > 128)
            {
                problems.Add($"OP{i + 1} harmonic limit must be between 1 and 128.");
            }

            if (op.Filter is not null)
            {
                foreach (string problem in op.Filter.Validate())
                {
                    problems.Add($"OP{i + 1} filter {problem}");
                }
            }

            if (op.VelocitySensitivity is < 0 or > 1 || !double.IsFinite(op.VelocitySensitivity))
            {
                problems.Add($"OP{i + 1} velocity sensitivity must be between 0 and 1.");
            }

            if (!double.IsFinite(op.FineTuneCents) || !double.IsFinite(op.DetuneHz) || !double.IsFinite(op.InitialPhase))
            {
                problems.Add($"OP{i + 1} has a tuning or phase value that is not finite.");
            }
        }

        if (Pitch?.PitchEnvelope is not null)
        {
            foreach (string problem in Pitch.PitchEnvelope.Validate())
            {
                problems.Add($"Pitch envelope: {problem}");
            }
        }

        return problems;
    }

    /// <summary>Serializes the patch to the canonical JSON format.</summary>
    /// <param name="indented">Write indented JSON.</param>
    public string ToJson(bool indented = true) => HearSayJson.Serialize(this, indented);

    /// <summary>Reads a patch from the canonical JSON format.</summary>
    /// <param name="json">JSON text.</param>
    public static OperatorMatrixPatch FromJson(string json) => HearSayJson.Deserialize<OperatorMatrixPatch>(json);

    /// <summary>
    /// Deterministic content hash: equal patches have equal hashes in every process and on every machine. Used as a
    /// cache key for renders and evaluations.
    /// </summary>
    public string ComputeHash()
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ToJson(indented: false)));
        return Convert.ToHexString(bytes, 0, 16);
    }
}
