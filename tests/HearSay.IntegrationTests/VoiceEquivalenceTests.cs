using System.Diagnostics;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Nome6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.IntegrationTests;

/// <summary>A voice held for a while and then released plays exactly the offline note of that duration.</summary>
public sealed class VoiceEquivalenceTests(ITestOutputHelper output)
{
    private const int BlockSize = 64;
    private const int SampleRate = 48000;

    // An odd chunk size, so that the chunks after the release cross control-block boundaries.
    private const int Chunk = 37;

    private static readonly Dictionary<string, OperatorMatrixPatch> Fm7Patches = new()
    {
        ["electric-piano"] = ReferencePatches.Find("electric-piano")!.Patch,
        ["bell"] = ReferencePatches.Find("bell")!.Patch,
        ["brass-like"] = ReferencePatches.Find("brass-like")!.Patch,
        ["bell-pitch-envelope-vibrato"] = ReferencePatches.Find("bell")!.Patch with
        {
            Pitch = new PitchModel(
                new OperatorEnvelope([new EnvelopePoint(0, -80), new EnvelopePoint(0.04, 15), new EnvelopePoint(0.12, 0)], null, 3),
                new LfoSettings(5.5, 14, 0.3, 0.4)),
        },

        // A sustaining envelope that starts with a step: its value in block 0 depends on the gate of block 0, so a
        // voice released before its first block must be constructed with the gate closed.
        ["brass-like-step-pitch-envelope"] = ReferencePatches.Find("brass-like")!.Patch with
        {
            Pitch = new PitchModel(new OperatorEnvelope(
                [new EnvelopePoint(0, 0), new EnvelopePoint(0.05, 40, EnvelopeCurve.Step), new EnvelopePoint(0.1, 10), new EnvelopePoint(0.2, 0)],
                SustainPoint: 2,
                ReleasePoint: 3)),
        },
    };

    public static TheoryData<string, int> Fm7Cases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (string name in Fm7Patches.Keys)
            {
                foreach (int heldBlocks in new[] { 1, 20, 300 })
                {
                    data.Add(name, heldBlocks);
                }
            }

