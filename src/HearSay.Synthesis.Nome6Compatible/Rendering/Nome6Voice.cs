using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Nome6Compatible.Rendering;

/// <summary>
/// The Nome6 kernel of <see cref="Nome6CompatibleOperatorMatrixRenderer"/> as a voice: the offline render and a held
/// note both run this code, one control block at a time. The oversampled matrix, the half-band decimators, the shared
/// noise and the prepared elliptic coefficients all carry over from one block to the next.
/// </summary>
/// <remarks>
/// Every voice starts its shared noise from the same seed as the offline render, so a voice plays exactly the samples
/// of the offline note. Mutable; see <see cref="ControlBlockVoice"/> for threading.
/// </remarks>
internal sealed class Nome6Voice : ControlBlockVoice
{
    private const int Oversample = Nome6CompatibleOperatorMatrixRenderer.Oversample;
    private const int MaxPartials = 24;
    private const double TwoPi = 2.0 * Math.PI;
    private const double HalfPi = 0.5 * Math.PI;

    private int _operators;
    private int _edges;
    private double _internalRate;
    private double[] _phase = [];
    private Prepared[] _prepared = [];
    private Prepared?[] _preparedLow = [];
    private Prepared?[] _preparedHigh = [];
    private double[] _family = [];
    private bool[] _isNoise = [];
    private double[] _noisePhase = [];
    private double[] _noiseM = [];
    private double[] _prevOut = [];
    private double[] _next = [];
    private double[] _pm = [];
    private double[] _dPhase = [];
    private double[] _freq = [];
    private double[] _freqInc = [];
    private double[] _amp = [];
    private double[] _ampInc = [];
    private double[] _noiseAmp = [];
    private double[] _noiseAmpInc = [];
    private int[] _partials = [];
    private HalfbandDecimator[] _decimators = [];
    private double[] _mod = [];
    private double[] _modInc = [];
    private bool _anyRoughness;
    private SharedNoiseSource _noise = null!;
    private double _noiseColor;

    /// <summary>Creates a voice whose control source is created at its first render.</summary>
    /// <param name="sampleRate">Sample rate in hertz.</param>
    /// <param name="start">Creates the control source; receives whether the gate is open in the first block.</param>
    public Nome6Voice(int sampleRate, Func<bool, IControlSource> start)
        : base(sampleRate, start)
    {
    }

    /// <summary>Creates a voice over a control source whose gate is already decided.</summary>
    /// <param name="controls">Control source, not advanced yet.</param>
    public Nome6Voice(IControlSource controls)
        : base(controls)
    {
    }

    /// <inheritdoc />
    protected override void Start(IControlSource controls)
    {
        int n = _operators = controls.OperatorCount;
        _edges = controls.EdgeCount;
        _internalRate = (double)controls.SampleRate * Oversample;
        _phase = new double[n];
        _prepared = new Prepared[n];
        _preparedLow = new Prepared?[n];
        _preparedHigh = new Prepared?[n];
        _family = new double[n];
        _isNoise = new bool[n];
        _noisePhase = new double[n];
        _noiseM = new double[n];
        _prevOut = new double[n];
        _next = new double[n];
        _pm = new double[n];
        _dPhase = new double[n];
        _freq = new double[n];
        _freqInc = new double[n];
        _amp = new double[n];
        _ampInc = new double[n];
        _noiseAmp = new double[n];
        _noiseAmpInc = new double[n];
        _partials = new int[n];
        _decimators = new HalfbandDecimator[n];
        _mod = [.. controls.EdgeAmounts.Select(static a => (double)a)];
        _modInc = new double[_edges];
        _anyRoughness = false;
        for (int k = 0; k < n; k++)
        {
            _phase[k] = Wrap01(controls.Phases[k] / TwoPi);
            bool elliptic = controls.Waveforms[k] == OperatorWaveform.Elliptic;
            _isNoise[k] = controls.Waveforms[k] == OperatorWaveform.Noise;
            double m = elliptic ? Math.Clamp((double)controls.Shapes[k], 0.0, 0.999999) : 0.0;
            _family[k] = elliptic ? controls.Morphs[k] : 0.0;
            _noisePhase[k] = _isNoise[k] ? 0.0 : controls.NoisePhases[k];
            _noiseM[k] = elliptic ? controls.NoiseMs[k] : 0.0;
            double depth = Math.Abs(_noiseM[k]);
            if (depth > 1.0e-12)
            {
                // The UGen prepares the two ends of the m excursion and interpolates their coefficients.
                _preparedLow[k] = Prepare(Math.Clamp(m - depth, 0.0, 0.999999));
                _preparedHigh[k] = Prepare(Math.Clamp(m + depth, 0.0, 0.999999));
            }

            _prepared[k] = Prepare(m);
            _anyRoughness |= Math.Abs(_noisePhase[k]) > 1.0e-15 || depth > 1.0e-12;
            _decimators[k] = new HalfbandDecimator();
        }

        // The first block ramps from the construction values.
        for (int k = 0; k < n; k++)
        {
            _freq[k] = FrequencyOf(k, controls.Frequencies);
            _amp[k] = AmplitudeOf(k, controls.Amplitudes);
            _noiseAmp[k] = NoiseAmplitudeOf(k, controls.Amplitudes);
        }

        _noise = new SharedNoiseSource(controls.NoiseCutoffHz, _internalRate);
        _noiseColor = Math.Clamp((double)controls.NoiseColor, -1.0, 1.0);
    }

