using HearSay.Synthesis.Envelopes;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Compilation;

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
        float[][] edgeAmounts)
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
    }

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
}

/// <summary>Turns a canonical patch and a note into a <see cref="CompiledOperatorMatrixPatch"/>. Stateless and thread-safe.</summary>
public static class PatchCompiler
{
    /// <summary>Compiles a patch for one note.</summary>
    /// <param name="patch">Patch; must be well formed.</param>
    /// <param name="request">Note to play.</param>
    /// <param name="controlBlockSize">Samples per control block.</param>
    /// <exception cref="ArgumentException">The patch is not well formed.</exception>
    public static CompiledOperatorMatrixPatch Compile(OperatorMatrixPatch patch, RenderRequest request, int controlBlockSize = 64)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(controlBlockSize, 1);
        IReadOnlyList<string> problems = patch.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException("The patch is not well formed: " + string.Join(" ", problems), nameof(patch));
        }

        int sampleRate = request.SampleRate;
        int sampleCount = Math.Max(0, request.TotalSamples);
        int blockCount = (sampleCount + controlBlockSize - 1) / controlBlockSize;
        int steps = blockCount + 1;
        double controlRate = (double)sampleRate / controlBlockSize;

        // The gate closes in the control block that contains the end of the note, as measured on scsynth: a gate
        // message takes effect at the start of the block its time stamp falls into.
        int gateOffBlock = request.NoteSamples / controlBlockSize;
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

        float[]? pitchRatio = patch.Pitch.IsStatic ? null : PitchRatio(patch.Pitch, steps, gateOffBlock, controlRate);

        int n = indices.Count;
        var waveforms = new OperatorWaveform[n];
        var frequencies = new float[n][];
        var amplitudes = new float[n][];
        var phases = new float[n];
        var shapes = new float[n];
        var morphs = new float[n];
        var noisePhases = new float[n];
        var noiseMs = new float[n];
        var harmonicLimits = new int[n];
        var gains = new float[n];
        for (int k = 0; k < n; k++)
        {
            OperatorPatch op = patch.Operators[indices[k]];
            waveforms[k] = op.Waveform;
            phases[k] = (float)op.InitialPhase;
            shapes[k] = (float)op.Shape;
            morphs[k] = (float)op.Morph;
            noisePhases[k] = (float)op.NoisePhase;
            noiseMs[k] = (float)op.NoiseM;
            harmonicLimits[k] = op.HarmonicLimit;
            gains[k] = (float)patch.OutputLevels[indices[k]];

            if (op.Frequency.Mode == OperatorFrequencyMode.Fixed || pitchRatio is null)
            {
                // Single precision in the order of the exported SynthDef: (freq * factor) + detune.
                frequencies[k] = op.Frequency.Mode == OperatorFrequencyMode.Fixed
                    ? [(float)(op.Frequency.FixedFrequencyHz + op.DetuneHz)]
                    : [((float)request.FrequencyHz * (float)(op.Frequency.Ratio * Math.Pow(2.0, op.FineTuneCents / 1200.0))) + (float)op.DetuneHz];
            }
            else
            {
                float tracking = (float)(request.FrequencyHz * op.Frequency.Ratio * Math.Pow(2.0, op.FineTuneCents / 1200.0));
                float detune = (float)op.DetuneHz;
                var f = new float[steps];
                for (int s = 0; s < steps; s++)
                {
                    f[s] = (tracking * pitchRatio[s]) + detune;
                }

                frequencies[k] = f;
            }

            float scale = (float)(op.VelocityFactor(request.Velocity) * op.KeyScaling.LevelFactor(midiNote));
            amplitudes[k] = Run(op.AmplitudeEnvelope, steps, gateOffBlock, controlRate, op.KeyScaling.TimeFactor(midiNote), scale);
        }

        var sources = new List<int>();
        var destinations = new List<int>();
        var amounts = new List<float[]>();
        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            if (!edge.Enabled || edge.Amount == 0.0 || map[edge.SourceOperator] < 0 || map[edge.DestinationOperator] < 0)
            {
                continue;
            }

            sources.Add(map[edge.SourceOperator]);
            destinations.Add(map[edge.DestinationOperator]);
            amounts.Add(edge.AmountEnvelope is null
                ? [(float)edge.Amount]
                : Run(edge.AmountEnvelope, steps, gateOffBlock, controlRate, 1.0, (float)edge.Amount));
        }

        return new CompiledOperatorMatrixPatch(
            sampleRate,
            controlBlockSize,
            blockCount,
            sampleCount,
            [.. indices],
            waveforms,
            frequencies,
            amplitudes,
            phases,
            shapes,
            morphs,
            noisePhases,
            noiseMs,
            (float)patch.Noise.Color,
            (float)patch.Noise.CutoffHz,
            harmonicLimits,
            gains,
            [.. sources],
            [.. destinations],
            [.. amounts]);
    }

    /// <summary>Evaluates an envelope per control step: entry 0 is the construction value, entry b + 1 belongs to block b.</summary>
    private static float[] Run(OperatorEnvelope envelope, int steps, int gateOffBlock, double controlRate, double timeScale, float scale)
    {
        var runner = new ControlRateEnvelope(envelope, controlRate, timeScale);
        var values = new float[steps];
        for (int b = 0; b < steps - 1; b++)
        {
            values[b + 1] = (float)runner.Next(b < gateOffBlock) * scale;
        }

        // A unit is constructed with the value of the first block.
        values[0] = steps > 1 ? values[1] : (float)envelope.EffectiveLevel(0) * scale;
        return values;
    }

    private static float[] PitchRatio(PitchModel pitch, int steps, int gateOffBlock, double controlRate)
    {
        var cents = new double[steps];
        if (pitch.PitchEnvelope is not null)
        {
            float[] envelope = Run(pitch.PitchEnvelope, steps, gateOffBlock, controlRate, 1.0, 1f);
            for (int s = 0; s < steps; s++)
            {
                cents[s] = envelope[s];
            }
        }

        if (pitch.Vibrato is { DepthCents: not 0.0 } vibrato)
        {
            var depthEnvelope = new OperatorEnvelope(
                [
                    new EnvelopePoint(0, vibrato.DelaySeconds > 0 || vibrato.FadeInSeconds > 0 ? 0 : 1),
                    new EnvelopePoint(vibrato.DelaySeconds, vibrato.DelaySeconds > 0 || vibrato.FadeInSeconds > 0 ? 0 : 1),
                    new EnvelopePoint(vibrato.FadeInSeconds, 1),
                ],
                SustainPoint: null,
                ReleasePoint: 3);
            float[] depth = Run(depthEnvelope, steps, int.MaxValue, controlRate, 1.0, (float)vibrato.DepthCents);
            double increment = 2.0 * Math.PI * vibrato.RateHz / controlRate;
            for (int s = 0; s < steps; s++)
            {
                // Block b is entry b + 1; the oscillator starts at phase zero in block 0.
                int block = Math.Max(0, s - 1);
                cents[s] += depth[s] * Math.Sin(increment * block);
            }
        }

        var ratio = new float[steps];
        for (int s = 0; s < steps; s++)
        {
            ratio[s] = (float)Math.Pow(2.0, cents[s] / 1200.0);
        }

        return ratio;
    }
}
