using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Compilation;

/// <summary>
/// Operator filters of the audible operators, in the order of <see cref="CompiledOperatorMatrixPatch.OperatorIndices"/>,
/// as the FMX6 UGen receives them: single-precision inputs, the envelope offset in octaves per control step.
/// </summary>
/// <param name="Types">Type code per operator, <see cref="OperatorFilterKernel.Off"/> for none.</param>
/// <param name="BaseHz">Cutoff, centre or F1 before the envelope.</param>
/// <param name="Octaves">Envelope offset; one entry when static, otherwise one per control step.</param>
/// <param name="Q">Resonance.</param>
/// <param name="FormantRatios">F2 / F1.</param>
/// <param name="FormantLevels">Level of F2.</param>
/// <param name="GainsDb">Peak gain.</param>
/// <param name="FormantFields">
/// The extra formant inputs, <see cref="OperatorFilterKernel.FormantFieldCount"/> per operator: the Q of F2, and ratio,
/// level and Q of F3..F5.
/// </param>
public sealed record CompiledFilters(int[] Types, float[] BaseHz, float[][] Octaves, float[] Q, float[] FormantRatios, float[] FormantLevels, float[] GainsDb, float[] FormantFields);

/// <summary>
/// A patch bound to one note and flattened for a sample loop: only audible operators and live edges remain, and
/// every control signal is evaluated once per control block. Control arrays have <c>BlockCount + 1</c> entries:
/// entry 0 is the value a unit sees when it is constructed, entry <c>b + 1</c> the value during block <c>b</c>, so a
/// renderer ramps from entry <c>b</c> to entry <c>b + 1</c> across block <c>b</c>.
/// </summary>
public sealed class CompiledOperatorMatrixPatch
{
    internal CompiledOperatorMatrixPatch(
        int sampleRate,
        int blockSize,
        int blockCount,
        int sampleCount,
        int[] operatorIndices,
        OperatorWaveform[] waveforms,
        float[][] frequencies,
        float[][] amplitudes,
        float[] phases,
        float[] shapes,
        float[] morphs,
        float[] noisePhases,
        float[] noiseMs,
        float noiseColor,
        float noiseCutoffHz,
        int[] harmonicLimits,
        float[] outputGains,
        int[] edgeSources,
        int[] edgeDestinations,
        float[][] edgeAmounts,
        CompiledFilters? filters)
    {
        SampleRate = sampleRate;
        BlockSize = blockSize;
        BlockCount = blockCount;
        SampleCount = sampleCount;
        OperatorIndices = operatorIndices;
        Waveforms = waveforms;
        Frequencies = frequencies;
        Amplitudes = amplitudes;
        Phases = phases;
        Shapes = shapes;
        Morphs = morphs;
        NoisePhases = noisePhases;
        NoiseMs = noiseMs;
        NoiseColor = noiseColor;
        NoiseCutoffHz = noiseCutoffHz;
        HarmonicLimits = harmonicLimits;
        OutputGains = outputGains;
        EdgeSources = edgeSources;
        EdgeDestinations = edgeDestinations;
        EdgeAmounts = edgeAmounts;
        Filters = filters;
    }

    /// <summary>Operator filters; null when no audible operator has one.</summary>
    public CompiledFilters? Filters { get; }

    /// <summary>Sample rate in hertz.</summary>
    public int SampleRate { get; }

    /// <summary>Samples per control block.</summary>
    public int BlockSize { get; }

    /// <summary>Number of control blocks; the last one may be cut short by <see cref="SampleCount"/>.</summary>
    public int BlockCount { get; }

    /// <summary>Number of samples to render.</summary>
    public int SampleCount { get; }

    /// <summary>Number of audible operators.</summary>
    public int OperatorCount => OperatorIndices.Length;

    /// <summary>Number of live edges.</summary>
    public int EdgeCount => EdgeSources.Length;

    /// <summary>Patch index of every audible operator.</summary>
    public int[] OperatorIndices { get; }

    /// <summary>Waveform per audible operator.</summary>
    public OperatorWaveform[] Waveforms { get; }

    /// <summary>Frequency in hertz per audible operator and control step; a single entry when constant.</summary>
    public float[][] Frequencies { get; }

    /// <summary>Amplitude per audible operator and control step.</summary>
    public float[][] Amplitudes { get; }

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

    /// <summary>Amount in radians per live edge and control step; a single entry when constant.</summary>
    public float[][] EdgeAmounts { get; }