    /// <inheritdoc />
    protected override void RenderBlock(IControlSource controls, Span<float> block)
    {
        int n = _operators;
        int edges = _edges;
        double internalRate = _internalRate;
        double[] phase = _phase;
        Prepared[] prepared = _prepared;
        Prepared?[] preparedLow = _preparedLow;
        Prepared?[] preparedHigh = _preparedHigh;
        double[] family = _family;
        bool[] isNoise = _isNoise;
        double[] noisePhase = _noisePhase;
        double[] noiseM = _noiseM;
        double[] prevOut = _prevOut;
        double[] next = _next;
        double[] pm = _pm;
        double[] dPhase = _dPhase;
        double[] freq = _freq;
        double[] freqInc = _freqInc;
        double[] amp = _amp;
        double[] ampInc = _ampInc;
        double[] noiseAmp = _noiseAmp;
        double[] noiseAmpInc = _noiseAmpInc;
        int[] partials = _partials;
        HalfbandDecimator[] decimators = _decimators;
        double[] mod = _mod;
        double[] modInc = _modInc;
        SharedNoiseSource noise = _noise;
        double noiseColor = _noiseColor;
        float[] frequencies = controls.Frequencies;
        float[] amplitudes = controls.Amplitudes;
        float[] amounts = controls.EdgeAmounts;
        float[] gain = controls.OutputGains;
        int[] source = controls.EdgeSources;
        int[] destination = controls.EdgeDestinations;

        // The UGen ramps frequency, amplitude and matrix linearly across the block, from the values of the previous
        // block to the ones it reads now, using each value before stepping it.
        double slope = 1.0 / block.Length;
        for (int k = 0; k < n; k++)
        {
            freqInc[k] = (FrequencyOf(k, frequencies) - freq[k]) * slope;
            ampInc[k] = (AmplitudeOf(k, amplitudes) - amp[k]) * slope;
            noiseAmpInc[k] = (NoiseAmplitudeOf(k, amplitudes) - noiseAmp[k]) * slope;
        }

        for (int e = 0; e < edges; e++)
        {
            modInc[e] = (amounts[e] - mod[e]) * slope;
        }

        for (int position = 0; position < block.Length; position++)
        {
            // The UGen derives these from its controls every host sample.
            bool noiseEnabled = _anyRoughness;
            for (int k = 0; k < n; k++)
            {
                noiseEnabled |= Math.Abs(noiseAmp[k]) > 1.0e-15;
                dPhase[k] = freq[k] / internalRate;
                int active = preparedLow[k] is { } low ? Math.Max(low.ActivePartials, preparedHigh[k]!.ActivePartials) : prepared[k].ActivePartials;
                partials[k] = Math.Min(PartialCount(freq[k], internalRate), active);
            }

            float mix = 0f;
            for (int sub = 0; sub < Oversample; sub++)
            {
                // All modulation of this internal sample comes from the outputs of the previous one.
                Array.Clear(pm);
                for (int e = 0; e < edges; e++)
                {
                    if (mod[e] != 0.0)
                    {
                        pm[destination[e]] += mod[e] * prevOut[source[e]];
                    }
                }

                // One noise sample for all operators, drawn only while some operator uses it.
                double shared = noiseEnabled ? noise.Next(noiseColor) : 0.0;
                for (int k = 0; k < n; k++)
                {
                    double readPhase = Wrap01(phase[k] + ((pm[k] + (noisePhase[k] * shared)) / TwoPi));
                    double value = noiseAmp[k] * shared;
                    if (Math.Abs(amp[k]) > 1.0e-15 && partials[k] > 0)
                    {
                        double wave;
                        if (preparedLow[k] is { } low)
                        {
                            double t = 0.5 * (Math.Clamp(noiseM[k] < 0.0 ? -shared : shared, -1.0, 1.0) + 1.0);
                            wave = RenderInterpolated(readPhase, low, preparedHigh[k]!, t, partials[k], family[k]);
                        }
                        else
                        {
                            wave = RenderElliptic(readPhase, prepared[k], partials[k], family[k]);
                        }

                        value += wave * amp[k];
                    }

                    next[k] = value;
                    phase[k] = Wrap01(phase[k] + dPhase[k]);
                }

                for (int k = 0; k < n; k++)
                {
                    if (decimators[k].Push(next[k], out double y))
                    {
                        mix += (float)y * gain[k];
                    }
                }

                (prevOut, next) = (next, prevOut);
            }

            block[position] = mix;
            for (int k = 0; k < n; k++)
            {
                freq[k] += freqInc[k];
                amp[k] += ampInc[k];
                noiseAmp[k] += noiseAmpInc[k];
            }

            for (int e = 0; e < edges; e++)
            {
                mod[e] += modInc[e];
            }
        }

        // The next block starts exactly at the values read now.
        for (int k = 0; k < n; k++)
        {
            freq[k] = FrequencyOf(k, frequencies);
            amp[k] = AmplitudeOf(k, amplitudes);
            noiseAmp[k] = NoiseAmplitudeOf(k, amplitudes);
        }

        for (int e = 0; e < edges; e++)
        {
            mod[e] = amounts[e];
        }

        // The output buffers swap every internal sample; keep the one that holds the latest outputs.
        _prevOut = prevOut;
        _next = next;
    }

