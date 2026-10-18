using HearSay.OperatorMatrix.Priors;
using HearSay.Playing;
using HearSay.SuperCollider;
using HearSay.SuperCollider.FMX6;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.IntegrationTests;

/// <summary>The voice mixer: polyphony, stealing, release, the end of a voice, shedding and levels.</summary>
public sealed class VoiceMixerTests(ITestOutputHelper output)
{
    private const int Rate = 48000;
    private const int Fade = 240;

    // The shortest hold of a note: 5 ms.
    private const int Hold = 240;

    // The size of the buffers the live output asks for.
    private const int Chunk = 256;

    private static readonly object Midi = new();
    private static readonly object Mouse = new();

    [Fact]
    public void PatchLifetime_MatchesTheExporter()
    {
        var exporter = new SuperColliderFmx6Exporter();
        var random = new Random(5);
        var patches = new List<OperatorMatrixPatch>();
        for (int i = 0; i < 30; i++)
        {
            patches.Add(StructureLibrary.Generate(SynthesisProfiles.SuperColliderFmx6.Capabilities, random.Next(50_000_000, 50_300_000)));
        }

        // The library patches all sustain; a one-shot one and a key-scaled one take the other paths.
        patches.Add(OneShot());
        patches.Add(Sustaining().WithOperator(0, Sustaining().Operators[0] with { KeyScaling = new KeyScaling(0, 0.5, 60) }));
        int sustaining = 0;
        foreach (OperatorMatrixPatch patch in patches)
        {
            string synthDef = exporter.ExportSynthDef(patch);
            if (PatchLifetime.SecondsAfterGateOff(patch) is double afterGateOff)
            {
                sustaining++;
                Assert.Contains($"EnvGen.kr(Env.asr(0, 1, {SuperColliderOperatorMatrixExporter.Number(afterGateOff)}, \\lin), gate, doneAction: Done.freeSelf);", synthDef, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains($"Line.kr(0, 1, {SuperColliderOperatorMatrixExporter.Number(PatchLifetime.SecondsAfterStart(patch))}, doneAction: Done.freeSelf);", synthDef, StringComparison.Ordinal);
            }
        }

        output.WriteLine($"{sustaining} sustaining, {patches.Count - sustaining} one-shot.");
        Assert.InRange(sustaining, 1, patches.Count - 1);
    }

    [Fact]
    public async Task TwoNotes_SoundTogether()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        Assert.True(mixer.NoteOn(60, 0.8, sound, Midi));
        Assert.True(mixer.NoteOn(67, 0.6, sound, Midi));
        Assert.Equal(2, mixer.ActiveVoices);
        Assert.Equal(new[] { 60, 67 }, mixer.SoundingNotes.Order());

        float[] mix = Render(mixer, 4800);
        float[] first = Alone(sound, 60, 0.8, 4800);
        float[] second = Alone(sound, 67, 0.6, 4800);
        AssertMix(mix, 0, Sum(first, second));
    }

    [Fact]
    public async Task Velocity_IsVolumeOnlyForPatchesWithoutVelocitySensitivity()
    {
        OperatorMatrixPatch patch = Sustaining().WithOperator(0, Sustaining().Operators[0] with { VelocitySensitivity = 1.0 });
        Sound sound = SoundOf(patch);
        Assert.True(sound.RespondsToVelocity);
        Assert.False(SoundOf(Sustaining()).RespondsToVelocity);
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        mixer.NoteOn(60, 0.5, sound, Midi);

        // The voice plays the velocity itself; the mixer does not scale it a second time.
        float[] alone = Alone(sound, 60, 0.5, 4800);
        AssertMix(Render(mixer, 4800), 0, alone);
        Assert.Equal(Gain(sound) * Peak(Alone(sound, 60, 0.5, 4800, gain: 1f)), Peak(alone), 1e-6);
    }

    [Fact]
    public async Task SeventeenthNote_TakesTheOldestVoice()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        for (int i = 0; i < 16; i++)
        {
            mixer.NoteOn(48 + i, 0.4, sound, Midi);
        }

