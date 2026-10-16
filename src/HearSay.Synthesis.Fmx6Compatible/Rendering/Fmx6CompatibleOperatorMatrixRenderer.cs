using HearSay.Synthesis.Compilation;
using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Fmx6Compatible.Rendering;

/// <summary>
/// Offline reference renderer that reproduces the discrete-time behaviour of the SuperCollider <c>FMX6</c> UGen
/// (https://github.com/shimpe/fmx6), so that a patch optimized here sounds the same when its exported SynthDef is
/// played.
/// </summary>
/// <remarks>
/// <para>What is reproduced, all taken from the FMX6 source:</para>
/// <list type="bullet">
/// <item>Previous-sample modulation over the full matrix, as in FM7: every edge has a delay of exactly one sample.</item>
/// <item>
/// Band-limited waveforms as additive Fourier series (triangle, saw, square, pulse with a duty cycle), with the
/// number of harmonics capped per sample so that the highest one stays below 45 % of the sample rate at the
/// instantaneous frequency, which includes the sample-to-sample change of the phase modulation.
/// </item>
/// <item>White noise and sample-and-hold noise from an xorshift generator seeded by operator slot; the held value changes when the base phase wraps.</item>
/// <item>Filtered noise: the white noise through a TPT state-variable filter in double precision, cutoff at the operator frequency.</item>
/// <item>Double-precision phase in cycles, single-precision frequency, phase offset, amplitude and matrix, all ramped linearly across each control block.</item>
/// <item>No warm-up sample at construction: the first output sample is at phase zero. FM7 differs here.</item>
/// <item>Operator filters: the TPT state-variable filters of <see cref="OperatorFilterKernel"/> between the waveform and the amplitude.</item>
/// <item>No oversampling: the aliasing of deep modulation is part of the sound of the backend.</item>
/// </list>
/// <para>Instances are immutable and thread-safe.</para>
/// </remarks>
public sealed class Fmx6CompatibleOperatorMatrixRenderer : IOperatorMatrixRenderer
{
    private const double TwoPi = 2.0 * Math.PI;
    private const float InvTwoPi = (float)(1.0 / (2.0 * Math.PI));

    private readonly SynthesisProfile _profile;

    /// <summary>Creates a renderer for the <see cref="SynthesisProfiles.SuperColliderFmx6"/> profile.</summary>
    public Fmx6CompatibleOperatorMatrixRenderer()
        : this(SynthesisProfiles.SuperColliderFmx6)
    {
    }

