using HearSay.Synthesis.Envelopes;
using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Compilation;

/// <summary>
/// A patch bound to one voice, its control signals evaluated one control block at a time with the gate decided by
/// the caller. Only audible operators and live edges remain, as in <see cref="CompiledOperatorMatrixPatch"/>, whose
/// arrays <see cref="PatchCompiler.Compile"/> fills by running a stream. Before the first <see cref="Advance"/> the
/// current values are the values a unit sees when it is constructed; after the advance for block <c>b</c> they are
/// the values during block <c>b</c>, so a renderer ramps from the previous values to the current ones across a block.
/// </summary>
/// <remarks>Mutable; use one instance per voice, from one thread.</remarks>
public sealed class ControlStream
{
    private readonly Signal[] _amplitudes;
    private readonly Signal[] _edgeSignals;
    private readonly int[] _edgeSignalTargets;
    private readonly Signal[] _filterSignals;
    private readonly int[] _filterSignalTargets;
    private readonly int[] _pitchedOperators;
    private readonly float[] _tracking;
    private readonly float[] _detune;
    private readonly bool _hasPitchEnvelope;
    private readonly bool _hasVibrato;
    private readonly double _vibratoIncrement;
    private Signal _pitchEnvelope;
    private Signal _vibratoDepth;
    private int _blocks;

    /// <summary>Binds a patch to a voice whose gate opens with the first block.</summary>
    /// <param name="patch">Patch; must be well formed.</param>
    /// <param name="request">Note to play.</param>
    /// <param name="controlBlockSize">Samples per control block.</param>
    /// <exception cref="ArgumentException">The patch is not well formed.</exception>
    public ControlStream(OperatorMatrixPatch patch, VoiceRequest request, int controlBlockSize = 64)
        : this(patch, request, controlBlockSize, firstGateOpen: true)
    {
    }

