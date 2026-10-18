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
    internal static void AssertSameAsOffline(IOperatorMatrixRenderer renderer, OperatorMatrixPatch patch, int heldBlocks, double velocity = 0.8, int midi = 57)
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
        int held = Math.Min(heldBlocks * BlockSize, live.Length);
        if (held > 0)
        {
            voice.Render(live.AsSpan(0, held));
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
