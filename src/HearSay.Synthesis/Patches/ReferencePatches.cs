namespace HearSay.Synthesis.Patches;

/// <summary>A named six-operator sine patch with the note it is meant to be played with.</summary>
/// <param name="Name">Short identifier without spaces.</param>
/// <param name="Patch">The patch.</param>
/// <param name="FrequencyHz">Note frequency.</param>
/// <param name="NoteSeconds">Gate time.</param>
/// <param name="ReleaseSeconds">Time rendered after the gate closes.</param>
public sealed record ReferencePatch(string Name, OperatorMatrixPatch Patch, double FrequencyHz, double NoteSeconds, double ReleaseSeconds);

/// <summary>
/// Hand-made patches that cover the territory of six-operator phase modulation. They serve as synthetic targets for
/// round-trip searches, as material for backend-equivalence tests and as benchmark input. All of them fit the
/// SuperCollider FM7 profile.
/// </summary>
public static class ReferencePatches
{
    /// <summary>All reference patches.</summary>
    public static IReadOnlyList<ReferencePatch> All { get; } =
    [
        new("pure-sine", Build(b => b.Carrier(1, 0.8, OperatorEnvelope.Adsr(0.01, 0.2, 0.7, 0.2))), 440, 1.0, 0.4),
        new("simple-2op", Build(b =>
        {
            int c = b.Carrier(1, 0.8, OperatorEnvelope.TwoStageDecay(0.003, 0.3, 0.4, 1.2, 0.1, 0.2));
            b.Modulator(2, c, 3.2, OperatorEnvelope.TwoStageDecay(0.001, 0.15, 0.3, 1.0, 0.05, 0.1));
        }), 220, 1.2, 0.4),
        new("bright-fm", Build(b =>
        {
            int c = b.Carrier(1, 0.8, OperatorEnvelope.Adsr(0.005, 0.4, 0.8, 0.25));
            int m = b.Modulator(1, c, 5.5, OperatorEnvelope.Adsr(0.002, 0.6, 0.7, 0.2));
            b.Modulator(3, m, 1.5, OperatorEnvelope.Adsr(0.002, 0.3, 0.5, 0.2));
        }), 165, 1.2, 0.4),
        new("bell", Build(b =>
        {
            int c1 = b.Carrier(1, 0.8, OperatorEnvelope.TwoStageDecay(0.001, 0.4, 0.35, 2.5, 0.02, 0.6));
            b.Modulator(3.5, c1, 2.6, OperatorEnvelope.TwoStageDecay(0.001, 0.5, 0.3, 2.0, 0.02, 0.5));
            int c2 = b.Carrier(2.76, 0.35, OperatorEnvelope.TwoStageDecay(0.001, 0.25, 0.2, 1.5, 0.01, 0.4));
            b.Modulator(5.4, c2, 1.4, OperatorEnvelope.TwoStageDecay(0.001, 0.2, 0.2, 1.0, 0.01, 0.3));
        }), 330, 2.0, 0.8),
        new("metallic-inharmonic", Build(b =>
        {
            int c = b.Carrier(1, 0.8, OperatorEnvelope.TwoStageDecay(0.001, 0.2, 0.4, 1.2, 0.05, 0.3));
            b.Modulator(1.41, c, 3.0, OperatorEnvelope.TwoStageDecay(0.001, 0.3, 0.5, 1.0, 0.1, 0.3));
            b.Modulator(2.73, c, 1.8, OperatorEnvelope.TwoStageDecay(0.001, 0.1, 0.3, 0.6, 0.02, 0.2));
        }), 247, 1.5, 0.6),
        new("electric-piano", Build(b =>
        {
            // The classic shape: a tine (high ratio, fast decay) over a body (ratio 1, slow decay).
            int body = b.Carrier(1, 0.8, OperatorEnvelope.TwoStageDecay(0.002, 0.25, 0.5, 2.0, 0.1, 0.25));
            b.Modulator(1, body, 1.6, OperatorEnvelope.TwoStageDecay(0.001, 0.4, 0.35, 1.5, 0.08, 0.2));
            int tine = b.Carrier(1, 0.35, OperatorEnvelope.TwoStageDecay(0.001, 0.12, 0.15, 0.8, 0.01, 0.2));
            b.Modulator(14, tine, 1.2, OperatorEnvelope.TwoStageDecay(0.001, 0.06, 0.1, 0.3, 0.005, 0.1));
        }), 262, 1.6, 0.5),
        new("bass", Build(b =>
        {
            int c = b.Carrier(1, 0.9, OperatorEnvelope.TwoStageDecay(0.002, 0.15, 0.6, 0.8, 0.3, 0.12));
            int m = b.Modulator(1, c, 2.8, OperatorEnvelope.TwoStageDecay(0.001, 0.08, 0.25, 0.5, 0.08, 0.1));
            b.Edge(m, m, 0.6);
        }), 65.4, 0.9, 0.3),
        new("brass-like", Build(b =>
        {
            // Brightness follows loudness: the modulator rises more slowly than the carrier.
            int c = b.Carrier(1, 0.8, OperatorEnvelope.Adsr(0.04, 0.2, 0.85, 0.15));
            b.Modulator(1, c, 3.6, OperatorEnvelope.Adsr(0.09, 0.3, 0.7, 0.12));
        }), 233, 1.2, 0.3),
        new("multi-output", Build(b =>
        {
            int c = b.Carrier(1, 0.8, OperatorEnvelope.Adsr(0.005, 0.3, 0.7, 0.25));
            int m = b.Modulator(2, c, 1.8, OperatorEnvelope.Adsr(0.005, 0.4, 0.6, 0.25));
            b.Output(m, 0.4);
            b.Carrier(3, 0.25, OperatorEnvelope.TwoStageDecay(0.002, 0.2, 0.3, 0.8, 0.05, 0.2));
        }), 196, 1.2, 0.4),
        new("detuned", Build(b =>
        {
            int c1 = b.Carrier(1, 0.6, OperatorEnvelope.Adsr(0.01, 0.3, 0.8, 0.3));
            b.Modulator(2, c1, 1.5, OperatorEnvelope.Adsr(0.005, 0.5, 0.6, 0.3));
            int c2 = b.Carrier(1, 0.6, OperatorEnvelope.Adsr(0.01, 0.3, 0.8, 0.3), detuneHz: 2.4);
            b.Modulator(2, c2, 1.5, OperatorEnvelope.Adsr(0.005, 0.5, 0.6, 0.3), detuneHz: 2.4);
        }), 220, 2.0, 0.4),
        new("feedback-heavy", Build(b =>
        {
            int c = b.Carrier(1, 0.8, OperatorEnvelope.Adsr(0.005, 0.3, 0.8, 0.2));
            b.Edge(c, c, 1.5);
            int m = b.Modulator(2, c, 0.8, OperatorEnvelope.Adsr(0.005, 0.2, 0.5, 0.2));
            b.Edge(m, m, 1.1);
        }), 147, 1.2, 0.3),
        new("circular-routing", Build(b =>
        {
            int a = b.Carrier(1, 0.8, OperatorEnvelope.Adsr(0.005, 0.3, 0.75, 0.25));
            int m1 = b.Modulator(2, a, 1.4, OperatorEnvelope.Adsr(0.005, 0.4, 0.6, 0.25));
            int m2 = b.Modulator(3, m1, 0.9, OperatorEnvelope.Adsr(0.005, 0.3, 0.5, 0.25));
            b.Edge(a, m2, 0.5);
        }), 196, 1.2, 0.4),
    ];

