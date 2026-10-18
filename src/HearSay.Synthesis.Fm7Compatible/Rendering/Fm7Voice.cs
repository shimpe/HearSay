using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Fm7Compatible.Rendering;

/// <summary>
/// The FM7 kernel of <see cref="Fm7CompatibleOperatorMatrixRenderer"/> as a voice: the offline render and a held note
/// both run this code, one control block at a time.
/// </summary>
/// <remarks>Mutable; see <see cref="ControlBlockVoice"/> for threading.</remarks>
internal sealed class Fm7Voice : ControlBlockVoice
{
    private const int TableSize = 8192;
    private const int TableMask = TableSize - 1;

    // Wavetable format of the server: value = First + Second * (1 + fraction).
    private static readonly float[] First = new float[TableSize];
    private static readonly float[] Second = new float[TableSize];

    private int _operators;
    private int _edges;
    private float _cpsToInc;
    private float _radToInc;
    private float _slopeFactor;
    private int[] _phase = [];
    private float[] _freq = [];
    private float[] _freqInc = [];
    private float[] _amp = [];
    private float[] _ampInc = [];
    private float[] _mem = [];
    private float[] _deviation = [];
    private float[] _mod = [];
    private float[] _modInc = [];

    static Fm7Voice()
    {
        var sine = new float[TableSize + 1];
        for (int i = 0; i <= TableSize; i++)
        {
            sine[i] = (float)Math.Sin(i * (2.0 * Math.PI / TableSize));
        }

        sine[TableSize] = sine[0];
        for (int i = 0; i < TableSize; i++)
        {
            float a = sine[i];
            float b = sine[i + 1];
            First[i] = (2f * a) - b;
            Second[i] = b - a;
        }
    }

    /// <summary>Creates a voice whose control source is created at its first render.</summary>
    /// <param name="sampleRate">Sample rate in hertz.</param>
    /// <param name="start">Creates the control source; receives whether the gate is open in the first block.</param>
    public Fm7Voice(int sampleRate, Func<bool, IControlSource> start)
        : base(sampleRate, start)
    {
    }

    /// <summary>Creates a voice over a control source whose gate is already decided.</summary>
    /// <param name="controls">Control source, not advanced yet.</param>
    public Fm7Voice(IControlSource controls)
        : base(controls)
    {
    }

    /// <inheritdoc />
    protected override void Start(IControlSource controls)
    {
        int n = _operators = controls.OperatorCount;
        int edges = _edges = controls.EdgeCount;
        _cpsToInc = (float)(TableSize * (1.0 / controls.SampleRate) * 65536.0);
        _radToInc = (float)(TableSize * (1.0 / (2.0 * Math.PI)) * 65536.0);
        _slopeFactor = 1f / controls.BlockSize;

        _phase = new int[n];
        _freq = [.. controls.Frequencies];
        _freqInc = new float[n];
        _amp = [.. controls.Amplitudes];
        _ampInc = new float[n];
        _mem = new float[n];
        _deviation = new float[n];
        _mod = [.. controls.EdgeAmounts];
        _modInc = new float[edges];

        // The unit computes one sample while it is constructed. Its output is discarded, its state is kept.
        Step(controls.Phases, controls.EdgeSources, controls.EdgeDestinations);
    }

    /// <inheritdoc />
    protected override void RenderBlock(IControlSource controls, Span<float> block)
    {
        int n = _operators;
        int edges = _edges;
        float slopeFactor = _slopeFactor;
        float[] freq = _freq;
        float[] freqInc = _freqInc;
        float[] amp = _amp;
        float[] ampInc = _ampInc;
        float[] mem = _mem;
        float[] mod = _mod;
        float[] modInc = _modInc;
        float[] frequencies = controls.Frequencies;
        float[] amplitudes = controls.Amplitudes;
        float[] amounts = controls.EdgeAmounts;
        float[] phaseOffset = controls.Phases;
        float[] gain = controls.OutputGains;
        int[] source = controls.EdgeSources;
        int[] destination = controls.EdgeDestinations;

        for (int k = 0; k < n; k++)
        {
            freqInc[k] = (frequencies[k] - freq[k]) * slopeFactor;
            ampInc[k] = (amplitudes[k] - amp[k]) * slopeFactor;
        }

        for (int e = 0; e < edges; e++)
        {
            modInc[e] = (amounts[e] - mod[e]) * slopeFactor;
        }

        for (int i = 0; i < block.Length; i++)
        {
            Step(phaseOffset, source, destination);
            float mix = 0f;
            for (int k = 0; k < n; k++)
            {
                mix += mem[k] * gain[k];
                freq[k] += freqInc[k];
                amp[k] += ampInc[k];
            }

            for (int e = 0; e < edges; e++)
            {
                mod[e] += modInc[e];
            }

            block[i] = mix;
        }
    }

    /// <summary>Float to 32-bit conversion with the out-of-range result of the x86 conversion the backend compiles to.</summary>
    private static int ToFixed(float value) =>
        value >= 2147483648f || value < -2147483648f || float.IsNaN(value) ? int.MinValue : (int)value;

    /// <summary>One sample: all phase deviations from the previous outputs first, then all operators.</summary>
    private void Step(float[] phaseOffset, int[] source, int[] destination)
    {
        int n = _operators;
        int edges = _edges;
        int[] phase = _phase;
        float[] freq = _freq;
        float[] amp = _amp;
        float[] mem = _mem;
        float[] deviation = _deviation;
        float[] mod = _mod;
        float cpsToInc = _cpsToInc;
        float radToInc = _radToInc;

        for (int k = 0; k < n; k++)
        {
            deviation[k] = phaseOffset[k];
        }

        for (int e = 0; e < edges; e++)
        {
            deviation[destination[e]] += mod[e] * mem[source[e]];
        }

        for (int k = 0; k < n; k++)
        {
            int p = unchecked(phase[k] + ToFixed(deviation[k] * radToInc));
            int index = (p >> 16) & TableMask;
            float fraction = 1f + ((p & 0xFFFF) * (1f / 65536f));
            mem[k] = amp[k] * (First[index] + (Second[index] * fraction));
            phase[k] = unchecked(phase[k] + ToFixed(freq[k] * cpsToInc));
        }
    }
}