    // A noise operator is a row whose amplitude drives the shared noise instead of the oscillator.
    private double FrequencyOf(int k, float[] frequencies) => _isNoise[k] ? 0.0 : frequencies[k];

    private double AmplitudeOf(int k, float[] amplitudes) => _isNoise[k] ? 0.0 : amplitudes[k];

    private double NoiseAmplitudeOf(int k, float[] amplitudes) => _isNoise[k] ? amplitudes[k] : 0.0;

    /// <summary>
    /// The shared noise of the UGen: xorshift white noise, a one-pole low-pass at the cutoff, and the colour
    /// crossfading from the low-passed noise (-1) through white (0) to the remainder above the cutoff (+1). The UGen
    /// seeds the generator from the address of the unit, so its sequence differs per synth; this one starts from the
    /// default seed of the UGen engine.
    /// </summary>
    private sealed class SharedNoiseSource
    {
        private const uint DefaultSeed = 0x6d2b79f5u;
        private readonly double _alpha;
        private uint _state = DefaultSeed;
        private double _low;

        public SharedNoiseSource(double cutoffHz, double internalRate)
        {
            double cutoff = Math.Clamp(Math.Abs(cutoffHz), 5.0, internalRate * 0.45);
            _alpha = 1.0 - Math.Exp(-TwoPi * cutoff / internalRate);
        }

