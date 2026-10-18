using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Fmx6Compatible.Rendering;

/// <summary>
/// The FMX6 kernel of <see cref="Fmx6CompatibleOperatorMatrixRenderer"/> as a voice: the offline render and a held note
/// both run this code, one control block at a time.
/// </summary>
/// <remarks>Mutable; see <see cref="ControlBlockVoice"/> for threading.</remarks>
internal sealed class Fmx6Voice : ControlBlockVoice
{
    private const double TwoPi = 2.0 * Math.PI;
    private const float InvTwoPi = (float)(1.0 / (2.0 * Math.PI));

    private int _operators;
    private int _edges;
    private float _sampleRate;
    private float _sampleDur;
    private float _slopeFactor;
    private double[] _phaseCycles = [];
    private float[] _freq = [];
    private float[] _freqInc = [];
    private float[] _amp = [];
    private float[] _ampInc = [];
    private float[] _lastPhaseMod = [];
    private float[] _mem = [];
    private float[] _sampleHold = [];
    private uint[] _rng = [];
    private double[] _filterState1 = [];
    private double[] _filterState2 = [];
    private float[] _matrixPhaseMod = [];
    private float[] _mod = [];
    private float[] _modInc = [];
    private int[] _wave = [];
    private int[] _harmonicLimit = [];
    private float[] _filterOct = [];
    private float[] _filterOctInc = [];
    private SvfState[] _filterA = [];
    private SvfState[] _filterB = [];

    /// <summary>Creates a voice whose control source is created at its first render.</summary>
    /// <param name="sampleRate">Sample rate in hertz.</param>
    /// <param name="start">Creates the control source; receives whether the gate is open in the first block.</param>
    public Fmx6Voice(int sampleRate, Func<bool, IControlSource> start)
        : base(sampleRate, start)
    {
    }

    /// <summary>Creates a voice over a control source whose gate is already decided.</summary>
    /// <param name="controls">Control source, not advanced yet.</param>
    public Fmx6Voice(IControlSource controls)
        : base(controls)
    {
    }

    /// <inheritdoc />
    protected override void Start(IControlSource controls)
    {
        int n = _operators = controls.OperatorCount;
        int edges = _edges = controls.EdgeCount;
        _sampleRate = controls.SampleRate;
        _sampleDur = (float)(1.0 / controls.SampleRate);
        _slopeFactor = 1f / controls.BlockSize;

        _phaseCycles = new double[n];
        _freq = [.. controls.Frequencies];
        _freqInc = new float[n];
        _amp = [.. controls.Amplitudes];
        _ampInc = new float[n];
        _lastPhaseMod = [.. controls.Phases];
        _mem = new float[n];
        _sampleHold = new float[n];
        _rng = new uint[n];
        _filterState1 = new double[n];
        _filterState2 = new double[n];
        _matrixPhaseMod = new float[n];
        _mod = [.. controls.EdgeAmounts];
        _modInc = new float[edges];
        _wave = new int[n];
        _harmonicLimit = new int[n];
        _filterOct = [.. controls.FilterOctaves];
        _filterOctInc = new float[n];
        _filterA = new SvfState[n];
        _filterB = new SvfState[n];

        for (int k = 0; k < n; k++)
        {
            _wave[k] = Fmx6CompatibleOperatorMatrixRenderer.WaveIndex(controls.Waveforms[k]);
            _harmonicLimit[k] = Math.Clamp(controls.HarmonicLimits[k], 1, 128);

            // The generator is seeded by the operator slot in the UGen, which is the index in the patch.
            _rng[k] = 0x9E3779B9u ^ unchecked(0x85EBCA6Bu * (uint)(controls.OperatorIndices[k] + 1));
            _sampleHold[k] = RandBipolar(ref _rng[k]);
        }
    }