    /// <summary>Creates a renderer for a profile with FMX6 semantics.</summary>
    /// <param name="profile">Profile; its capabilities must not exceed those of FMX6.</param>
    /// <exception cref="ArgumentException">The profile asks for more than FMX6 can play.</exception>
    public Fmx6CompatibleOperatorMatrixRenderer(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFmx6.Capabilities))
        {
            throw new ArgumentException($"Profile '{profile.Name}' needs capabilities that the FMX6-compatible renderer does not have.", nameof(profile));
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

        float sampleRate = compiled.SampleRate;
        float sampleDur = (float)(1.0 / compiled.SampleRate);
        float slopeFactor = 1f / blockSize;

        var phaseCycles = new double[n];
        var freq = new float[n];
        var freqInc = new float[n];
        var phaseOffset = new float[n];
        var amp = new float[n];
        var ampInc = new float[n];
        var lastPhaseMod = new float[n];
        var mem = new float[n];
        var sampleHold = new float[n];
        var rng = new uint[n];
        var filterState1 = new double[n];
        var filterState2 = new double[n];
        var matrixPhaseMod = new float[n];
        var mod = new float[edges];
        var modInc = new float[edges];
        int[] source = compiled.EdgeSources;
        int[] destination = compiled.EdgeDestinations;
        float[][] frequencies = compiled.Frequencies;
        float[][] amplitudes = compiled.Amplitudes;
        float[][] amounts = compiled.EdgeAmounts;
        var wave = new int[n];
        var harmonicLimit = new int[n];
        float[] shape = compiled.Shapes;
        CompiledFilters? filters = compiled.Filters;
        var filterFreq = new float[n];
        var filterFreqInc = new float[n];
        var filterOct = new float[n];
        var filterOctInc = new float[n];
        var filterA = new SvfState[n];
        var filterB = new SvfState[n];
        if (filters is not null)
        {
            for (int k = 0; k < n; k++)
            {
                filterFreq[k] = filters.BaseHz[k];
                filterOct[k] = filters.Octaves[k][0];
            }
        }

        for (int k = 0; k < n; k++)
        {
            freq[k] = frequencies[k][0];
            amp[k] = amplitudes[k][0];
            phaseOffset[k] = compiled.Phases[k];
            lastPhaseMod[k] = phaseOffset[k];
            wave[k] = WaveIndex(compiled.Waveforms[k]);
            harmonicLimit[k] = Math.Clamp(compiled.HarmonicLimits[k], 1, 128);

            // The generator is seeded by the operator slot in the UGen, which is the index in the patch.
            rng[k] = 0x9E3779B9u ^ unchecked(0x85EBCA6Bu * (uint)(compiled.OperatorIndices[k] + 1));
            sampleHold[k] = RandBipolar(ref rng[k]);
        }

        for (int e = 0; e < edges; e++)
        {
            mod[e] = amounts[e][0];
        }

        int position = 0;
        for (int block = 0; block < compiled.BlockCount; block++)
        {
            int step = block + 1;
            for (int k = 0; k < n; k++)
            {
                float[] f = frequencies[k];
                float[] a = amplitudes[k];
                freqInc[k] = ((f.Length == 1 ? f[0] : f[step]) - freq[k]) * slopeFactor;
                ampInc[k] = ((a.Length == 1 ? a[0] : a[step]) - amp[k]) * slopeFactor;
                if (filters is not null)
                {
                    float[] o = filters.Octaves[k];
                    filterFreqInc[k] = (filters.BaseHz[k] - filterFreq[k]) * slopeFactor;
                    filterOctInc[k] = ((o.Length == 1 ? o[0] : o[step]) - filterOct[k]) * slopeFactor;
                }
            }

            for (int e = 0; e < edges; e++)
            {
                float[] m = amounts[e];
                modInc[e] = ((m.Length == 1 ? m[0] : m[step]) - mod[e]) * slopeFactor;
            }

            int end = Math.Min(position + blockSize, output.Length);
            for (; position < end; position++)
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

                    float shaped = filters is null || filters.Types[k] == OperatorFilterKernel.Off
                        ? osc
                        : (float)OperatorFilterKernel.Process(
                            filters.Types[k],
                            osc,
                            filterFreq[k],
                            filterOct[k],
                            filters.Q[k],
                            filters.FormantRatios[k],
                            filters.FormantLevels[k],
                            filters.GainsDb[k],
                            sampleRate,
                            ref filterA[k],
                            ref filterB[k]);
                    float y = amp[k] * shaped;
                    mix += y * compiled.OutputGains[k];
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
                    filterFreq[k] += filterFreqInc[k];
                    filterOct[k] += filterOctInc[k];
                }

                for (int e = 0; e < edges; e++)
                {
                    mod[e] += modInc[e];
                }

                output[position] = mix;
            }
        }

        return output;
    }

    /// <summary>Waveform selector of the UGen.</summary>
    /// <param name="waveform">Canonical waveform.</param>
    /// <exception cref="NotSupportedException">FMX6 has no such waveform.</exception>
    public static int WaveIndex(OperatorWaveform waveform) =>
        waveform switch
        {
            OperatorWaveform.Sine => 0,
            OperatorWaveform.Triangle => 1,
            OperatorWaveform.Saw => 2,
            OperatorWaveform.Square => 3,
            OperatorWaveform.Pulse => 4,
            OperatorWaveform.SampleHoldNoise => 5,
            OperatorWaveform.Noise => 6,
            OperatorWaveform.FilteredNoise => 7,
            _ => throw new NotSupportedException($"FMX6 has no {waveform} waveform."),
        };

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
