using HearSay.Editing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>The vowel presets of the formant filter, from the Csound formant table; fast, no rendering.</summary>
public sealed class FormantPresetsTests
{
    private static readonly SynthesisProfile Fm7 = SynthesisProfiles.SuperColliderFm7;
    private static readonly SynthesisProfile Fmx6 = SynthesisProfiles.SuperColliderFmx6;
    private static readonly SynthesisProfile Nome6 = SynthesisProfiles.SuperColliderNome6;

    /// <summary>A saw carrier on OP1, heard; OP2 to OP6 are free.</summary>
    private static OperatorMatrixPatch Carrier(OperatorFilter? filter = null)
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)).WithWaveform(OperatorWaveform.Saw) with { Filter = filter })
            .WithOutputLevel(0, 1.0);

    [Fact]
    public void EveryPreset_IsValidAndCompatible_OnFmx6()
    {
        int count = 0;
        foreach (FormantVoice voice in Enum.GetValues<FormantVoice>())
        {
            foreach (FormantVowel vowel in Enum.GetValues<FormantVowel>())
            {
                OperatorFilter filter = FormantPresets.Filter(voice, vowel, keepSweepFrom: null);
                Assert.Equal(OperatorFilterType.Formant, filter.Type);
                Assert.Empty(filter.Validate());

                // F2..F5 all on, each above the one before, each with its own Q.
                IReadOnlyList<FormantPeak> peaks = filter.FormantPeaks;
                Assert.All(peaks, static p => Assert.InRange(p.Level, 1e-6, 1.0));
                Assert.All(peaks, static p => Assert.InRange(p.Q, OperatorFilter.MinQ, OperatorFilter.MaxFormantQ));
                for (int n = 1; n < peaks.Count; n++)
                {
                    Assert.True(peaks[n].Ratio > peaks[n - 1].Ratio, $"{voice} {vowel}: F{n + 2} is not above F{n + 1}");
                }

                OperatorMatrixPatch patch = Carrier(filter);
                Assert.True(PatchCompatibilityValidator.Instance.Validate(patch, Fmx6.Capabilities).IsCompatible);

                var editor = new PatchEditor(Carrier(), Fmx6);
                EditOutcome outcome = editor.Apply(PatchEdits.SetFormantPreset(0, voice, vowel));
                Assert.False(outcome.IsRefused, outcome.Refusal);
                Assert.Equal(patch.ComputeHash(), editor.Current.ComputeHash());
                count++;
            }
        }

        Assert.Equal(25, count);
    }

    [Fact]
    public void Preset_FollowsTheTable()
    {
        // soprano a: 800 1150 2900 3900 4950 Hz, 0 -6 -32 -20 -50 dB, 80 90 120 130 140 Hz wide.
        OperatorFilter a = FormantPresets.Filter(FormantVoice.Soprano, FormantVowel.A, null);
        Assert.Equal(800.0, a.FixedHz);
        Assert.Equal(10.0, a.Q, 12);
        Assert.Equal(1150.0 / 800.0, a.FormantRatio, 12);
        Assert.Equal(1.4375, a.FormantRatio, 12);
        Assert.Equal(Math.Pow(10, -6.0 / 20.0), a.FormantLevel, 12);
        Assert.Equal(1150.0 / 90.0, a.FormantQ2, 12);
        Assert.Equal(2900.0 / 800.0, a.Formant3Ratio, 12);
        Assert.Equal(Math.Pow(10, -32.0 / 20.0), a.Formant3Level, 12);
        Assert.Equal(2900.0 / 120.0, a.Formant3Q, 12);
        Assert.Equal(3900.0 / 800.0, a.Formant4Ratio, 12);
        Assert.Equal(0.1, a.Formant4Level, 12);
        Assert.Equal(30.0, a.Formant4Q, 12);
        Assert.Equal(4950.0 / 800.0, a.Formant5Ratio, 12);
        Assert.Equal(Math.Pow(10, -50.0 / 20.0), a.Formant5Level, 12);
        Assert.Equal(4950.0 / 140.0, a.Formant5Q, 12);
        Assert.Equal(0.0, a.EnvelopeOctaves);
        Assert.Null(a.Envelope);

        // bass u: 350 600 2400 2675 2950 Hz, 0 -20 -32 -28 -36 dB, 40 80 100 120 120 Hz wide.
        OperatorFilter u = FormantPresets.Filter(FormantVoice.Bass, FormantVowel.U, null);
        Assert.Equal(
            OperatorFilter.Formants(
                350,
                350.0 / 40.0,
                new FormantPeak(600.0 / 350.0, Math.Pow(10, -20.0 / 20.0), 600.0 / 80.0),
                new FormantPeak(2400.0 / 350.0, Math.Pow(10, -32.0 / 20.0), 2400.0 / 100.0),
                new FormantPeak(2675.0 / 350.0, Math.Pow(10, -28.0 / 20.0), 2675.0 / 120.0),
                new FormantPeak(2950.0 / 350.0, Math.Pow(10, -36.0 / 20.0), 2950.0 / 120.0)),
            u);

        // tenor o: 400 800 2600 2800 3000 Hz, 0 -10 -12 -12 -26 dB, 40 80 100 120 120 Hz wide.
        OperatorFilter o = FormantPresets.Filter(FormantVoice.Tenor, FormantVowel.O, null);
        Assert.Equal(400.0, o.FixedHz);
        Assert.Equal(10.0, o.Q, 12);
        Assert.Equal(2.0, o.FormantRatio, 12);
        Assert.Equal(Math.Pow(10, -10.0 / 20.0), o.FormantLevel, 12);
        Assert.Equal(10.0, o.FormantQ2, 12);
        Assert.Equal(7.5, o.Formant5Ratio, 12);
        Assert.Equal(25.0, o.Formant5Q, 12);

        // soprano i has the widest spread of the table, F5 / F1 = 4950 / 270 = 18.3, within the ratio range of 32.
        Assert.Equal(4950.0 / 270.0, FormantPresets.Filter(FormantVoice.Soprano, FormantVowel.I, null).Formant5Ratio, 12);

        // All 25 against the table as the design spec has it (the spec is found from the test's folder upwards).
        string[] rows = [.. SpecTable()];
        Assert.Equal(25, rows.Length);
        foreach (string line in rows)
        {
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            FormantVoice voice = Enum.Parse<FormantVoice>(words[0], ignoreCase: true);
            FormantVowel vowel = Enum.Parse<FormantVowel>(words[1], ignoreCase: true);
            double[] v = [.. words.Skip(2).Select(static w => double.Parse(w, System.Globalization.CultureInfo.InvariantCulture))];
            Assert.Equal(15, v.Length);
            OperatorFilter filter = FormantPresets.Filter(voice, vowel, null);
            Assert.Equal(v[0], filter.FixedHz);
            Assert.Equal(v[0] / v[10], filter.Q, 12);
            for (int n = 1; n < 5; n++)
            {
                FormantPeak peak = filter.FormantPeaks[n - 1];
                Assert.Equal(v[n] / v[0], peak.Ratio, 12);
                Assert.Equal(Math.Pow(10, v[5 + n] / 20.0), peak.Level, 12);
                Assert.Equal(v[n] / v[10 + n], peak.Q, 12);
            }
        }
    }

    /// <summary>The rows of the Csound formant table in the design spec, such as "soprano a 800 1150 …".</summary>
    private static IEnumerable<string> SpecTable()
    {
        string spec = Path.Combine("docs", "superpowers", "specs", "2026-10-19-five-formants-design.md");
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, spec)))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string[] voices = [.. Enum.GetNames<FormantVoice>().Select(static n => n.ToLowerInvariant() + " ")];
        return File.ReadLines(Path.Combine(directory.FullName, spec)).Where(l => voices.Any(v => l.StartsWith(v, StringComparison.Ordinal)));
    }

    [Fact]
    public void Preset_KeepsTheSweep()
    {
        var sweep = OperatorEnvelope.Adsr(0.005, 0.3, 0.0, 0.2);
        OperatorFilter swept = OperatorFilter.FormantAt(500, 2.0, 0.4, 6) with { EnvelopeOctaves = 1.5, Envelope = sweep };
        OperatorFilter preset = FormantPresets.Filter(FormantVoice.Alto, FormantVowel.E, swept);
        Assert.Equal(1.5, preset.EnvelopeOctaves);
        Assert.Same(sweep, preset.Envelope);
        Assert.Equal(400.0, preset.FixedHz);
        Assert.Equal(FormantPresets.Filter(FormantVoice.Alto, FormantVowel.E, null) with { EnvelopeOctaves = 1.5, Envelope = sweep }, preset);

        // Through the edit, on a low-pass with a sweep: it becomes a formant filter and keeps the sweep.
        OperatorMatrixPatch lowPass = Carrier(OperatorFilter.LowPass(3) with { EnvelopeOctaves = -2.0, Envelope = sweep });
        var editor = new PatchEditor(lowPass, Fmx6);
        EditOutcome outcome = editor.Apply(PatchEdits.SetFormantPreset(0, FormantVoice.Tenor, FormantVowel.A));
        Assert.False(outcome.IsRefused, outcome.Refusal);
        OperatorFilter filter = editor.Current.Operators[0].Filter!;
        Assert.Equal(OperatorFilterType.Formant, filter.Type);
        Assert.Equal(-2.0, filter.EnvelopeOctaves);
        Assert.Same(sweep, filter.Envelope);
        Assert.Equal(650.0, filter.FixedHz);

        // The rest of the operator stays as it was.
        Assert.Equal(lowPass.Operators[0] with { Filter = null }, editor.Current.Operators[0] with { Filter = null });

        // Without a filter, the preset gives the operator one, without a sweep.
        var bare = new PatchEditor(Carrier(), Fmx6);
        Assert.False(bare.Apply(PatchEdits.SetFormantPreset(0, FormantVoice.Bass, FormantVowel.I)).IsRefused);
        Assert.Equal(FormantPresets.Filter(FormantVoice.Bass, FormantVowel.I, null), bare.Current.Operators[0].Filter);
    }

    [Fact]
    public void Preset_IsOneUndoStep()
    {
        OperatorMatrixPatch original = Carrier(OperatorFilter.FormantAt(700, 1.8, 0.5, 8));
        var editor = new PatchEditor(original, Fmx6);
        Assert.False(editor.Apply(PatchEdits.SetFormantPreset(0, FormantVoice.Soprano, FormantVowel.O)).IsRefused);
        Assert.True(editor.CanUndo);
        Assert.Equal(450.0, editor.Current.Operators[0].Filter!.FixedHz);

        editor.Undo();
        Assert.False(editor.CanUndo);
        Assert.Equal(original.ComputeHash(), editor.Current.ComputeHash());

        editor.Redo();
        Assert.Equal(450.0, editor.Current.Operators[0].Filter!.FixedHz);

        // The same preset again is no step at all.
        Assert.False(editor.Apply(PatchEdits.SetFormantPreset(0, FormantVoice.Soprano, FormantVowel.O)).IsRefused);
        editor.Undo();
        Assert.False(editor.CanUndo);

        // A missing operator is refused.
        Assert.True(editor.Apply(PatchEdits.SetFormantPreset(6, FormantVoice.Soprano, FormantVowel.O)).IsRefused);
    }

    [Fact]
    public void Preset_OnFm7_IsRefused()
    {
        // A sine carrier, which both profiles play; neither has operator filters.
        OperatorMatrixPatch sine = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)))
            .WithOutputLevel(0, 1.0);
        foreach (SynthesisProfile profile in new[] { Fm7, Nome6 })
        {
            var editor = new PatchEditor(sine, profile);
            EditOutcome outcome = editor.Apply(PatchEdits.SetFormantPreset(0, FormantVoice.Soprano, FormantVowel.A));
            Assert.True(outcome.IsRefused);
            Assert.Equal("This profile has no operator filters.", outcome.Refusal);
            Assert.Equal(sine.ComputeHash(), editor.Current.ComputeHash());
            Assert.False(editor.CanUndo);
        }
    }
}
