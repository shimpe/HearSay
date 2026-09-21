using HearSay.Synthesis.Compilation;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Fm7Compatible.Rendering;

/// <summary>
/// Offline reference renderer that reproduces the discrete-time behaviour of the SuperCollider <c>FM7</c> UGen, so
/// that a patch optimized here sounds the same when its exported SynthDef is played.
/// </summary>
/// <remarks>
/// <para>What is reproduced, all taken from the FM7 source:</para>
/// <list type="bullet">
/// <item>
/// Previous-sample modulation. In every sample all phase deviations are computed first, from the operator outputs of
/// the previous sample, and only then are the six outputs updated. Every edge therefore has a delay of exactly one
/// sample, which is what makes self-feedback and feedback loops through several operators well defined.
/// </item>
/// <item>The output of an operator includes its amplitude, so an amplitude envelope also shapes the modulation it sends.</item>
/// <item>32-bit fixed-point phase with truncated increments and a linearly interpolated 8192-point sine table.</item>
/// <item>Frequency, amplitude and matrix inputs are control-rate signals, ramped linearly across each control block.</item>
/// <item>The unit computes one sample when it is constructed; the audible output starts one sample into the cycle.</item>
/// <item>No oversampling and no band-limiting: aliasing is part of the sound of the backend.</item>
/// </list>
/// <para>Instances are immutable and thread-safe.</para>
/// </remarks>
public sealed class Fm7CompatibleOperatorMatrixRenderer : IOperatorMatrixRenderer
{
    private const int TableSize = 8192;
    private const int TableMask = TableSize - 1;

    // Wavetable format of the server: value = First + Second * (1 + fraction).
    private static readonly float[] First = new float[TableSize];
    private static readonly float[] Second = new float[TableSize];

    private readonly SynthesisProfile _profile;

    static Fm7CompatibleOperatorMatrixRenderer()
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

    /// <summary>Creates a renderer for the <see cref="SynthesisProfiles.SuperColliderFm7"/> profile.</summary>
    public Fm7CompatibleOperatorMatrixRenderer()
        : this(SynthesisProfiles.SuperColliderFm7)
    {
    }

    /// <summary>Creates a renderer for a profile with FM7 semantics and, for example, another control block size.</summary>
    /// <param name="profile">Profile; its capabilities must not exceed those of FM7.</param>
    /// <exception cref="ArgumentException">The profile asks for more than FM7 can play.</exception>
    public Fm7CompatibleOperatorMatrixRenderer(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFm7.Capabilities))
        {
            throw new ArgumentException($"Profile '{profile.Name}' needs capabilities that the FM7-compatible renderer does not have.", nameof(profile));
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
    /// <param name="compiled">Compiled patch; all operators must be sine phase-modulation operators.</param>
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

        float cpsToInc = (float)(TableSize * (1.0 / compiled.SampleRate) * 65536.0);
        float radToInc = (float)(TableSize * (1.0 / (2.0 * Math.PI)) * 65536.0);
        float slopeFactor = 1f / blockSize;

        var phase = new int[n];
        var freq = new float[n];
        var freqInc = new float[n];
        var amp = new float[n];
        var ampInc = new float[n];
        var mem = new float[n];
        var deviation = new float[n];
        var mod = new float[edges];
        var modInc = new float[edges];
        float[] phaseOffset = compiled.Phases;
        float[] gain = compiled.OutputGains;
        int[] source = compiled.EdgeSources;
        int[] destination = compiled.EdgeDestinations;
        float[][] frequencies = compiled.Frequencies;
        float[][] amplitudes = compiled.Amplitudes;
        float[][] amounts = compiled.EdgeAmounts;

        for (int k = 0; k < n; k++)
        {
            freq[k] = frequencies[k][0];
            amp[k] = amplitudes[k][0];
        }

        for (int e = 0; e < edges; e++)
        {
            mod[e] = amounts[e][0];
        }

        // The unit computes one sample while it is constructed. Its output is discarded, its state is kept.
        Step(n, edges, phase, freq, amp, mem, deviation, mod, phaseOffset, source, destination, cpsToInc, radToInc);

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
            }

            for (int e = 0; e < edges; e++)
            {
                float[] m = amounts[e];
                modInc[e] = ((m.Length == 1 ? m[0] : m[step]) - mod[e]) * slopeFactor;
            }

            int end = Math.Min(position + blockSize, output.Length);
            for (; position < end; position++)
            {
                Step(n, edges, phase, freq, amp, mem, deviation, mod, phaseOffset, source, destination, cpsToInc, radToInc);
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

                output[position] = mix;
            }
        }

        return output;
    }

    /// <summary>One sample: all phase deviations from the previous outputs first, then all operators.</summary>
    private static void Step(
        int n,
        int edges,
        int[] phase,
        float[] freq,
        float[] amp,
        float[] mem,
        float[] deviation,
        float[] mod,
        float[] phaseOffset,
        int[] source,
        int[] destination,
        float cpsToInc,
        float radToInc)
    {
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

    /// <summary>Float to 32-bit conversion with the out-of-range result of the x86 conversion the backend compiles to.</summary>
    private static int ToFixed(float value) =>
        value >= 2147483648f || value < -2147483648f || float.IsNaN(value) ? int.MinValue : (int)value;
}