    /// <summary>Finds a reference patch by name; null when unknown.</summary>
    /// <param name="name">Name, case-insensitive.</param>
    public static ReferencePatch? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static OperatorMatrixPatch Build(Action<Builder> fill)
    {
        var builder = new Builder();
        fill(builder);
        return builder.ToPatch();
    }

    private sealed class Builder
    {
        private readonly List<OperatorPatch> _operators = [];
        private readonly List<double> _levels = [];
        private readonly List<ModulationEdge> _edges = [];

        public int Carrier(double ratio, double level, OperatorEnvelope envelope, double detuneHz = 0)
        {
            _operators.Add(OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), envelope) with { DetuneHz = detuneHz });
            _levels.Add(level);
            return _operators.Count - 1;
        }

        public int Modulator(double ratio, int destination, double amount, OperatorEnvelope envelope, double detuneHz = 0)
        {
            int index = Carrier(ratio, 0.0, envelope, detuneHz);
            Edge(index, destination, amount);
            return index;
        }

        public void Edge(int source, int destination, double amount) => _edges.Add(new ModulationEdge(source, destination, true, amount));

        public void Output(int op, double level) => _levels[op] = level;

        public OperatorMatrixPatch ToPatch()
        {
            const int n = 6;
            var operators = new OperatorPatch[n];
            var levels = new double[n];
            for (int i = 0; i < n; i++)
            {
                operators[i] = i < _operators.Count ? _operators[i] : OperatorPatch.Disabled;
                levels[i] = i < _levels.Count ? _levels[i] : 0.0;
            }

            return new OperatorMatrixPatch(operators, new PhaseModulationMatrix(n, _edges), levels, PitchModel.None);
        }
    }
}