    /// <summary>Binds a patch to a voice.</summary>
    /// <param name="patch">Patch; must be well formed.</param>
    /// <param name="request">Note to play.</param>
    /// <param name="controlBlockSize">Samples per control block.</param>
    /// <param name="firstGateOpen">
    /// Whether the gate is open during the first block; the construction values are the values of that block.
    /// </param>
    internal ControlStream(OperatorMatrixPatch patch, VoiceRequest request, int controlBlockSize, bool firstGateOpen)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(controlBlockSize, 1);
        IReadOnlyList<string> problems = patch.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException("The patch is not well formed: " + string.Join(" ", problems), nameof(patch));
        }

        SampleRate = request.SampleRate;
        BlockSize = controlBlockSize;
        double controlRate = (double)SampleRate / controlBlockSize;
        double midiNote = request.EffectiveMidiNote;

        bool[] audible = patch.FindAudibleOperators();
        var map = new int[patch.OperatorCount];
        var indices = new List<int>();
        for (int i = 0; i < audible.Length; i++)
        {
            map[i] = audible[i] ? indices.Count : -1;
            if (audible[i])
            {
                indices.Add(i);
            }
        }

        int n = indices.Count;
        OperatorIndices = [.. indices];
        Waveforms = new OperatorWaveform[n];
        Phases = new float[n];
        Shapes = new float[n];
        Morphs = new float[n];
        NoisePhases = new float[n];
        NoiseMs = new float[n];
        HarmonicLimits = new int[n];
        OutputGains = new float[n];
        NoiseColor = (float)patch.Noise.Color;
        NoiseCutoffHz = (float)patch.Noise.CutoffHz;
        Frequencies = new float[n];
        Amplitudes = new float[n];
        FrequencyVaries = new bool[n];
        _amplitudes = new Signal[n];

        PitchModel pitch = patch.Pitch;
        var pitched = new List<int>();
        var tracking = new List<float>();
        var detune = new List<float>();
        for (int k = 0; k < n; k++)
        {
            OperatorPatch op = patch.Operators[indices[k]];
            Waveforms[k] = op.Waveform;
            Phases[k] = (float)op.InitialPhase;
            Shapes[k] = (float)op.Shape;
            Morphs[k] = (float)op.Morph;
            NoisePhases[k] = (float)op.NoisePhase;
            NoiseMs[k] = (float)op.NoiseM;
            HarmonicLimits[k] = op.HarmonicLimit;
            OutputGains[k] = (float)patch.OutputLevels[indices[k]];

            if (op.Frequency.Mode == OperatorFrequencyMode.Fixed || pitch.IsStatic)
            {
                // Single precision in the order of the exported SynthDef: (freq * factor) + detune.
                Frequencies[k] = op.Frequency.Mode == OperatorFrequencyMode.Fixed
                    ? (float)(op.Frequency.FixedFrequencyHz + op.DetuneHz)
                    : ((float)request.FrequencyHz * (float)(op.Frequency.Ratio * Math.Pow(2.0, op.FineTuneCents / 1200.0))) + (float)op.DetuneHz;
            }
            else
            {
                FrequencyVaries[k] = true;
                pitched.Add(k);
                tracking.Add((float)(request.FrequencyHz * op.Frequency.Ratio * Math.Pow(2.0, op.FineTuneCents / 1200.0)));
                detune.Add((float)op.DetuneHz);
            }

            float scale = (float)(op.VelocityFactor(request.Velocity) * op.KeyScaling.LevelFactor(midiNote));
            _amplitudes[k] = new Signal(op.AmplitudeEnvelope, controlRate, op.KeyScaling.TimeFactor(midiNote), scale);
        }

        _pitchedOperators = [.. pitched];
        _tracking = [.. tracking];
        _detune = [.. detune];

        FilterTypes = new int[n];
        FilterBaseHz = new float[n];
        FilterOctaves = new float[n];
        FilterQ = new float[n];
        FormantRatios = new float[n];
        FormantLevels = new float[n];
        FilterGainsDb = new float[n];
        FilterOctavesVary = new bool[n];
        var filterSignals = new List<Signal>();
        var filterTargets = new List<int>();
        for (int k = 0; k < n; k++)
        {
            OperatorPatch op = patch.Operators[indices[k]];
            OperatorFilter? filter = op.Filter;
            FilterTypes[k] = OperatorFilterKernel.Code(filter);
            if (filter is null)
            {
                FilterQ[k] = 1f;
                FormantRatios[k] = 1f;
                continue;
            }

            HasFilters = true;

            // Single precision in the order of the exported SynthDef: (freq * ratio), or the fixed frequency.
            FilterBaseHz[k] = filter.IsKeyTracked ? (float)request.FrequencyHz * (float)filter.CutoffRatio : (float)filter.FixedHz;
            if (filter.Envelope is not null && filter.EnvelopeOctaves != 0.0)
            {
                FilterOctavesVary[k] = true;
                filterSignals.Add(new Signal(filter.Envelope, controlRate, op.KeyScaling.TimeFactor(midiNote), (float)filter.EnvelopeOctaves));
                filterTargets.Add(k);
            }

            FilterQ[k] = (float)filter.Q;
            FormantRatios[k] = (float)filter.FormantRatio;
            FormantLevels[k] = (float)filter.FormantLevel;
            FilterGainsDb[k] = (float)filter.PeakGainDb;
        }

        _filterSignals = [.. filterSignals];
        _filterSignalTargets = [.. filterTargets];

        var sources = new List<int>();
        var destinations = new List<int>();
        var amounts = new List<float>();
        var varies = new List<bool>();
        var edgeSignals = new List<Signal>();
        var edgeTargets = new List<int>();
        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            if (!edge.Enabled || edge.Amount == 0.0 || map[edge.SourceOperator] < 0 || map[edge.DestinationOperator] < 0)
            {
                continue;
            }

            if (edge.AmountEnvelope is not null)
            {
                edgeSignals.Add(new Signal(edge.AmountEnvelope, controlRate, 1.0, (float)edge.Amount));
                edgeTargets.Add(sources.Count);
            }

            varies.Add(edge.AmountEnvelope is not null);
            amounts.Add((float)edge.Amount);
            sources.Add(map[edge.SourceOperator]);
            destinations.Add(map[edge.DestinationOperator]);
        }

        EdgeSources = [.. sources];
        EdgeDestinations = [.. destinations];
        EdgeAmounts = [.. amounts];
        EdgeAmountVaries = [.. varies];
        _edgeSignals = [.. edgeSignals];
        _edgeSignalTargets = [.. edgeTargets];

        if (pitch.PitchEnvelope is not null && _pitchedOperators.Length > 0)
        {
            _hasPitchEnvelope = true;
            _pitchEnvelope = new Signal(pitch.PitchEnvelope, controlRate, 1.0, 1f);
        }

        if (pitch.Vibrato is { DepthCents: not 0.0 } vibrato && _pitchedOperators.Length > 0)
        {
            bool delayed = vibrato.DelaySeconds > 0 || vibrato.FadeInSeconds > 0;
            var depthEnvelope = new OperatorEnvelope(
                [
                    new EnvelopePoint(0, delayed ? 0 : 1),
                    new EnvelopePoint(vibrato.DelaySeconds, delayed ? 0 : 1),
                    new EnvelopePoint(vibrato.FadeInSeconds, 1),
                ],
                SustainPoint: null,
                ReleasePoint: 3);
            _hasVibrato = true;
            _vibratoDepth = new Signal(depthEnvelope, controlRate, 1.0, (float)vibrato.DepthCents);
            _vibratoIncrement = 2.0 * Math.PI * vibrato.RateHz / controlRate;
        }

        // A unit is constructed with the value of the first block.
        for (int k = 0; k < n; k++)
        {
            Amplitudes[k] = _amplitudes[k].Peek(firstGateOpen);
        }

        for (int i = 0; i < _edgeSignals.Length; i++)
        {
            EdgeAmounts[_edgeSignalTargets[i]] = _edgeSignals[i].Peek(firstGateOpen);
        }

        for (int i = 0; i < _filterSignals.Length; i++)
        {
            FilterOctaves[_filterSignalTargets[i]] = _filterSignals[i].Peek(firstGateOpen);
        }

        // The vibrato depth ignores the gate.
        SetFrequencies(
            _hasPitchEnvelope ? _pitchEnvelope.Peek(firstGateOpen) : 0f,
            _hasVibrato ? _vibratoDepth.Peek(true) : 0f,
            0);
    }

    /// <summary>Sample rate in hertz.</summary>
    public int SampleRate { get; }

    /// <summary>Samples per control block.</summary>
    public int BlockSize { get; }

    /// <summary>Number of audible operators.</summary>
    public int OperatorCount => OperatorIndices.Length;

    /// <summary>Number of live edges.</summary>
    public int EdgeCount => EdgeSources.Length;

    /// <summary>Patch index of every audible operator.</summary>
    public int[] OperatorIndices { get; }

    /// <summary>Waveform per audible operator.</summary>
    public OperatorWaveform[] Waveforms { get; }

    /// <summary>Phase offset in radians per audible operator.</summary>
    public float[] Phases { get; }

    /// <summary>Waveform shape per audible operator (pulse duty cycle, filter response, elliptic parameter m).</summary>
    public float[] Shapes { get; }

    /// <summary>Second waveform parameter per audible operator (elliptic sn-to-sd morph).</summary>
    public float[] Morphs { get; }

    /// <summary>Phase roughness per audible operator, radians per unit of shared noise.</summary>
    public float[] NoisePhases { get; }

    /// <summary>Elliptic-parameter roughness per audible operator.</summary>
    public float[] NoiseMs { get; }

    /// <summary>Colour of the shared noise, -1..1.</summary>
    public float NoiseColor { get; }

    /// <summary>Crossover of the shared noise filter in hertz.</summary>
    public float NoiseCutoffHz { get; }

    /// <summary>Harmonic limit per audible operator.</summary>
    public int[] HarmonicLimits { get; }

    /// <summary>Linear output gain per audible operator.</summary>
    public float[] OutputGains { get; }

    /// <summary>Source of every live edge as an index into the audible operators.</summary>
    public int[] EdgeSources { get; }

    /// <summary>Destination of every live edge as an index into the audible operators.</summary>
    public int[] EdgeDestinations { get; }

    /// <summary>True when any audible operator has a filter; the filter parts below are those of no filter otherwise.</summary>
    public bool HasFilters { get; }

    /// <summary>Filter type code per audible operator, <see cref="OperatorFilterKernel.Off"/> for none.</summary>
    public int[] FilterTypes { get; }

    /// <summary>Filter cutoff, centre or F1 per audible operator, before the envelope.</summary>
    public float[] FilterBaseHz { get; }

    /// <summary>Filter resonance per audible operator.</summary>
    public float[] FilterQ { get; }

    /// <summary>F2 / F1 per audible operator.</summary>
    public float[] FormantRatios { get; }

    /// <summary>Level of F2 per audible operator.</summary>
    public float[] FormantLevels { get; }

    /// <summary>Peak filter gain in decibels per audible operator.</summary>
    public float[] FilterGainsDb { get; }

    /// <summary>Current frequency in hertz per audible operator.</summary>
    public float[] Frequencies { get; }

    /// <summary>Current amplitude per audible operator.</summary>
    public float[] Amplitudes { get; }

    /// <summary>Current amount in radians per live edge.</summary>
    public float[] EdgeAmounts { get; }

    /// <summary>Current filter envelope offset in octaves per audible operator; zero without a filter envelope.</summary>
    public float[] FilterOctaves { get; }

    /// <summary>Per audible operator, whether its frequency follows the pitch model and so changes between blocks.</summary>
    internal bool[] FrequencyVaries { get; }

    /// <summary>Per live edge, whether its amount follows an envelope and so changes between blocks.</summary>
    internal bool[] EdgeAmountVaries { get; }

    /// <summary>Per audible operator, whether its filter offset follows an envelope and so changes between blocks.</summary>
    internal bool[] FilterOctavesVary { get; }

    /// <summary>Computes the values of the next control block.</summary>
    /// <param name="gateOpen">Whether the gate is open during that block.</param>
    public void Advance(bool gateOpen)
    {
        for (int k = 0; k < _amplitudes.Length; k++)
        {
            Amplitudes[k] = _amplitudes[k].Next(gateOpen);
        }

        for (int i = 0; i < _edgeSignals.Length; i++)
        {
            EdgeAmounts[_edgeSignalTargets[i]] = _edgeSignals[i].Next(gateOpen);
        }

        for (int i = 0; i < _filterSignals.Length; i++)
        {
            FilterOctaves[_filterSignalTargets[i]] = _filterSignals[i].Next(gateOpen);
        }

        // The vibrato ignores the gate and starts at phase zero in block 0.
        SetFrequencies(
            _hasPitchEnvelope ? _pitchEnvelope.Next(gateOpen) : 0f,
            _hasVibrato ? _vibratoDepth.Next(true) : 0f,
            _blocks);
        _blocks++;
    }

    private void SetFrequencies(float pitchCents, float vibratoDepth, int block)
    {
        if (_pitchedOperators.Length == 0)
        {
            return;
        }

        double cents = 0.0;
        if (_hasPitchEnvelope)
        {
            cents = pitchCents;
        }

        if (_hasVibrato)
        {
            cents += vibratoDepth * Math.Sin(_vibratoIncrement * block);
        }

        float ratio = (float)Math.Pow(2.0, cents / 1200.0);
        for (int i = 0; i < _pitchedOperators.Length; i++)
        {
            Frequencies[_pitchedOperators[i]] = (_tracking[i] * ratio) + _detune[i];
        }
    }

    /// <summary>An envelope stepped once per control block and scaled in single precision.</summary>
    private struct Signal
    {
        private readonly float _scale;
        private ControlRateEnvelope _runner;

        public Signal(OperatorEnvelope envelope, double controlRate, double timeScale, float scale)
        {
            _runner = new ControlRateEnvelope(envelope, controlRate, timeScale);
            _scale = scale;
        }

        /// <summary>Value of the next block.</summary>
        public float Next(bool gate) => (float)_runner.Next(gate) * _scale;

        /// <summary>Value the next block would have, without stepping.</summary>
        public readonly float Peek(bool gate)
        {
            ControlRateEnvelope copy = _runner;
            return (float)copy.Next(gate) * _scale;
        }
    }
}
