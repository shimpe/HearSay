using HearSay.Synthesis.Profiles;

namespace HearSay.Synthesis.Patches;

/// <summary>What a <see cref="RandomPatchGenerator"/> may use.</summary>
public sealed record RandomPatchOptions
{
    /// <summary>Smallest number of enabled operators.</summary>
    public int MinOperators { get; init; } = 2;

    /// <summary>Largest number of enabled operators.</summary>
    public int MaxOperators { get; init; } = 6;

    /// <summary>Probability that a possible edge between two enabled operators exists.</summary>
    public double EdgeDensity { get; init; } = 0.2;

    /// <summary>Probability that an enabled operator modulates itself.</summary>
    public double SelfFeedbackProbability { get; init; } = 0.2;

    /// <summary>Allow edges that close a loop through several operators.</summary>
    public bool AllowCircularFeedback { get; init; } = true;

    /// <summary>Probability that an operator runs at a fixed frequency.</summary>
    public double FixedFrequencyProbability { get; init; } = 0.15;

    /// <summary>Probability that an operator is detuned.</summary>
    public double DetuneProbability { get; init; } = 0.3;

    /// <summary>Probability that an edge has an amount envelope.</summary>
    public double EdgeEnvelopeProbability { get; init; } = 0.3;

    /// <summary>Probability that an operator is roughened by the shared noise, for profiles that have one.</summary>
    public double RoughnessProbability { get; init; }

    /// <summary>Probability that the patch has a pitch envelope or a vibrato.</summary>
    public double PitchModulationProbability { get; init; } = 0.2;

    /// <summary>Largest amount of one edge in radians.</summary>
    public double MaxEdgeAmount { get; init; } = 4.0;
}

/// <summary>
/// Generates random patches that a profile allows, reproducibly from a seed. Used for backend-equivalence tests,
/// benchmarks and synthetic search targets.
/// </summary>
public sealed class RandomPatchGenerator
{
    private static readonly double[] Ratios = [0.5, 1, 1, 1, 2, 2, 3, 3.5, 4, 5, 7, 1.41, 2.73];

    private readonly SynthesisCapabilities _capabilities;
    private readonly RandomPatchOptions _options;

    /// <summary>Creates a generator.</summary>
    /// <param name="capabilities">Limits of the backend the patches are for.</param>
    /// <param name="options">Generation options.</param>
    public RandomPatchGenerator(SynthesisCapabilities capabilities, RandomPatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities;
        _options = options ?? new RandomPatchOptions();
    }

