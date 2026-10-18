using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.IntegrationTests;

/// <summary>A voice held for a while and then released plays exactly the offline note of that duration.</summary>
public sealed class VoiceEquivalenceTests
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
    [InlineData("electric-piano", true)]
    [InlineData("saw-filter-sweep-up", false)]
    [InlineData("filtered-noise", false)]
    public void Voice_ReleasedInTheMiddleOfAControlBlock_IsTheOfflineNote(string name, bool fm7)
    {
        IOperatorMatrixRenderer renderer = fm7 ? new Fm7CompatibleOperatorMatrixRenderer() : new Fmx6CompatibleOperatorMatrixRenderer();
        AssertSameAsOffline(renderer, fm7 ? Fm7Patches[name] : Fmx6Patches[name], heldBlocks: 20, releaseEarlySamples: 10);
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