    /// <inheritdoc />
    protected override void RenderBlock(IControlSource controls, Span<float> block)
    {
        int n = _operators;
        int edges = _edges;
        float sampleRate = _sampleRate;
        float sampleDur = _sampleDur;
        float slopeFactor = _slopeFactor;
        double[] phaseCycles = _phaseCycles;
        float[] freq = _freq;
        float[] freqInc = _freqInc;
        float[] amp = _amp;
        float[] ampInc = _ampInc;
        float[] lastPhaseMod = _lastPhaseMod;
        float[] mem = _mem;
        float[] sampleHold = _sampleHold;
        uint[] rng = _rng;
        double[] filterState1 = _filterState1;
        double[] filterState2 = _filterState2;
        float[] matrixPhaseMod = _matrixPhaseMod;
        float[] mod = _mod;
        float[] modInc = _modInc;
        int[] wave = _wave;
        int[] harmonicLimit = _harmonicLimit;
        float[] filterOct = _filterOct;
        float[] filterOctInc = _filterOctInc;
        SvfState[] filterA = _filterA;
        SvfState[] filterB = _filterB;
        float[] frequencies = controls.Frequencies;
        float[] amplitudes = controls.Amplitudes;
        float[] amounts = controls.EdgeAmounts;
        float[] phaseOffset = controls.Phases;
        float[] shape = controls.Shapes;
        float[] gain = controls.OutputGains;
        int[] source = controls.EdgeSources;
        int[] destination = controls.EdgeDestinations;
        bool hasFilters = controls.HasFilters;
        int[] filterTypes = controls.FilterTypes;
        float[] filterBaseHz = controls.FilterBaseHz;
        float[] filterQ = controls.FilterQ;
        float[] formantRatios = controls.FormantRatios;
        float[] formantLevels = controls.FormantLevels;
        float[] filterGainsDb = controls.FilterGainsDb;
        float[] octaves = controls.FilterOctaves;

        for (int k = 0; k < n; k++)
        {
            freqInc[k] = (frequencies[k] - freq[k]) * slopeFactor;
            ampInc[k] = (amplitudes[k] - amp[k]) * slopeFactor;
            if (hasFilters)
            {
                filterOctInc[k] = (octaves[k] - filterOct[k]) * slopeFactor;
            }
        }

        for (int e = 0; e < edges; e++)
        {
            modInc[e] = (amounts[e] - mod[e]) * slopeFactor;
        }

        for (int i = 0; i < block.Length; i++)
        {
            // All modulation of this sample comes from the outputs of the previous one.
            Array.Clear(matrixPhaseMod);
            for (int e = 0; e < edges; e++)
            {
                matrixPhaseMod[destination[e]] += mod[e] * mem[source[e]];
            }

            float mix = 0f;
            for (int k = 0; k < n; k++)
            {
                float totalPhaseMod = phaseOffset[k] + matrixPhaseMod[k];
                float pmDelta = totalPhaseMod - lastPhaseMod[k];
                float effectiveHz = freq[k] + (pmDelta * sampleRate * InvTwoPi);
                double phaseRad = (phaseCycles[k] * TwoPi) + totalPhaseMod;

                double next = phaseCycles[k] + ((double)freq[k] * (double)sampleDur);
                bool wrapped = next >= 1.0 || next < 0.0;
                next -= Math.Floor(next);

                float osc = wave[k] switch
                {
                    <= 4 => WaveformHarmonic(wave[k], phaseRad, ClampHarmonics(effectiveHz, harmonicLimit[k], sampleRate), shape[k]),
                    5 => sampleHold[k],
                    7 => FilteredNoise(ref rng[k], ref filterState1[k], ref filterState2[k], freq[k], shape[k], sampleRate),
                    _ => RandBipolar(ref rng[k]),
                };

                float shaped = !hasFilters || filterTypes[k] == OperatorFilterKernel.Off
                    ? osc
                    : (float)OperatorFilterKernel.Process(
                        filterTypes[k],
                        osc,
                        filterBaseHz[k],
                        filterOct[k],
                        filterQ[k],
                        formantRatios[k],
                        formantLevels[k],
                        filterGainsDb[k],
                        sampleRate,
                        ref filterA[k],
                        ref filterB[k]);
                float y = amp[k] * shaped;
                mix += y * gain[k];
                if (wave[k] == 5 && wrapped)
                {
                    sampleHold[k] = RandBipolar(ref rng[k]);
                }

                // Committed after the whole sample in the UGen; the modulation above already used the old values.
                mem[k] = y;
                phaseCycles[k] = next;
                lastPhaseMod[k] = totalPhaseMod;
                freq[k] += freqInc[k];
                amp[k] += ampInc[k];
                filterOct[k] += filterOctInc[k];
            }

            for (int e = 0; e < edges; e++)
            {
                mod[e] += modInc[e];
            }

            block[i] = mix;
        }
    }