    /// <summary>
    /// A control source over the compiled arrays, for a voice that renders this note: its current values step through
    /// entry 0, 1, 2 and so on, and after the last block they stay at the last entry. The gate is already in the
    /// arrays, so the source ignores the gate it is given.
    /// </summary>
    /// <returns>A new source, not advanced yet.</returns>
    public IControlSource Controls() => new CompiledControls(this);

    /// <summary>The compiled arrays as an <see cref="IControlSource"/>.</summary>
    private sealed class CompiledControls : IControlSource
    {
        private readonly CompiledOperatorMatrixPatch _compiled;
        private int _step;

        public CompiledControls(CompiledOperatorMatrixPatch compiled)
        {
            _compiled = compiled;
            int n = compiled.OperatorCount;
            CompiledFilters? filters = compiled.Filters;
            if (filters is null)
            {
                // The parts of no filter, as a control stream has them.
                FilterTypes = new int[n];
                FilterBaseHz = new float[n];
                FilterQ = new float[n];
                FormantRatios = new float[n];
                FormantLevels = new float[n];
                FormantFields = new float[n * OperatorFilterKernel.FormantFieldCount];
                FilterGainsDb = new float[n];
                Array.Fill(FilterTypes, OperatorFilterKernel.Off);
                Array.Fill(FilterQ, 1f);
                Array.Fill(FormantRatios, 1f);
                for (int k = 0; k < n; k++)
                {
                    OperatorFilterKernel.FormantFields(null, FormantFields.AsSpan(k * OperatorFilterKernel.FormantFieldCount, OperatorFilterKernel.FormantFieldCount));
                }
            }
            else
            {
                FilterTypes = filters.Types;
                FilterBaseHz = filters.BaseHz;
                FilterQ = filters.Q;
                FormantRatios = filters.FormantRatios;
                FormantLevels = filters.FormantLevels;
                FormantFields = filters.FormantFields;
                FilterGainsDb = filters.GainsDb;
            }

            Frequencies = new float[n];
            Amplitudes = new float[n];
            EdgeAmounts = new float[compiled.EdgeCount];
            FilterOctaves = new float[n];
            Load();
        }

        public int SampleRate => _compiled.SampleRate;

        public int BlockSize => _compiled.BlockSize;

        public int OperatorCount => _compiled.OperatorCount;

        public int EdgeCount => _compiled.EdgeCount;

        public int[] OperatorIndices => _compiled.OperatorIndices;

        public OperatorWaveform[] Waveforms => _compiled.Waveforms;

        public float[] Phases => _compiled.Phases;

        public float[] Shapes => _compiled.Shapes;

        public float[] Morphs => _compiled.Morphs;

        public float[] NoisePhases => _compiled.NoisePhases;

        public float[] NoiseMs => _compiled.NoiseMs;

        public float NoiseColor => _compiled.NoiseColor;

        public float NoiseCutoffHz => _compiled.NoiseCutoffHz;

        public int[] HarmonicLimits => _compiled.HarmonicLimits;

        public float[] OutputGains => _compiled.OutputGains;

        public int[] EdgeSources => _compiled.EdgeSources;

        public int[] EdgeDestinations => _compiled.EdgeDestinations;

        public bool HasFilters => _compiled.Filters is not null;

        public int[] FilterTypes { get; }

        public float[] FilterBaseHz { get; }

        public float[] FilterQ { get; }

        public float[] FormantRatios { get; }

        public float[] FormantLevels { get; }

        public float[] FormantFields { get; }

        public float[] FilterGainsDb { get; }

        public float[] Frequencies { get; }

        public float[] Amplitudes { get; }

        public float[] EdgeAmounts { get; }

        public float[] FilterOctaves { get; }

        public void Advance(bool gateOpen)
        {
            if (_step < _compiled.BlockCount)
            {
                _step++;
                Load();
            }
        }

        /// <summary>Copies entry <see cref="_step"/> of every signal into the current values.</summary>
        private void Load()
        {
            Copy(_compiled.Frequencies, Frequencies, _step);
            Copy(_compiled.Amplitudes, Amplitudes, _step);
            Copy(_compiled.EdgeAmounts, EdgeAmounts, _step);
            if (_compiled.Filters is { } filters)
            {
                Copy(filters.Octaves, FilterOctaves, _step);
            }
        }