        public double Next(double color)
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x != 0 ? x : DefaultSeed;
            double white = ((double)((_state >> 8) & 0x00ffffffu) / 0x007fffffu) - 1.0;
            _low += _alpha * (white - _low);
            double high = white - _low;
            double y = color < 0.0 ? white + ((_low - white) * -color)
                : color > 0.0 ? white + ((high - white) * color)
                : white;
            return Math.Clamp(y, -1.0, 1.0);
        }
    }

    /// <summary>
    /// An elliptic waveform between two prepared parameters: the normalized coefficients of both ends interpolated by
    /// <paramref name="t"/>, as the UGen does for m noise.
    /// </summary>
    private static double RenderInterpolated(double phase, Prepared low, Prepared high, double t, int numPartials, double family)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        double f = Math.Clamp(family, 0.0, 1.0);
        bool needSn = f < 1.0 - 1.0e-12;
        bool needSd = f > 1.0e-12;
        double lowSnNorm = low.SnNorm[numPartials], highSnNorm = high.SnNorm[numPartials];
        double lowSdNorm = low.SdNorm[numPartials], highSdNorm = high.SdNorm[numPartials];
        double Sn(int i)
        {
            double a = low.SnCoeff[i] * lowSnNorm;
            return a + (((high.SnCoeff[i] * highSnNorm) - a) * t);
        }

        double Sd(int i)
        {
            double a = low.SdCoeff[i] * lowSdNorm;
            return a + (((high.SdCoeff[i] * highSdNorm) - a) * t);
        }

        double s1 = FastSin01(phase);
        double sumSn = needSn ? Sn(0) * s1 : 0.0;
        double sumSd = needSd ? Sd(0) * s1 : 0.0;
        if (numPartials > 1)
        {
            double s1sq = s1 * s1;
            double cos2 = 1.0 - (2.0 * s1sq);
            double prev = s1;
            double cur = s1 * (3.0 - (4.0 * s1sq));
            if (needSn)
            {
                sumSn += Sn(1) * cur;
            }

            if (needSd)
            {
                sumSd += Sd(1) * cur;
            }

            for (int i = 2; i < numPartials; i++)
            {
                double nextValue = (2.0 * cos2 * cur) - prev;
                if (needSn)
                {
                    sumSn += Sn(i) * nextValue;
                }

                if (needSd)
                {
                    sumSd += Sd(i) * nextValue;
                }

                prev = cur;
                cur = nextValue;
            }
        }

        if (!needSd)
        {
            return sumSn;
        }

        return !needSn ? sumSd : sumSn + ((sumSd - sumSn) * f);
    }

    private static double Wrap01(double x)
    {
        x -= Math.Floor(x);
        return x < 0.0 ? x + 1.0 : x;
    }

    /// <summary>Eleventh-order sine polynomial on [0, π/2].</summary>
    private static double SinQuarter(double x)
    {
        double x2 = x * x;
        return x * (1.0 + (x2 * (-1.0 / 6.0 + (x2 * (1.0 / 120.0 + (x2 * (-1.0 / 5040.0 + (x2 * (1.0 / 362880.0 + (x2 * (-1.0 / 39916800.0)))))))))));
    }

    private static double FastSin01(double phase)
    {
        phase = Wrap01(phase);
        double qf = phase * 4.0;
        int q = (int)qf;
        double u = qf - q;
        return (q & 3) switch
        {
            0 => SinQuarter(u * HalfPi),
            1 => SinQuarter((1.0 - u) * HalfPi),
            2 => -SinQuarter(u * HalfPi),
            _ => -SinQuarter((1.0 - u) * HalfPi),
        };
    }

    private static double Agm(double a, double b)
    {
        for (int i = 0; i < 8; i++)
        {
            double an = 0.5 * (a + b);
            double bn = Math.Sqrt(a * b);
            a = an;
            b = bn;
            if (Math.Abs(a - b) <= 1.0e-14 * Math.Max(1.0, a))
            {
                break;
            }
        }

        return a;
    }

    /// <summary>The coefficient tables of an elliptic parameter, as the UGen prepares them when m changes.</summary>
    private sealed class Prepared
    {
        public double[] SnCoeff { get; } = new double[MaxPartials];

        public double[] SdCoeff { get; } = new double[MaxPartials];

        public double[] SnNorm { get; } = [.. Enumerable.Repeat(1.0, MaxPartials + 1)];

        public double[] SdNorm { get; } = [.. Enumerable.Repeat(1.0, MaxPartials + 1)];

        public int ActivePartials { get; set; } = 1;
    }

    private static Prepared Prepare(double mIn)
    {
        var p = new Prepared();
        double m = Math.Clamp(mIn, 0.0, 0.999999);
        if (m <= 1.0e-10)
        {
            p.SnCoeff[0] = 1.0;
            p.SdCoeff[0] = 1.0;
            return p;
        }

        double k = Math.Sqrt(m);
        double kPrime = Math.Sqrt(Math.Max(0.0, 1.0 - m));
        double a = Agm(1.0, kPrime);
        double b = Agm(1.0, k);
        double q = Math.Exp(-Math.PI * a / b);
        double scale = 4.0 * a / k;

        double r = Math.Sqrt(q);
        double qOdd = q;
        double q2 = q * q;
        double snQuarter = 0.0;
        double sdQuarter = 0.0;
        for (int i = 0; i < MaxPartials; i++)
        {
            double sign = (i & 1) != 0 ? -1.0 : 1.0;
            double snDenom = Math.Max(1.0e-15, 1.0 - qOdd);
            double sdDenom = Math.Max(1.0e-15, 1.0 + qOdd);
            double snC = scale * r / snDenom;
            double sdC = sign * scale * r / sdDenom;
            p.SnCoeff[i] = snC;
            p.SdCoeff[i] = sdC;
            if (Math.Max(Math.Abs(snC), Math.Abs(sdC)) > 1.0e-10)
            {
                p.ActivePartials = i + 1;
            }

            snQuarter += sign * snC;
            sdQuarter += sign * sdC;
            p.SnNorm[i + 1] = Math.Abs(snQuarter) > 1.0e-15 ? 1.0 / snQuarter : 1.0;
            p.SdNorm[i + 1] = Math.Abs(sdQuarter) > 1.0e-15 ? 1.0 / sdQuarter : 1.0;
            r *= q;
            qOdd *= q2;
        }

        return p;
    }

    /// <summary>Odd harmonics below the anti-alias ceiling of 47.5 % of the internal rate (the UGen's bandwidth 0).</summary>
    private static int PartialCount(double freq, double internalRate)
    {
        double af = Math.Abs(freq);
        if (af <= 1.0e-12)
        {
            return MaxPartials;
        }

        double limit = internalRate * 0.475;
        if (limit < af)
        {
            return 0;
        }

        int maxHarmonic = (int)Math.Floor(limit / af);
        int count = (maxHarmonic + 1) / 2;
        return Math.Max(0, Math.Min(MaxPartials, count));
    }

    private static double RenderElliptic(double phase, Prepared p, int numPartials, double family)
    {
        double f = Math.Clamp(family, 0.0, 1.0);
        bool needSn = f < 1.0 - 1.0e-12;
        bool needSd = f > 1.0e-12;
        double s1 = FastSin01(phase);
        double sumSn = needSn ? p.SnCoeff[0] * s1 : 0.0;
        double sumSd = needSd ? p.SdCoeff[0] * s1 : 0.0;
        if (numPartials > 1)
        {
            double s1sq = s1 * s1;
            double cos2 = 1.0 - (2.0 * s1sq);
            double prev = s1;
            double cur = s1 * (3.0 - (4.0 * s1sq));
            if (needSn)
            {
                sumSn += p.SnCoeff[1] * cur;
            }

            if (needSd)
            {
                sumSd += p.SdCoeff[1] * cur;
            }

            for (int i = 2; i < numPartials; i++)
            {
                double nextValue = (2.0 * cos2 * cur) - prev;
                if (needSn)
                {
                    sumSn += p.SnCoeff[i] * nextValue;
                }

                if (needSd)
                {
                    sumSd += p.SdCoeff[i] * nextValue;
                }

                prev = cur;
                cur = nextValue;
            }
        }

        double sn = needSn ? sumSn * p.SnNorm[numPartials] : 0.0;
        double sd = needSd ? sumSd * p.SdNorm[numPartials] : 0.0;
        if (!needSd)
        {
            return sn;
        }

        return !needSn ? sd : sn + ((sd - sn) * f);
    }

    /// <summary>The UGen's 31-tap half-band decimator by two.</summary>
    private sealed class HalfbandDecimator
    {
        private const int Taps = 31;
        private static readonly int[] Delay = [0, 2, 4, 6, 8, 10, 12, 14, 15, 16, 18, 20, 22, 24, 26, 28, 30];
        private static readonly double[] H =
        [
            -4.9629878624e-05, 6.4225402557e-04, -2.7344414175e-03, 8.0203240103e-03, -1.9227637930e-02, 4.1536704659e-02,
            -9.1224808140e-02, 3.1304552743e-01, 4.9998341448e-01, 3.1304552743e-01, -9.1224808140e-02, 4.1536704659e-02,
            -1.9227637930e-02, 8.0203240103e-03, -2.7344414175e-03, 6.4225402557e-04, -4.9629878624e-05,
        ];

        private readonly double[] _history = new double[Taps];
        private int _position;
        private int _phase;

        public bool Push(double x, out double y)
        {
            _history[_position] = x;
            _position = (_position + 1) % Taps;
            _phase ^= 1;
            if (_phase != 0)
            {
                y = 0.0;
                return false;
            }

            int newest = (_position + Taps - 1) % Taps;
            double acc = 0.0;
            for (int k = 0; k < Delay.Length; k++)
            {
                int index = newest - Delay[k];
                if (index < 0)
                {
                    index += Taps;
                }

                acc += _history[index] * H[k];
            }

            y = acc;
            return true;
        }
    }
}