    /// <summary>Generates one patch. The same seed gives the same patch.</summary>
    /// <param name="seed">Random seed.</param>
    public OperatorMatrixPatch Generate(int seed)
    {
        var random = new Random(seed);
        int n = _capabilities.OperatorCount;
        OperatorWaveform[] waveforms = [.. _capabilities.SupportedWaveforms.Order()];
        OperatorMode[] modes = [.. _capabilities.SupportedModes.Order()];
        int enabledCount = random.Next(Math.Clamp(_options.MinOperators, 1, n), Math.Clamp(_options.MaxOperators, 1, n) + 1);

        var operators = new OperatorPatch[n];
        var levels = new double[n];
        for (int i = 0; i < n; i++)
        {
            bool enabled = i < enabledCount;
            bool fixedFrequency = _capabilities.SupportsFixedFrequency
                && (!_capabilities.SupportsRatioFrequency || random.NextDouble() < _options.FixedFrequencyProbability);
            OperatorFrequency frequency = fixedFrequency
                ? OperatorFrequency.AtFixed(Math.Round(20 * Math.Pow(200, random.NextDouble()), 1))
                : OperatorFrequency.AtRatio(Ratios[random.Next(Ratios.Length)]);
            bool detuned = random.NextDouble() < _options.DetuneProbability;
            operators[i] = new OperatorPatch(
                enabled,
                modes[random.Next(modes.Length)],
                waveforms[random.Next(waveforms.Length)],
                frequency,
                FineTuneCents: detuned ? Math.Round((random.NextDouble() * 20) - 10, 2) : 0,
                DetuneHz: detuned ? Math.Round((random.NextDouble() * 4) - 2, 2) : 0,
                InitialPhase: 0,
                RandomEnvelope(random),
                VelocitySensitivity: Math.Round(random.NextDouble() * 0.8, 2),
                KeyScaling.None);
            if (operators[i].Waveform == OperatorWaveform.Pulse)
            {
                operators[i] = operators[i] with { Shape = Math.Round(0.1 + (random.NextDouble() * 0.8), 2) };
            }
            else if (operators[i].Waveform == OperatorWaveform.FilteredNoise)
            {
                operators[i] = operators[i] with { Shape = Math.Round(random.NextDouble(), 2) };
            }
            else if (operators[i].Waveform == OperatorWaveform.Elliptic)
            {
                // m from a near-sine to the brightest, evenly in its nome; the morph anywhere from sn to sd.
                double m = Math.Round(Oscillators.JacobiNome.ToParameter(0.02 + (random.NextDouble() * 0.5)), 6);
                operators[i] = operators[i] with { Shape = Math.Min(m, OperatorPatch.MaxEllipticM), Morph = Math.Round(random.NextDouble(), 2) };
            }

            if (operators[i].Waveform is OperatorWaveform.Triangle or OperatorWaveform.Saw or OperatorWaveform.Square or OperatorWaveform.Pulse)
            {
                operators[i] = operators[i] with { HarmonicLimit = 8 << random.Next(5) };
            }
        }

        // The first operator is always heard; others feed the output with decreasing probability.
        levels[0] = 1.0;
        for (int i = 1; i < enabledCount; i++)
        {
            if (random.NextDouble() < 0.35)
            {
                levels[i] = Math.Round(0.2 + (random.NextDouble() * 0.8), 3);
            }
        }

        var edges = new List<ModulationEdge>();
        var incoming = new double[n];
        for (int destination = 0; destination < enabledCount; destination++)
        {
            for (int source = 0; source < enabledCount; source++)
            {
                bool self = source == destination;
                bool feedForward = source > destination;
                bool allowed = self
                    ? _capabilities.SupportsSelfFeedback
                    : feedForward || (_capabilities.SupportsFullModulationMatrix && _capabilities.SupportsCircularFeedback && _options.AllowCircularFeedback);
                double probability = self ? _options.SelfFeedbackProbability : feedForward ? _options.EdgeDensity * 1.5 : _options.EdgeDensity * 0.5;
                if (!allowed || random.NextDouble() >= probability)
                {
                    continue;
                }

                double amount = Math.Round((self ? 0.2 : 0.3) + (random.NextDouble() * (self ? 1.2 : _options.MaxEdgeAmount)), 3);
                if (incoming[destination] + amount > _capabilities.MaxModulationRadiansPerDestination)
                {
                    continue;
                }

                incoming[destination] += amount;
                OperatorEnvelope? envelope = _capabilities.SupportsDynamicModulationMatrix && random.NextDouble() < _options.EdgeEnvelopeProbability
                    ? OperatorEnvelope.TwoStageDecay(0.001, Round(0.02 + (random.NextDouble() * 0.2)), Round(0.2 + (random.NextDouble() * 0.5)), Round(0.2 + random.NextDouble()), Round(random.NextDouble() * 0.2), 0.1)
                    : null;
                edges.Add(new ModulationEdge(source, destination, true, amount, envelope));
            }
        }

        // Make sure the first operator has at least one modulator when there is more than one operator.
        if (enabledCount > 1 && !edges.Exists(static e => e.DestinationOperator == 0 && e.SourceOperator != 0))
        {
            edges.Add(new ModulationEdge(1, 0, true, Math.Round(0.5 + (random.NextDouble() * 3), 3)));
        }

        PitchModel pitch = PitchModel.None;
        if (random.NextDouble() < _options.PitchModulationProbability)
        {
            pitch = random.Next(2) == 0
                ? new PitchModel(new OperatorEnvelope([new EnvelopePoint(0, Round((random.NextDouble() * 100) - 50)), new EnvelopePoint(Round(0.02 + (random.NextDouble() * 0.1)), 0)], null, 2))
                : new PitchModel(null, new LfoSettings(Round(3 + (random.NextDouble() * 4)), Round(5 + (random.NextDouble() * 25)), Round(random.NextDouble() * 0.2), Round(random.NextDouble() * 0.3)));
        }

        SharedNoise noise = SharedNoise.White;
        if (_capabilities.SupportsSharedNoise && random.NextDouble() < _options.RoughnessProbability)
        {
            // A coloured shared noise that roughens one operator, in phase or, for an elliptic one, in m.
            noise = new SharedNoise(Math.Round((random.NextDouble() * 2) - 1, 2), Math.Round(200 * Math.Pow(50, random.NextDouble()), 1));
            int op = random.Next(enabledCount);
            operators[op] = operators[op].Waveform == OperatorWaveform.Elliptic && random.Next(2) == 0
                ? operators[op] with { NoiseM = Math.Round((random.NextDouble() * 0.6) - 0.3, 3) }
                : operators[op] with { NoisePhase = Math.Round(random.NextDouble() * 0.8, 3) };
        }

        return new OperatorMatrixPatch(operators, new PhaseModulationMatrix(n, edges), levels, pitch) { Noise = noise };
    }

    private static OperatorEnvelope RandomEnvelope(Random random) =>
        random.Next(3) switch
        {
            0 => OperatorEnvelope.Adsr(Round(0.001 + (random.NextDouble() * 0.05)), Round(0.05 + (random.NextDouble() * 0.4)), Round(0.2 + (random.NextDouble() * 0.8)), Round(0.05 + (random.NextDouble() * 0.4))),
            1 => OperatorEnvelope.TwoStageDecay(Round(0.001 + (random.NextDouble() * 0.01)), Round(0.03 + (random.NextDouble() * 0.2)), Round(0.2 + (random.NextDouble() * 0.6)), Round(0.3 + (random.NextDouble() * 1.5)), Round(random.NextDouble() * 0.2), Round(0.05 + (random.NextDouble() * 0.3))),
            _ => OperatorEnvelope.Constant(1.0, Round(0.002 + (random.NextDouble() * 0.02))),
        };

    private static double Round(double value) => Math.Round(value, 4);
}
