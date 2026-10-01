using HearSay.Synthesis.Compilation;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Nome6Compatible.Rendering;

/// <summary>
/// Offline reference renderer that reproduces the discrete-time behaviour of the SuperCollider <c>Nome6</c> UGen, so
/// that a patch optimized here sounds the same when its exported SynthDef is played.
/// </summary>
/// <remarks>
/// <para>What is reproduced, all taken from the Nome6 source (<c>Nome6DSP.hpp</c>):</para>
/// <list type="bullet">
/// <item>
/// Jacobi elliptic oscillators from their odd-harmonic Fourier series in the nome q, at most 24 terms, cut where the
/// harmonics would pass 47.5 % of the internal sample rate and renormalized to a peak of exactly one at a quarter
/// cycle; sn and the peak-normalized sd crossfaded by the morph. m = 0 is a sine.
/// </item>
/// <item>The sine of the series from the UGen's eleventh-order polynomial with quadrant reduction, and the odd harmonics from a recurrence.</item>
/// <item>
/// The whole matrix at twice the host rate: every edge has a delay of one internal sample, and the six outputs are
/// decimated by the UGen's 31-tap half-band filter.
/// </item>
/// <item>Double-precision phase in cycles; frequency, amplitude and matrix held for each control block at the value the block reads, not ramped as in FM7.</item>
/// <item>No warm-up sample: the first internal sample is at the initial phase.</item>
/// <item>
/// One noise source for all operators at the internal rate, drawn only while an operator uses it: xorshift white noise
/// through a one-pole crossover, coloured by the patch. A noise operator is a row whose amplitude drives that noise
/// instead of its oscillator; phase noise adds to the phase modulation; m noise interpolates the normalized
/// coefficients between m − depth and m + depth. The UGen seeds its generator from the address of the unit, so noise
/// matches the UGen in level and colour, never sample by sample.
/// </item>
/// </list>
/// <para>Instances are immutable and thread-safe.</para>
/// </remarks>
public sealed class Nome6CompatibleOperatorMatrixRenderer : IOperatorMatrixRenderer
{
    /// <summary>Oversampling factor of the exported UGen and of this renderer.</summary>
    public const int Oversample = 2;

    private const int MaxPartials = 24;
    private const double TwoPi = 2.0 * Math.PI;
    private const double HalfPi = 0.5 * Math.PI;

    private readonly SynthesisProfile _profile;

    /// <summary>Creates a renderer for the <see cref="SynthesisProfiles.SuperColliderNome6"/> profile.</summary>
    public Nome6CompatibleOperatorMatrixRenderer()
        : this(SynthesisProfiles.SuperColliderNome6)
    {
    }