    private static uint XorShift32(ref uint state)
    {
        uint x = state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        state = x;
        return x;
    }

    private static float RandBipolar(ref uint state)
    {
        uint x = XorShift32(ref state) >> 8;
        return (x * (2.0f / 16777216.0f)) - 1.0f;
    }

    /// <summary>
    /// White noise through a TPT state-variable filter with Q = 1/sqrt(2), as in the UGen: shape 0 = low-pass,
    /// 0.5 = band-pass with unity gain at the centre, 1 = high-pass, crossfaded in between.
    /// </summary>
    private static float FilteredNoise(ref uint rng, ref double ic1, ref double ic2, float cutoffHz, float shape, float sampleRate)
    {
        double x = RandBipolar(ref rng);
        double fc = Math.Max(10.0, Math.Min(Math.Abs(cutoffHz), 0.49 * (double)sampleRate));
        double g = Math.Tan(Math.PI * fc / sampleRate);
        const double k = 1.4142135623730951;
        double a1 = 1.0 / (1.0 + (g * (g + k)));
        double a2 = g * a1;
        double a3 = g * a2;

        double v3 = x - ic2;
        double v1 = (a1 * ic1) + (a2 * v3);
        double v2 = ic2 + (a2 * ic1) + (a3 * v3);
        ic1 = (2.0 * v1) - ic1;
        ic2 = (2.0 * v2) - ic2;

        double lp = v2;
        double bp = k * v1;
        double hp = x - (k * v1) - v2;

        double m = Math.Max(0.0, Math.Min(shape, 1.0));
        double y = m < 0.5
            ? ((1.0 - (2.0 * m)) * lp) + (2.0 * m * bp)
            : ((2.0 - (2.0 * m)) * bp) + (((2.0 * m) - 1.0) * hp);
        return (float)y;
    }

    private static int ClampHarmonics(float effectiveHz, int requested, float sampleRate)
    {
        requested = Math.Max(1, Math.Min(requested, 128));
        float f = Math.Abs(effectiveHz);
        if (f < 1.0e-6f)
        {
            return requested;
        }

        float safeNyquist = 0.45f * sampleRate;
        int byFrequency = (int)Math.Floor(safeNyquist / f);
        return Math.Max(1, Math.Min(requested, byFrequency));
    }

    private static float WaveformHarmonic(int wave, double phaseRad, int harmonics, float shape)
    {
        double p = Math.IEEERemainder(phaseRad, TwoPi);
        double s1 = Math.Sin(p);
        double c1 = Math.Cos(p);
        if (wave == 0)
        {
            return (float)s1;
        }

        double sinN = s1;
        double cosN = c1;
        double sum = 0.0;
        float duty = Math.Max(0.01f, Math.Min(shape, 0.99f));
        for (int n = 1; n <= harmonics; n++)
        {
            switch (wave)
            {
                case 1:
                    if ((n & 1) != 0)
                    {
                        int oddIndex = (n - 1) / 2;
                        double sign = (oddIndex & 1) != 0 ? -1.0 : 1.0;
                        sum += sign * sinN / ((double)n * n);
                    }

                    break;
                case 2:
                    sum += -sinN / n;
                    break;
                case 3:
                    if ((n & 1) != 0)
                    {
                        sum += sinN / n;
                    }

                    break;
                case 4:
                    double nd = TwoPi * n * (double)duty;
                    double a = 2.0 * Math.Sin(nd) / (Math.PI * n);
                    double b = 2.0 * (1.0 - Math.Cos(nd)) / (Math.PI * n);
                    sum += (a * cosN) + (b * sinN);
                    break;
            }

            double nextSin = (sinN * c1) + (cosN * s1);
            double nextCos = (cosN * c1) - (sinN * s1);
            sinN = nextSin;
            cosN = nextCos;
        }

        return wave switch
        {
            1 => (float)((8.0 / (Math.PI * Math.PI)) * sum),
            2 => (float)((2.0 / Math.PI) * sum),
            3 => (float)((4.0 / Math.PI) * sum),
            4 => (float)((2.0 * duty - 1.0) + sum),
            _ => (float)s1,
        };
    }
}
