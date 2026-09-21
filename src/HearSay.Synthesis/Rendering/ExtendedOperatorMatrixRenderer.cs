using HearSay.Dsp;
using HearSay.Synthesis.Compilation;
using HearSay.Synthesis.Oscillators;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Synthesis.Rendering;

/// <summary>
/// Experimental renderer for profiles beyond sine operators. It runs the generic operator-matrix equation
/// <c>output_i[n] = amplitude_i[n] * waveform_i(phase_i[n] + sum_j M[i,j] * output_j[n-1])</c> in double precision
/// and keeps aliasing of non-sine waveforms down by oversampling internally. It does not imitate any backend and is
/// therefore never used for <see cref="RenderProfile.BackendExact"/> work. Immutable and thread-safe.
/// </summary>
/// <remarks>
/// Oversampling is an implementation detail of this renderer: the search, the patch model and the profiles know
/// nothing about it. Note that the one-sample delay of every edge is one sample at the internal rate.
/// </remarks>
public sealed class ExtendedOperatorMatrixRenderer : IOperatorMatrixRenderer
{
    private readonly SynthesisProfile _profile;

    /// <summary>Creates a renderer for a profile.</summary>
    /// <param name="profile">Profile; its waveforms must be implemented by <see cref="WaveformKernel"/>.</param>
    /// <param name="oversampling">Internal oversampling factor, 1, 2, 4 or 8.</param>
    /// <exception cref="ArgumentException">The profile contains a waveform or mode this renderer does not implement.</exception>
    public ExtendedOperatorMatrixRenderer(SynthesisProfile profile, int oversampling = 4)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (oversampling is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(oversampling), "Oversampling must be 1, 2, 4 or 8.");
        }

        foreach (OperatorWaveform waveform in profile.Capabilities.SupportedWaveforms)
        {
            if (!WaveformKernel.IsImplemented(waveform))
            {
                throw new ArgumentException($"Waveform {waveform} is not implemented by the extended renderer.", nameof(profile));
            }
        }

        foreach (OperatorMode mode in profile.Capabilities.SupportedModes)
        {
            if (mode != OperatorMode.PhaseModulation)
            {
                throw new ArgumentException($"Operator mode {mode} is not implemented by the extended renderer.", nameof(profile));
            }
        }

        _profile = profile;
        Oversampling = oversampling;
    }

    /// <summary>Internal oversampling factor.</summary>
    public int Oversampling { get; }

    /// <inheritdoc />
    public SynthesisCapabilities Capabilities => _profile.Capabilities;

    /// <inheritdoc />
    public RenderProfile RenderProfile => RenderProfile.ExperimentalHighQuality;

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

        RenderRequest internalRequest = request with { SampleRate = request.SampleRate * Oversampling };
        CompiledOperatorMatrixPatch compiled = PatchCompiler.Compile(patch, internalRequest, _profile.ControlBlockSize * Oversampling);
        float[] rendered = RenderCompiled(compiled);
        if (Oversampling > 1)
        {
            rendered = Resampler.Resample(rendered, internalRequest.SampleRate, request.SampleRate);
        }

        if (rendered.Length != request.TotalSamples)
        {
            Array.Resize(ref rendered, request.TotalSamples);
        }

        return new RenderedAudio(rendered, request.SampleRate);
    }

    private static float[] RenderCompiled(CompiledOperatorMatrixPatch compiled)
    {
        int n = compiled.OperatorCount;
        int edges = compiled.EdgeCount;
        int blockSize = compiled.BlockSize;
        var output = new float[compiled.SampleCount];
        if (n == 0)
        {
            return output;
        }

        double radiansPerHz = 2.0 * Math.PI / compiled.SampleRate;
        var phase = new double[n];
        var freq = new double[n];
        var freqInc = new double[n];
        var amp = new double[n];
        var ampInc = new double[n];
        var previous = new double[n];
        var deviation = new double[n];
        var mod = new double[edges];
        var modInc = new double[edges];
        for (int k = 0; k < n; k++)
        {
            freq[k] = compiled.Frequencies[k][0];
            amp[k] = compiled.Amplitudes[k][0];
        }

        for (int e = 0; e < edges; e++)
        {
            mod[e] = compiled.EdgeAmounts[e][0];
        }

        int position = 0;
        for (int block = 0; block < compiled.BlockCount; block++)
        {
            int step = block + 1;
            for (int k = 0; k < n; k++)
            {
                float[] f = compiled.Frequencies[k];
                float[] a = compiled.Amplitudes[k];
                freqInc[k] = ((f.Length == 1 ? f[0] : f[step]) - freq[k]) / blockSize;
                ampInc[k] = ((a.Length == 1 ? a[0] : a[step]) - amp[k]) / blockSize;
            }

            for (int e = 0; e < edges; e++)
            {
                float[] m = compiled.EdgeAmounts[e];
                modInc[e] = ((m.Length == 1 ? m[0] : m[step]) - mod[e]) / blockSize;
            }

            int end = Math.Min(position + blockSize, output.Length);
            for (; position < end; position++)
            {
                for (int k = 0; k < n; k++)
                {
                    deviation[k] = compiled.Phases[k];
                }

                // Every edge reads the output of the previous sample, so loops need no special treatment.
                for (int e = 0; e < edges; e++)
                {
                    deviation[compiled.EdgeDestinations[e]] += mod[e] * previous[compiled.EdgeSources[e]];
                }

                double mix = 0;
                for (int k = 0; k < n; k++)
                {
                    previous[k] = amp[k] * WaveformKernel.Evaluate(compiled.Waveforms[k], phase[k] + deviation[k]);
                    mix += previous[k] * compiled.OutputGains[k];
                    phase[k] += freq[k] * radiansPerHz;
                    if (phase[k] > Math.PI * 2048)
                    {
                        phase[k] -= Math.PI * 2048;
                    }

                    freq[k] += freqInc[k];
                    amp[k] += ampInc[k];
                }

                for (int e = 0; e < edges; e++)
                {
                    mod[e] += modInc[e];
                }

                output[position] = (float)mix;
            }
        }

        return output;
    }
}