        Render(mixer, 480);
        Assert.Equal(16, mixer.ActiveVoices);
        int changes = 0;
        mixer.SoundingNotesChanged += () => changes++;

        mixer.NoteOn(64, 0.4, sound, Midi);
        Assert.Equal(1, changes);
        Assert.Equal(17, mixer.ActiveVoices);
        Assert.DoesNotContain(48, mixer.SoundingNotes);
        Assert.Contains(64, mixer.SoundingNotes);
        Assert.Equal(16, mixer.SoundingNotes.Count);

        // The stolen voice fades out over 5 ms, then it is gone.
        Render(mixer, Fade - 1);
        Assert.Equal(17, mixer.ActiveVoices);
        Render(mixer, 1);
        Assert.Equal(16, mixer.ActiveVoices);
        Assert.DoesNotContain(48, mixer.SoundingNotes);

        // A note-off for the stolen note is a stray one now.
        mixer.NoteOff(48, Midi);
        Assert.Equal(16, mixer.SoundingNotes.Count);
    }

    [Fact]
    public async Task Retrigger_ReleasesTheHeldVoice()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        mixer.NoteOn(60, 0.8, sound, Midi);
        Render(mixer, 4800);

        mixer.NoteOn(60, 0.8, sound, Midi);
        Assert.Equal(2, mixer.ActiveVoices);
        Assert.Equal(new[] { 60 }, mixer.SoundingNotes);
        float[] mix = Render(mixer, 4800);
        float[] old = Alone(sound, 60, 0.8, 9600, releaseAt: 4800);
        float[] fresh = Alone(sound, 60, 0.8, 4800);
        AssertMix(mix, 0, Sum(old.AsSpan(4800).ToArray(), fresh));

        // The released voice ends with its release; the new one is still held.
        Render(mixer, Rate / 2);
        Assert.Equal(1, mixer.ActiveVoices);
        Assert.Equal(new[] { 60 }, mixer.SoundingNotes);
        mixer.NoteOff(60, Midi);
        Assert.Empty(mixer.SoundingNotes);
        Render(mixer, Rate / 2);
        Assert.Equal(0, mixer.ActiveVoices);
    }

    [Fact]
    public async Task StrayNoteOff_IsIgnored()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        mixer.NoteOff(60, Midi);
        mixer.NoteOn(60, 0.8, sound, Midi);
        int changes = 0;
        mixer.SoundingNotesChanged += () => changes++;

        mixer.NoteOff(61, Midi);
        mixer.NoteOff(60, Mouse);
        Assert.Equal(0, changes);
        Assert.Equal(new[] { 60 }, mixer.SoundingNotes);
        AssertMix(Render(mixer, 4800), 0, Alone(sound, 60, 0.8, 4800));
        Assert.Equal(1, mixer.ActiveVoices);
    }

    [Fact]
    public async Task NoteOff_BeforeTheFirstRender_StillPlaysAndEnds()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        mixer.NoteOn(60, 0.8, sound, Midi);
        mixer.NoteOff(60, Midi);
        Assert.Empty(mixer.SoundingNotes);
        Assert.Equal(1, mixer.ActiveVoices);

        // Attack, then release: held for the shortest hold, since the note of zero length is silent for an envelope from 0.
        int lifetime = Samples(PatchLifetime.SecondsAfterGateOff(sound.Patch)!.Value);
        float[] mix = Render(mixer, Hold + lifetime + Chunk);
        Assert.True(Peak(mix) > 0.01f, $"Peak {Peak(mix)}.");
        AssertMix(mix.AsSpan(0, 2400).ToArray(), 0, Alone(sound, 60, 0.8, 2400, releaseAt: Hold));
        Assert.Equal(0, mixer.ActiveVoices);
    }

    [Fact]
    public async Task NoteOff_WhileTheSoundIsNotReady_EndsTheNote()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        Assert.True(mixer.NoteOn(60, 0.8, sound, Midi));
        mixer.NoteOff(60, Midi);
        Assert.Empty(mixer.SoundingNotes);
        Assert.Equal(1, mixer.ActiveVoices);

        await mixer.PrepareAsync(sound);
        int lifetime = Samples(PatchLifetime.SecondsAfterGateOff(sound.Patch)!.Value);
        float[] mix = Render(mixer, Hold + lifetime + Chunk);
        Assert.True(Peak(mix) > 0.01f, $"Peak {Peak(mix)}.");
        Assert.Equal(0, mixer.ActiveVoices);
    }

    [Fact]
    public async Task HeldNotes_KeepTheirSound_WhenTheSoundChanges()
    {
        Sound first = SoundOf(Sustaining());
        Sound second = SoundOf(Other());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(first);
        await mixer.PrepareAsync(second);
        mixer.NoteOn(60, 0.8, first, Midi);
        float[] before = Render(mixer, 2400);
        mixer.NoteOn(64, 0.8, second, Midi);
        float[] after = Render(mixer, 4800);

        float[] sixty = Alone(first, 60, 0.8, 7200);
        AssertMix(before, 0, sixty.AsSpan(0, 2400).ToArray());
        AssertMix(after, 0, Sum(sixty.AsSpan(2400).ToArray(), Alone(second, 64, 0.8, 4800)));
    }

    [Fact]
    public async Task ReleaseAll_ReleasesEveryHeldNote()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        mixer.NoteOn(60, 0.4, sound, Midi);
        mixer.NoteOn(64, 0.4, sound, Midi);
        mixer.NoteOn(67, 0.4, sound, Mouse);
        Render(mixer, 480);

        mixer.ReleaseAll(Midi);
        Assert.Equal(new[] { 67 }, mixer.SoundingNotes);
        Render(mixer, Rate);
        Assert.Equal(1, mixer.ActiveVoices);
        Assert.Equal(new[] { 67 }, mixer.SoundingNotes);
    }

    [Fact]
    public async Task OneShot_EndsAfterItsEnvelope()
    {
        Sound sound = SoundOf(OneShot());
        Assert.Null(PatchLifetime.SecondsAfterGateOff(sound.Patch));
        int lifetime = Samples(PatchLifetime.SecondsAfterStart(sound.Patch));
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        mixer.NoteOn(60, 0.8, sound, Midi);
        int changes = 0;
        mixer.SoundingNotesChanged += () => changes++;

        // Held all along: the envelope runs, and the voice ends with it.
        float[] early = Render(mixer, Rate / 5);
        Assert.True(Peak(early) > 0.01f);
        Assert.Equal(1, mixer.ActiveVoices);
        Render(mixer, lifetime - (Rate / 5) + Chunk);
        Assert.Equal(0, mixer.ActiveVoices);
        Assert.Empty(mixer.SoundingNotes);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Sustaining_HoldsUntilNoteOff()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        mixer.NoteOn(60, 0.8, sound, Midi);
        float[] held = Render(mixer, 3 * Rate);
        Assert.Equal(1, mixer.ActiveVoices);
        Assert.True(Peak(held.AsSpan(held.Length - 4800).ToArray()) > 0.05f);

        mixer.NoteOff(60, Midi);
        Render(mixer, Rate / 2);
        Assert.Equal(0, mixer.ActiveVoices);
    }

    [Fact]
    public async Task NonZeroRelease_EndsAtTheLifetimeWithAFade()
    {
        // The release stops at 0.3 and stays there; only the lifetime ends the voice.
        var envelope = new OperatorEnvelope(
            [new EnvelopePoint(0, 0), new EnvelopePoint(0.01, 1), new EnvelopePoint(0.1, 0.7), new EnvelopePoint(0.2, 0.3)],
            SustainPoint: 2,
            ReleasePoint: 3);
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), envelope))
            .WithOutputLevel(0, 0.8);
        Sound sound = SoundOf(patch);
        int lifetime = Samples(PatchLifetime.SecondsAfterGateOff(patch)!.Value);
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        mixer.NoteOn(60, 0.8, sound, Midi);
        Render(mixer, 4800);
        mixer.NoteOff(60, Midi);
        float[] mix = Render(mixer, lifetime + 4800);
        Assert.Equal(0, mixer.ActiveVoices);

        float[] alone = Alone(sound, 60, 0.8, 4800 + lifetime + 4800, releaseAt: 4800).AsSpan(4800).ToArray();
        var expected = new float[mix.Length];
        for (int i = 0; i < lifetime; i++)
        {
            int left = lifetime - i;
            expected[i] = left > Fade ? alone[i] : alone[i] * ((left - 1) / (float)Fade);
        }

        AssertMix(mix, 0, expected);
        Assert.True(Peak(mix.AsSpan(lifetime - (2 * Fade), Fade).ToArray()) > 0.05f);
        Assert.Equal(0f, mix[lifetime - 1]);
    }

    [Fact]
    public async Task Shed_DropsTheOldestVoicesWithAFade()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        int[] notes = [60, 64, 67, 72];
        foreach (int note in notes)
        {
            mixer.NoteOn(note, 0.4, sound, Midi);
        }

        Render(mixer, 4800);
        mixer.Shed(2);
        Assert.Equal(4, mixer.ActiveVoices);
        Assert.Equal(new[] { 67, 72 }, mixer.SoundingNotes.Order());
        float[] mix = Render(mixer, 4800);
        Assert.Equal(2, mixer.ActiveVoices);

        float[][] alone = [.. notes.Select(n => Alone(sound, n, 0.4, 9600).AsSpan(4800).ToArray())];
        var expected = new float[mix.Length];
        for (int i = 0; i < mix.Length; i++)
        {
            float fade = i < Fade ? (Fade - 1 - i) / (float)Fade : 0f;
            expected[i] = (alone[0][i] * fade) + (alone[1][i] * fade) + alone[2][i] + alone[3][i];
        }

        AssertMix(mix, 0, expected);

        // A note-off for a dropped note is a stray one.
        mixer.NoteOff(60, Midi);
        Assert.Equal(new[] { 67, 72 }, mixer.SoundingNotes.Order());
    }

    [Fact]
    public async Task ManyLoudNotes_AreSoftLimited()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        for (int i = 0; i < 16; i++)
        {
            mixer.NoteOn(48 + (i % 4), 1.0, sound, i % 2 == 0 ? Midi : Mouse);
        }

        float[] mix = Render(mixer, 9600);
        output.WriteLine($"Peak of sixteen loud notes: {Peak(mix)}.");
        Assert.True(Peak(mix) > 0.9f);
        Assert.True(Peak(mix) <= 1f);
    }

    [Fact]
    public async Task NoteOn_OfAProfileWithoutALiveVoice_IsNotPlayed()
    {
        var sound = new Sound(Sustaining(), SynthesisProfiles.HearSayExtended, RenderRequest.Create(220, 1.0, 0.3));
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        Assert.False(mixer.NoteOn(60, 0.8, sound, Midi));
        Assert.Equal(0, mixer.ActiveVoices);
        Assert.Empty(mixer.SoundingNotes);
        Assert.Equal(0f, Peak(Render(mixer, 480)));
    }

    [Fact]
    public async Task NoteMethods_FromOtherThreads_WhileRendering()
    {
        Sound sound = SoundOf(Sustaining());
        var mixer = new VoiceMixer();
        await mixer.PrepareAsync(sound);
        int changes = 0;
        mixer.SoundingNotesChanged += () => Interlocked.Increment(ref changes);
        Task Play(object source, int seed) => Task.Run(() =>
        {
            var random = new Random(seed);
            for (int i = 0; i < 3000; i++)
            {
                int note = 48 + random.Next(24);
                if (random.Next(2) == 0)
                {
                    mixer.NoteOn(note, 0.3, sound, source);
                }
                else
                {
                    mixer.NoteOff(note, source);
                }

                if (i % 300 == 0)
                {
                    mixer.Shed(1);
                }

                Assert.True(mixer.SoundingNotes.Count <= 24);
            }
        });

        Task midi = Play(Midi, 1);
        Task mouse = Play(Mouse, 2);
        var buffer = new float[Chunk];
        float peak = 0;
        int buffers = 0;
        while (!midi.IsCompleted || !mouse.IsCompleted || buffers < 10)
        {
            mixer.Render(buffer);
            peak = Math.Max(peak, Peak(buffer));
            buffers++;
        }

        await Task.WhenAll(midi, mouse);
        mixer.ReleaseAll(Midi);
        mixer.ReleaseAll(Mouse);
        Assert.Empty(mixer.SoundingNotes);
        Render(mixer, Rate);
        output.WriteLine($"{buffers} buffers, {changes} changes, peak {peak}.");
        Assert.Equal(0, mixer.ActiveVoices);
        Assert.True(peak is > 0f and <= 1f);
    }

    private static Sound SoundOf(OperatorMatrixPatch patch) => new(patch, SynthesisProfiles.SuperColliderFm7, RenderRequest.Create(220, 1.0, 0.3));

    private static OperatorMatrixPatch Sustaining() =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.1, 0.7, 0.2)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.2)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 1.0));

    private static OperatorMatrixPatch Other() =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.02, 0.3, 0.6, 0.3)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(3.5), OperatorEnvelope.Adsr(0.005, 0.1, 0.3, 0.2)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 2.0));

    private static OperatorMatrixPatch OneShot() =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(
                OperatorFrequency.AtRatio(1),
                new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.005, 1), new EnvelopePoint(0.3, 0)], SustainPoint: null, ReleasePoint: 3)))
            .WithOutputLevel(0, 0.8);

    private static int Samples(double seconds) => (int)Math.Round(seconds * Rate);

    private static double Hz(int note) => 440.0 * Math.Pow(2.0, (note - 69) / 12.0);

    /// <summary>The gain of a sound as the Studio note player computes it: 0.5 over the peak of its reference at full velocity.</summary>
    private static float Gain(Sound sound)
    {
        RenderedAudio full = SynthesisBackends.RendererFor(sound.Profile).Render(sound.Patch, sound.GainReference with { Velocity = 1.0 });
        return full.Peak > 0 ? 0.5f / full.Peak : 1f;
    }

    /// <summary>One note rendered on a voice of its own at the level the mixer gives it, released at a sample or held.</summary>
    private static float[] Alone(Sound sound, int note, double velocity, int length, int? releaseAt = null, float? gain = null)
    {
        var renderer = Assert.IsAssignableFrom<IVoiceRenderer>(SynthesisBackends.RendererFor(sound.Profile));
        IOperatorMatrixVoice voice = renderer.StartVoice(sound.Patch, new VoiceRequest(Hz(note), note, velocity, Rate));
        var samples = new float[length];
        int held = Math.Min(releaseAt ?? length, length);
        if (held > 0)
        {
            voice.Render(samples.AsSpan(0, held));
        }

        if (releaseAt is not null)
        {
            voice.Release();
            voice.Render(samples.AsSpan(held));
        }

        float level = (gain ?? Gain(sound)) * (sound.RespondsToVelocity ? 1f : (float)velocity);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= level;
        }

        return samples;
    }

    private static float[] Render(VoiceMixer mixer, int length)
    {
        var samples = new float[length];
        for (int position = 0; position < length; position += Chunk)
        {
            mixer.Render(samples.AsSpan(position, Math.Min(Chunk, length - position)));
        }

        return samples;
    }

    private static float[] Sum(float[] a, float[] b)
    {
        var sum = new float[a.Length];
        for (int i = 0; i < sum.Length; i++)
        {
            sum[i] = a[i] + b[i];
        }

        return sum;
    }

    private static float Peak(float[] samples) => samples.Length == 0 ? 0f : samples.Max(Math.Abs);

    /// <summary>Asserts that a mix equals the expected samples, which stay under the limiter threshold.</summary>
    private static void AssertMix(float[] mix, int from, float[] expected)
    {
        Assert.True(Peak(expected) < 0.9f, $"The expected samples reach {Peak(expected)}, above the limiter threshold.");
        for (int i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(mix[from + i] - expected[i]) > 1e-6f)
            {
                Assert.Fail($"Sample {from + i} differs: mix {mix[from + i]:R}, expected {expected[i]:R}.");
            }
        }
    }
}