            return data;
        }
    }

    private static readonly Dictionary<string, OperatorMatrixPatch> Fmx6Patches = BuildFmx6Patches();

    public static TheoryData<string, int> Fmx6Cases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (string name in Fmx6Patches.Keys)
            {
                foreach (int heldBlocks in new[] { 0, 20, 300 })
                {
                    data.Add(name, heldBlocks);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fmx6Cases))]
    public void Fmx6Voice_HeldThenReleased_IsTheOfflineNote(string name, int heldBlocks) =>
        AssertSameAsOffline(new Fmx6CompatibleOperatorMatrixRenderer(), Fmx6Patches[name], heldBlocks);

    [Theory]
    [InlineData("fm7", "electric-piano")]
    [InlineData("fmx6", "saw-filter-sweep-up")]
    [InlineData("fmx6", "filtered-noise")]
    [InlineData("nome6", "rough-elliptic-coloured-noise")]
    public void Voice_ReleasedInTheMiddleOfAControlBlock_IsTheOfflineNote(string backend, string name)
    {
        (IOperatorMatrixRenderer renderer, OperatorMatrixPatch patch) = backend switch
        {
            "fm7" => ((IOperatorMatrixRenderer)new Fm7CompatibleOperatorMatrixRenderer(), Fm7Patches[name]),
            "fmx6" => (new Fmx6CompatibleOperatorMatrixRenderer(), Fmx6Patches[name]),
            _ => (new Nome6CompatibleOperatorMatrixRenderer(), Nome6Patches[name]),
        };
        AssertSameAsOffline(renderer, patch, heldBlocks: 20, releaseEarlySamples: 10);
    }

    private static readonly Dictionary<string, OperatorMatrixPatch> Nome6Patches = BuildNome6Patches();

    public static TheoryData<string, int> Nome6Cases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (string name in Nome6Patches.Keys)
            {
                foreach (int heldBlocks in new[] { 0, 20, 300 })
                {
                    data.Add(name, heldBlocks);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Nome6Cases))]
    public void Nome6Voice_HeldThenReleased_IsTheOfflineNote(string name, int heldBlocks) =>
        AssertSameAsOffline(new Nome6CompatibleOperatorMatrixRenderer(), Nome6Patches[name], heldBlocks);

    /// <summary>For the record, not a limit: how long one second of a six-operator Nome6 voice takes to render.</summary>
    [Fact]
    public void Nome6Voice_SixOperators_TimesOneSecond()
    {
        // A stack of six elliptic operators, every one roughened in phase and in m: the dearest path of the kernel.
        OperatorEnvelope adsr = OperatorEnvelope.Adsr(0.01, 0.3, 0.6, 0.4);
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6) with { Noise = new SharedNoise(-0.4, 2000) };
        for (int k = 0; k < 6; k++)
        {
            patch = patch.WithOperator(k, OperatorPatch.Sine(OperatorFrequency.AtRatio(k + 1), adsr) with
            {
                Waveform = OperatorWaveform.Elliptic,
                Shape = 0.99,
                Morph = 0.2 * (k % 3),
                NoisePhase = 0.1,
                NoiseM = 0.05,
            });
            if (k > 0)
            {
                patch = patch.WithEdge(new ModulationEdge(k, k - 1, true, 0.8));
            }
        }

        patch = patch.WithOutputLevel(0, 0.8);
        IVoiceRenderer renderer = Assert.IsAssignableFrom<IVoiceRenderer>(new Nome6CompatibleOperatorMatrixRenderer());
        var request = new VoiceRequest(220.0, 57, 0.8, SampleRate);
        var block = new float[BlockSize];

        // Warm up the JIT on a voice of its own.
        IOperatorMatrixVoice warmUp = renderer.StartVoice(patch, request);
        for (int i = 0; i < SampleRate / BlockSize / 10; i++)
        {
            warmUp.Render(block);
        }

        IOperatorMatrixVoice voice = renderer.StartVoice(patch, request);
        var second = new float[SampleRate];
        var watch = Stopwatch.StartNew();
        for (int position = 0; position < second.Length; position += BlockSize)
        {
            voice.Render(second.AsSpan(position, BlockSize));
        }

        watch.Stop();
        output.WriteLine($"One second of a six-operator Nome6 voice at {SampleRate} Hz: {watch.Elapsed.TotalMilliseconds:F1} ms.");
        Assert.Contains(second, static s => s != 0f);
    }

    private static Dictionary<string, OperatorMatrixPatch> BuildNome6Patches()
    {
        OperatorEnvelope adsr = OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.3);
        OperatorPatch Elliptic(double ratio, double m, double morph, OperatorEnvelope envelope) =>
            OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), envelope) with { Waveform = OperatorWaveform.Elliptic, Shape = m, Morph = morph };

        return new Dictionary<string, OperatorMatrixPatch>
        {
            // A percussive envelope: no sustain point, so the release changes nothing.
            ["one-shot-elliptic"] = OperatorMatrixPatch.Silent(6)
                .WithOperator(0, Elliptic(1, 0.99, 0.25, new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.005, 1), new EnvelopePoint(0.4, 0)], SustainPoint: null, ReleasePoint: 3)))
                .WithOutputLevel(0, 0.8),
            ["elliptic-stack"] = OperatorMatrixPatch.Silent(6)
                .WithOperator(0, Elliptic(1, 0.99, 0.25, adsr))
                .WithOperator(1, Elliptic(2, 0.9999, 1.0, OperatorEnvelope.Adsr(0.02, 0.3, 0.3, 0.4)))
                .WithOperator(2, Elliptic(3.5, 0.9, 0.0, adsr))
                .WithOutputLevel(0, 0.8)
                .WithEdge(new ModulationEdge(1, 0, true, 1.2))
                .WithEdge(new ModulationEdge(2, 1, true, 0.7)),
            ["rough-elliptic-coloured-noise"] = OperatorMatrixPatch.Silent(6)
                .WithOperator(0, Elliptic(1, 0.99, 0.5, adsr) with { NoisePhase = 0.4, NoiseM = -0.2 })
                .WithOperator(1, Elliptic(2, 0.95, 0.0, adsr) with { NoiseM = 0.15 })
                .WithOutputLevel(0, 0.8)
                .WithEdge(new ModulationEdge(1, 0, true, 0.6))
                with { Noise = new SharedNoise(-0.6, 600) },
            ["noise-operator"] = OperatorMatrixPatch.Silent(6)
                .WithOperator(0, Elliptic(1, 0.99, 0.0, adsr))
                .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtFixed(90), OperatorEnvelope.Adsr(0.005, 0.1, 0.2, 0.2)) with { Waveform = OperatorWaveform.Noise })
                .WithOutputLevel(0, 0.6)
                .WithOutputLevel(1, 0.3)
                .WithEdge(new ModulationEdge(1, 0, true, 0.5))
                with { Noise = new SharedNoise(0.3, 3000) },
        };
    }

    private static Dictionary<string, OperatorMatrixPatch> BuildFmx6Patches()
    {
        OperatorEnvelope adsr = OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.3);
        OperatorPatch Wave(OperatorWaveform waveform, double ratio, OperatorEnvelope envelope) =>
            OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), envelope) with { Waveform = waveform, HarmonicLimit = 64 };

        OperatorMatrixPatch Sweep(double octaves) =>
            OperatorMatrixPatch.Silent(6)
                .WithOperator(0, Wave(OperatorWaveform.Saw, 1, adsr) with
                {
                    Filter = OperatorFilter.LowPass(2, 4, poles: 4) with { EnvelopeOctaves = octaves, Envelope = OperatorEnvelope.Adsr(0.05, 0.3, 0.4, 0.4) },
                })
                .WithOutputLevel(0, 0.8);

        return new Dictionary<string, OperatorMatrixPatch>
        {
            ["saw-filter-sweep-up"] = Sweep(3),
            // A percussive envelope: no sustain point, so the release changes nothing.
            ["one-shot-saw"] = OperatorMatrixPatch.Silent(6)
                .WithOperator(0, Wave(OperatorWaveform.Saw, 1, new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.005, 1), new EnvelopePoint(0.4, 0)], SustainPoint: null, ReleasePoint: 3)))
                .WithOutputLevel(0, 0.8),
            ["saw-filter-sweep-down"] = Sweep(-3),
            ["pulse-edge-amount-envelope"] = OperatorMatrixPatch.Silent(6)
                .WithOperator(0, Wave(OperatorWaveform.Sine, 1, adsr))
                .WithOperator(1, Wave(OperatorWaveform.Pulse, 2, adsr) with { Shape = 0.25 })
                .WithOutputLevel(0, 0.8)
                .WithEdge(new ModulationEdge(1, 0, true, 1.5, OperatorEnvelope.Adsr(0.02, 0.3, 0.3, 0.4))),
            ["filtered-noise"] = OperatorMatrixPatch.Silent(6)
                .WithOperator(0, Wave(OperatorWaveform.Sine, 1, adsr))
                .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtFixed(3000), adsr) with { Waveform = OperatorWaveform.FilteredNoise, Shape = 0.5 })
                .WithOutputLevel(0, 0.5)
                .WithOutputLevel(1, 0.5)
                .WithEdge(new ModulationEdge(1, 0, true, 0.5)),
            ["sample-hold-noise"] = OperatorMatrixPatch.Silent(6)
                .WithOperator(0, Wave(OperatorWaveform.Sine, 1, adsr))
                .WithOperator(1, Wave(OperatorWaveform.SampleHoldNoise, 7, adsr))
                .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtFixed(90), adsr) with { Waveform = OperatorWaveform.Noise })
                .WithOutputLevel(0, 0.5)
                .WithOutputLevel(2, 0.2)
                .WithEdge(new ModulationEdge(1, 0, true, 1.0)),
        };
    }

    [Theory]
    [MemberData(nameof(Fm7Cases))]
    public void Fm7Voice_HeldThenReleased_IsTheOfflineNote(string name, int heldBlocks) =>
        AssertSameAsOffline(new Fm7CompatibleOperatorMatrixRenderer(), Fm7Patches[name], heldBlocks);

    [Theory]
    [InlineData("bell")]
    [InlineData("brass-like-step-pitch-envelope")]
    public void Voice_ReleasedBeforeTheFirstBlock_IsTheOfflineNoteOfZeroLength(string name) =>
        AssertSameAsOffline(new Fm7CompatibleOperatorMatrixRenderer(), Fm7Patches[name], heldBlocks: 0);

    /// <summary>
    /// Renders a note offline and as a voice released after <paramref name="heldBlocks"/> control blocks (before any
    /// rendering when zero), the rest of the voice in odd-sized chunks, and asserts that both are the same, sample for
    /// sample, over the length of the offline note.
    /// </summary>
    internal static void AssertSameAsOffline(IOperatorMatrixRenderer renderer, OperatorMatrixPatch patch, int heldBlocks, double velocity = 0.8, int midi = 57, int releaseEarlySamples = 0)
    {
        double hz = 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
        var request = new RenderRequest(
            hz,
            midi,
            velocity,
            TimeSpan.FromSeconds(heldBlocks * (double)BlockSize / SampleRate),
            TimeSpan.FromSeconds(1.5),
            SampleRate);
        Assert.Equal(heldBlocks, request.NoteSamples / BlockSize);
        float[] offline = renderer.Render(patch, request).Samples;

        IOperatorMatrixVoice voice = Assert.IsAssignableFrom<IVoiceRenderer>(renderer).StartVoice(patch, request.ToVoiceRequest());
        Assert.Equal(SampleRate, voice.SampleRate);
        var live = new float[offline.Length];
        int held = Math.Min((heldBlocks * BlockSize) - releaseEarlySamples, live.Length);
        for (int position = 0; position < held; position += Chunk)
        {
            voice.Render(live.AsSpan(position, Math.Min(Chunk, held - position)));
        }

        Assert.False(voice.IsReleased);
        voice.Release();
        Assert.True(voice.IsReleased);
        for (int position = held; position < live.Length; position += Chunk)
        {
            voice.Render(live.AsSpan(position, Math.Min(Chunk, live.Length - position)));
        }

        Assert.Equal(live.Length, voice.SamplesRendered);
        for (int i = 0; i < offline.Length; i++)
        {
            if (live[i] != offline[i])
            {
                Assert.Fail($"Sample {i} (block {i / BlockSize}) differs: voice {live[i]:R}, offline {offline[i]:R}.");
            }
        }
    }
}