        private static void Copy(float[][] signals, float[] current, int step)
        {
            for (int i = 0; i < signals.Length; i++)
            {
                float[] signal = signals[i];
                current[i] = signal.Length == 1 ? signal[0] : signal[step];
            }
        }
    }
}

/// <summary>Turns a canonical patch and a note into a <see cref="CompiledOperatorMatrixPatch"/>. Stateless and thread-safe.</summary>
public static class PatchCompiler
{
    /// <summary>Compiles a patch for one note by running a <see cref="ControlStream"/> through the whole note.</summary>
    /// <param name="patch">Patch; must be well formed.</param>
    /// <param name="request">Note to play.</param>
    /// <param name="controlBlockSize">Samples per control block.</param>
    /// <exception cref="ArgumentException">The patch is not well formed.</exception>
    public static CompiledOperatorMatrixPatch Compile(OperatorMatrixPatch patch, RenderRequest request, int controlBlockSize = 64)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(controlBlockSize, 1);

        int sampleCount = Math.Max(0, request.TotalSamples);
        int blockCount = (sampleCount + controlBlockSize - 1) / controlBlockSize;
        int steps = blockCount + 1;

        // The gate closes in the control block that contains the end of the note, as measured on scsynth: a gate
        // message takes effect at the start of the block its time stamp falls into.
        int gateOffBlock = request.NoteSamples / controlBlockSize;

        // A unit is constructed with the value of the first block, so the stream needs to know that block's gate.
        var stream = new ControlStream(patch, request.ToVoiceRequest(), controlBlockSize, firstGateOpen: blockCount > 0 && gateOffBlock > 0);
        float[][] frequencies = Signals(stream.Frequencies, stream.FrequencyVaries, steps);
        float[][] amplitudes = Signals(stream.Amplitudes, null, steps);
        float[][] edgeAmounts = Signals(stream.EdgeAmounts, stream.EdgeAmountVaries, steps);
        float[][]? octaves = stream.HasFilters ? Signals(stream.FilterOctaves, stream.FilterOctavesVary, steps) : null;
        for (int b = 0; b < blockCount; b++)
        {
            stream.Advance(b < gateOffBlock);
            Record(frequencies, stream.Frequencies, b + 1);
            Record(amplitudes, stream.Amplitudes, b + 1);
            Record(edgeAmounts, stream.EdgeAmounts, b + 1);
            if (octaves is not null)
            {
                Record(octaves, stream.FilterOctaves, b + 1);
            }
        }

        CompiledFilters? filters = stream.HasFilters
            ? new CompiledFilters(stream.FilterTypes, stream.FilterBaseHz, octaves!, stream.FilterQ, stream.FormantRatios, stream.FormantLevels, stream.FilterGainsDb, stream.FormantFields)
            : null;
        return new CompiledOperatorMatrixPatch(
            stream.SampleRate,
            controlBlockSize,
            blockCount,
            sampleCount,
            stream.OperatorIndices,
            stream.Waveforms,
            frequencies,
            amplitudes,
            stream.Phases,
            stream.Shapes,
            stream.Morphs,
            stream.NoisePhases,
            stream.NoiseMs,
            stream.NoiseColor,
            stream.NoiseCutoffHz,
            stream.HarmonicLimits,
            stream.OutputGains,
            stream.EdgeSources,
            stream.EdgeDestinations,
            edgeAmounts,
            filters);
    }

    /// <summary>
    /// One array per signal, holding its construction value: <paramref name="steps"/> entries for a signal that
    /// varies, a single entry for a constant one.
    /// </summary>
    /// <param name="current">Construction values.</param>
    /// <param name="varies">Which signals vary; null when all do.</param>
    /// <param name="steps">Construction value plus one entry per block.</param>
    private static float[][] Signals(float[] current, bool[]? varies, int steps)
    {
        var signals = new float[current.Length][];
        for (int i = 0; i < current.Length; i++)
        {
            signals[i] = varies is null || varies[i] ? new float[steps] : new float[1];
            signals[i][0] = current[i];
        }

        return signals;
    }

    /// <summary>Stores the current values of the varying signals as entry <paramref name="step"/>.</summary>
    private static void Record(float[][] signals, float[] current, int step)
    {
        for (int i = 0; i < signals.Length; i++)
        {
            if (signals[i].Length > 1)
            {
                signals[i][step] = current[i];
            }
        }
    }
}