    /// <summary>Creates a renderer for a profile with Nome6 semantics.</summary>
    /// <param name="profile">Profile; its capabilities must not exceed those of Nome6.</param>
    /// <exception cref="ArgumentException">The profile asks for more than Nome6 can play.</exception>
    public Nome6CompatibleOperatorMatrixRenderer(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderNome6.Capabilities))
        {
            throw new ArgumentException($"Profile '{profile.Name}' needs capabilities that the Nome6-compatible renderer does not have.", nameof(profile));
        }

        _profile = profile;
    }

    /// <inheritdoc />
    public SynthesisCapabilities Capabilities => _profile.Capabilities;

    /// <inheritdoc />
    public RenderProfile RenderProfile => RenderProfile.BackendExact;

    /// <inheritdoc />
    public RenderedAudio Render(OperatorMatrixPatch patch, RenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(request);
        PatchCompatibilityResult compatibility = PatchCompatibilityValidator.Instance.Validate(patch, _profile.Capabilities);
        if (!compatibility.IsCompatible)
        {
            throw new PatchNotSupportedException(_profile.Name, compatibility);
        }

        CompiledOperatorMatrixPatch compiled = PatchCompiler.Compile(patch, request, _profile.ControlBlockSize);
        return new RenderedAudio(Render(compiled), request.SampleRate);
    }

    /// <summary>The elliptic parameter m an operator gets in the UGen: the shape of an elliptic operator, zero for a sine.</summary>
    /// <param name="op">Operator.</param>
    public static double ParameterOf(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Waveform == OperatorWaveform.Elliptic ? op.Shape : 0.0;
    }

    /// <summary>The sn-to-sd morph an operator gets in the UGen: the morph of an elliptic operator, zero for a sine.</summary>
    /// <param name="op">Operator.</param>
    public static double FamilyOf(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Waveform == OperatorWaveform.Elliptic ? op.Morph : 0.0;
    }

    /// <summary>The phase noise an operator gets in the UGen: its phase roughness, zero for a noise operator.</summary>
    /// <param name="op">Operator.</param>
    public static double NoisePhaseOf(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Waveform == OperatorWaveform.Noise ? 0.0 : op.NoisePhase;
    }

    /// <summary>The m noise an operator gets in the UGen: the roughness of an elliptic operator, zero for the others.</summary>
    /// <param name="op">Operator.</param>
    public static double NoiseMOf(OperatorPatch op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Waveform == OperatorWaveform.Elliptic ? op.NoiseM : 0.0;
    }

    /// <summary>Renders an already compiled patch.</summary>
    /// <param name="compiled">Compiled patch.</param>
    public static float[] Render(CompiledOperatorMatrixPatch compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        int n = compiled.OperatorCount;
        int edges = compiled.EdgeCount;
        int blockSize = compiled.BlockSize;
        var output = new float[compiled.SampleCount];
        if (n == 0 || output.Length == 0)
        {
            return output;
        }

        double internalRate = (double)compiled.SampleRate * Oversample;
        var phase = new double[n];
        var prepared = new Prepared[n];
        var preparedLow = new Prepared?[n];
        var preparedHigh = new Prepared?[n];
        var family = new double[n];
        var isNoise = new bool[n];
        var noisePhase = new double[n];
        var noiseM = new double[n];
        var prevOut = new double[n];
        var next = new double[n];
        var pm = new double[n];
        var dPhase = new double[n];
        var amp = new double[n];
        var noiseAmp = new double[n];
        var partials = new int[n];
        var decimators = new HalfbandDecimator[n];
        var mod = new double[edges];
        int[] source = compiled.EdgeSources;
        int[] destination = compiled.EdgeDestinations;
        bool anyRoughness = false;
        for (int k = 0; k < n; k++)
        {
            phase[k] = Wrap01(compiled.Phases[k] / TwoPi);
            bool elliptic = compiled.Waveforms[k] == OperatorWaveform.Elliptic;
            isNoise[k] = compiled.Waveforms[k] == OperatorWaveform.Noise;
            double m = elliptic ? Math.Clamp((double)compiled.Shapes[k], 0.0, 0.999999) : 0.0;
            family[k] = elliptic ? compiled.Morphs[k] : 0.0;
            noisePhase[k] = isNoise[k] ? 0.0 : compiled.NoisePhases[k];
            noiseM[k] = elliptic ? compiled.NoiseMs[k] : 0.0;
            double depth = Math.Abs(noiseM[k]);
            if (depth > 1.0e-12)
            {
                // The UGen prepares the two ends of the m excursion and interpolates their coefficients.
                preparedLow[k] = Prepare(Math.Clamp(m - depth, 0.0, 0.999999));
                preparedHigh[k] = Prepare(Math.Clamp(m + depth, 0.0, 0.999999));
            }

            prepared[k] = Prepare(m);
            anyRoughness |= Math.Abs(noisePhase[k]) > 1.0e-15 || depth > 1.0e-12;
            decimators[k] = new HalfbandDecimator();
        }

        var noise = new SharedNoiseSource(compiled.NoiseCutoffHz, internalRate);
        double noiseColor = Math.Clamp((double)compiled.NoiseColor, -1.0, 1.0);
        int position = 0;
        for (int block = 0; block < compiled.BlockCount; block++)
        {
            // The UGen reads its control-rate inputs once, at the start of the block: the value of block b.
            int step = block + 1;
            bool noiseEnabled = anyRoughness;
            for (int k = 0; k < n; k++)
            {
                float[] f = compiled.Frequencies[k];
                float[] a = compiled.Amplitudes[k];
                double freq = isNoise[k] ? 0.0 : f.Length == 1 ? f[0] : f[step];
                double level = a.Length == 1 ? a[0] : a[step];

                // A noise operator is a row whose amplitude drives the shared noise instead of the oscillator.
                amp[k] = isNoise[k] ? 0.0 : level;
                noiseAmp[k] = isNoise[k] ? level : 0.0;
                noiseEnabled |= Math.Abs(noiseAmp[k]) > 1.0e-15;
                dPhase[k] = freq / internalRate;
                int active = preparedLow[k] is { } low ? Math.Max(low.ActivePartials, preparedHigh[k]!.ActivePartials) : prepared[k].ActivePartials;
                partials[k] = Math.Min(PartialCount(freq, internalRate), active);
            }

            for (int e = 0; e < edges; e++)
            {
                float[] m = compiled.EdgeAmounts[e];
                mod[e] = m.Length == 1 ? m[0] : m[step];
            }

            int end = Math.Min(position + blockSize, output.Length);
            for (; position < end; position++)
            {
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
                            mix += (float)y * compiled.OutputGains[k];
                        }
                    }

                    (prevOut, next) = (next, prevOut);
                }

                output[position] = mix;
            }
        }

        return output;
    }

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
