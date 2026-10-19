using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HearSay.Editing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>
/// The vowel presets of the formant filter: the sung Csound table and the measured English and Dutch speech vowels;
/// fast, no rendering.
/// </summary>
public sealed class FormantPresetsTests
{
    private static readonly SynthesisProfile Fm7 = SynthesisProfiles.SuperColliderFm7;
    private static readonly SynthesisProfile Fmx6 = SynthesisProfiles.SuperColliderFmx6;
    private static readonly SynthesisProfile Nome6 = SynthesisProfiles.SuperColliderNome6;

    /// <summary>The speech bandwidths of the spec, B1..B5 in hertz.</summary>
    private static readonly double[] SpeechBandwidths = [80, 100, 150, 250, 300];

    private static FormantPresetSet Set(string name) => Assert.Single(FormantPresets.Sets, s => s.Name == name);

    private static FormantPresetSet Sung => Set("Sung");

    private static FormantPresetSet English => Set("English");

    private static FormantPresetSet Dutch => Set("Dutch");

    /// <summary>A saw carrier on OP1, heard; OP2 to OP6 are free.</summary>
    private static OperatorMatrixPatch Carrier(OperatorFilter? filter = null)
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)).WithWaveform(OperatorWaveform.Saw) with { Filter = filter })
            .WithOutputLevel(0, 1.0);

    /// <summary>The carrier as the voice source, a 1 % pulse with 64 harmonics, through a filter.</summary>
    private static OperatorMatrixPatch VoiceSourceCarrier(OperatorFilter filter)
        => Carrier(filter).WithOperator(0, Carrier(filter).Operators[0].WithWaveform(OperatorWaveform.Pulse) with { Shape = 0.01, HarmonicLimit = 64 });

    /// <summary>
    /// The level of each of F2..F5 relative to F1 in the spec's cascade model: a two-pole resonance of unity gain at
    /// 0 Hz per formant, times a 1/f source-plus-radiation slope; written out here independently of the code.
    /// </summary>
    private static double[] Cascade(double[] f, double[] b)
    {
        double H(double at)
        {
            double h = 1.0 / at;
            for (int k = 0; k < 5; k++)
            {
                double pole = (f[k] * f[k]) + (b[k] * b[k] / 4.0);
                h *= pole / Math.Sqrt(Math.Pow(pole - (at * at), 2) + Math.Pow(b[k] * at, 2));
            }

            return h;
        }

        return [.. Enumerable.Range(1, 4).Select(n => H(f[n]) / H(f[0]))];
    }

    [Fact]
    public void Sets_AreSungEnglishDutch_WithTheirSpeakersAndVowels()
    {
        Assert.Equal(["Sung", "English", "Dutch"], FormantPresets.Sets.Select(static s => s.Name));
        Assert.Equal(["soprano", "alto", "countertenor", "tenor", "bass"], Sung.Speakers);
        Assert.Equal(["a", "e", "i", "o", "u"], Sung.Vowels.Select(static v => v.Word));
        Assert.Equal(["men", "women", "children"], English.Speakers);
        Assert.Equal(["heed", "hid", "hayed", "head", "had", "hod", "hawed", "hoed", "hood", "who'd", "hud", "heard"], English.Vowels.Select(static v => v.Word));
        Assert.Equal(["iy", "ih", "ei", "eh", "ae", "ah", "aw", "oa", "oo", "uw", "uh", "er"], English.Vowels.Select(static v => v.Key));
        Assert.Equal(["men", "women"], Dutch.Speakers);
        Assert.Equal(["poet", "paat", "poot", "pat", "peut", "piet", "puut", "peet", "put", "pet", "pot", "pit"], Dutch.Vowels.Select(static v => v.Word));
        Assert.Equal(["oe", "aa", "oo", "a", "eu", "ie", "uu", "ee", "u", "e", "o", "i"], Dutch.Vowels.Select(static v => v.Key));
        Assert.Equal("/ʏ/", Assert.Single(Dutch.Vowels, static v => v.Word == "put").Ipa);
        Assert.Equal("/ɝ/", Assert.Single(English.Vowels, static v => v.Word == "heard").Ipa);
        Assert.All(FormantPresets.Sets, static s => Assert.False(string.IsNullOrWhiteSpace(s.Source)));
        Assert.Contains("Hillenbrand", English.Source, StringComparison.Ordinal);
        Assert.Contains("Pols", Dutch.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPreset_OfEverySet_IsValidAndCompatible_OnFmx6()
    {
        var counts = new List<int>();
        foreach (FormantPresetSet set in FormantPresets.Sets)
        {
            int count = 0;
            foreach (string speaker in set.Speakers)
            {
                foreach (FormantPresetVowel vowel in set.Vowels)
                {
                    string what = $"{set.Name} {speaker} {vowel.Word}";
                    OperatorFilter filter = FormantPresets.Filter(set, speaker, vowel.Key, keepSweepFrom: null);
                    Assert.Equal(OperatorFilterType.Formant, filter.Type);
                    Assert.Empty(filter.Validate());

                    // F2..F5 all on, each above the one before, each with its own Q.
                    IReadOnlyList<FormantPeak> peaks = filter.FormantPeaks;
                    Assert.All(peaks, p => Assert.True(p.Level is > 1e-6 and <= 1.0, what));
                    Assert.All(peaks, p => Assert.InRange(p.Q, OperatorFilter.MinQ, OperatorFilter.MaxFormantQ));
                    Assert.True(peaks[0].Ratio > 1.0, what);
                    for (int n = 1; n < peaks.Count; n++)
                    {
                        Assert.True(peaks[n].Ratio > peaks[n - 1].Ratio, $"{what}: F{n + 2} is not above F{n + 1}");
                    }

                    OperatorMatrixPatch patch = Carrier(filter);
                    Assert.True(PatchCompatibilityValidator.Instance.Validate(patch, Fmx6.Capabilities).IsCompatible, what);

                    // Through the edit on the bare saw, which becomes the voice source as well.
                    var editor = new PatchEditor(Carrier(), Fmx6);
                    EditOutcome outcome = editor.Apply(PatchEdits.SetFormantPreset(0, set, speaker, vowel.Key));
                    Assert.False(outcome.IsRefused, outcome.Refusal);
                    Assert.Equal(VoiceSourceCarrier(filter).ComputeHash(), editor.Current.ComputeHash());
                    count++;
                }
            }

            counts.Add(count);
        }

        Assert.Equal([25, 36, 24], counts);
    }

    [Fact]
    public void SungPresets_AreUnchanged()
    {
        // The digest of the 25 sung presets as Task 4 made them (soprano..bass x a..u), captured from its code before
        // the preset sets: the hash of a patch with each filter on OP1.
        var text = new StringBuilder();
        foreach (string speaker in Sung.Speakers)
        {
            foreach (FormantPresetVowel vowel in Sung.Vowels)
            {
                OperatorFilter f = FormantPresets.Filter(Sung, speaker, vowel.Key, null);
                OperatorMatrixPatch p = OperatorMatrixPatch.Silent(6).WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Filter = f }).WithOutputLevel(0, 1.0);
                text.Append(p.ComputeHash()).Append('\n');
            }
        }

        Assert.Equal("9179AD67A81B7CD9385CC2E307EF69FC6E4F3EE1709D6FBFAE92E9BA2C6CB4F7", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));

        // soprano a: 800 1150 2900 3900 4950 Hz, 0 -6 -32 -20 -50 dB, 80 90 120 130 140 Hz wide.
        OperatorFilter a = FormantPresets.Filter(Sung, "soprano", "a", null);
        Assert.Equal(800.0, a.FixedHz);
        Assert.Equal(10.0, a.Q, 12);
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
        Assert.Equal(
            OperatorFilter.Formants(
                350,
                350.0 / 40.0,
                new FormantPeak(600.0 / 350.0, Math.Pow(10, -20.0 / 20.0), 600.0 / 80.0),
                new FormantPeak(2400.0 / 350.0, Math.Pow(10, -32.0 / 20.0), 2400.0 / 100.0),
                new FormantPeak(2675.0 / 350.0, Math.Pow(10, -28.0 / 20.0), 2675.0 / 120.0),
                new FormantPeak(2950.0 / 350.0, Math.Pow(10, -36.0 / 20.0), 2950.0 / 120.0)),
            FormantPresets.Filter(Sung, "bass", "u", null));

        // tenor o: 400 800 2600 2800 3000 Hz, 0 -10 -12 -12 -26 dB, 40 80 100 120 120 Hz wide.
        OperatorFilter o = FormantPresets.Filter(Sung, "tenor", "o", null);
        Assert.Equal((400.0, 2.0, 7.5, 25.0), (o.FixedHz, o.FormantRatio, o.Formant5Ratio, o.Formant5Q));
        Assert.Equal(10.0, o.Q, 12);
        Assert.Equal(10.0, o.FormantQ2, 12);

        // soprano i has the widest spread of the table, F5 / F1 = 4950 / 270 = 18.3, within the ratio range of 32.
        Assert.Equal(4950.0 / 270.0, FormantPresets.Filter(Sung, "soprano", "i", null).Formant5Ratio, 12);
    }

    /// <summary>
    /// All 25 sung presets against the Csound table as the design spec has it, read from the spec itself; skipped where
    /// the spec is not found (a test run outside the repository).
    /// </summary>
    [SpecFact]
    public void Preset_FollowsTheSpecsTable()
    {
        List<string> rows = FencedBlock(SpecFactAttribute.SpecPath()!, "Csound formant table");
        Assert.Equal(25, rows.Count);
        foreach (string line in rows)
        {
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double[] v = [.. words.Skip(2).Select(static w => double.Parse(w, CultureInfo.InvariantCulture))];
            Assert.Equal(15, v.Length);
            OperatorFilter filter = FormantPresets.Filter(Sung, words[0], words[1], null);
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

    /// <summary>
    /// All 60 speech means against the spec's "Speech vowel presets" block: F1..F3 and, for Dutch, the measured levels
    /// of F2 and F3; skipped where the spec is not found.
    /// </summary>
    [SpecFact]
    public void SpeechPresets_FollowTheSpecsMeans()
    {
        List<string> rows = FencedBlock(SpecFactAttribute.SpecPath()!, "Speech vowel presets");
        Assert.Equal(60, rows.Count);
        foreach (string line in rows)
        {
            // "Dutch m ie n=50 F=294 2208 2766 L=5.5 25.0 26.1" or "English men iy n=45 F=343 2323 3001".
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            FormantPresetSet set = Set(words[0]);
            string speaker = words[1] switch { "m" => "men", "f" => "women", var s => s };
            double[] f = [.. words[4..7].Select(static (w, i) => double.Parse(i == 0 ? w[2..] : w, CultureInfo.InvariantCulture))];
            OperatorFilter filter = FormantPresets.Filter(set, speaker, words[2], null);
            Assert.Equal(f[0], filter.FixedHz);
            Assert.Equal(f[1] / f[0], filter.FormantRatio, 12);
            Assert.Equal(f[2] / f[0], filter.Formant3Ratio, 12);
            if (set == Dutch)
            {
                double[] l = [.. words[7..10].Select(static (w, i) => double.Parse(i == 0 ? w[2..] : w, CultureInfo.InvariantCulture))];
                Assert.Equal(Math.Min(1.0, Math.Pow(10, (l[0] - l[1]) / 20.0)), filter.FormantLevel, 12);
                Assert.Equal(Math.Min(1.0, Math.Pow(10, (l[0] - l[2]) / 20.0)), filter.Formant3Level, 12);
            }
        }
    }

    [Fact]
    public void DutchPreset_FollowsTheMeasuredData()
    {
        // men "piet" /i/: F 294 2208 2766 Hz, L 5.5 25.0 26.1 dB below the overall level.
        OperatorFilter piet = FormantPresets.Filter(Dutch, "men", "ie", null);
        Assert.Equal(294.0, piet.FixedHz);
        Assert.Equal(294.0 / 80.0, piet.Q, 12);
        Assert.Equal(2208.0 / 294.0, piet.FormantRatio, 12);
        Assert.Equal(2766.0 / 294.0, piet.Formant3Ratio, 12);
        Assert.Equal(Math.Pow(10, (5.5 - 25.0) / 20.0), piet.FormantLevel, 12);
        Assert.Equal(Math.Pow(10, (5.5 - 26.1) / 20.0), piet.Formant3Level, 12);
        Assert.Equal(2208.0 / 100.0, piet.FormantQ2, 12);
        Assert.Equal(2766.0 / 150.0, piet.Formant3Q, 12);

        // F4 and F5 of men, with the model's levels taken relative to the measured F3.
        Assert.Equal(3500.0 / 294.0, piet.Formant4Ratio, 12);
        Assert.Equal(4500.0 / 294.0, piet.Formant5Ratio, 12);
        Assert.Equal(3500.0 / 250.0, piet.Formant4Q, 12);
        Assert.Equal(4500.0 / 300.0, piet.Formant5Q, 12);
        double[] model = Cascade([294, 2208, 2766, 3500, 4500], SpeechBandwidths);
        Assert.Equal(Math.Min(1.0, model[2] / model[1] * piet.Formant3Level), piet.Formant4Level, 12);
        Assert.Equal(Math.Min(1.0, model[3] / model[1] * piet.Formant3Level), piet.Formant5Level, 12);

        // women "pit" /ɪ/: F 465 2262 2840 Hz, L 1.8 24.2 26.4; F4 and F5 of women.
        OperatorFilter pit = FormantPresets.Filter(Dutch, "women", "i", null);
        Assert.Equal(465.0, pit.FixedHz);
        Assert.Equal(2262.0 / 465.0, pit.FormantRatio, 12);
        Assert.Equal(Math.Pow(10, (1.8 - 24.2) / 20.0), pit.FormantLevel, 12);
        Assert.Equal(Math.Pow(10, (1.8 - 26.4) / 20.0), pit.Formant3Level, 12);
        Assert.Equal(4100.0 / 465.0, pit.Formant4Ratio, 12);
        Assert.Equal(5000.0 / 465.0, pit.Formant5Ratio, 12);
    }

    [Fact]
    public void EnglishPreset_FollowsTheData()
    {
        // men "heed" /i/: F 343 2323 3001 Hz; F4 3500, F5 4500; levels from the cascade model.
        OperatorFilter heed = FormantPresets.Filter(English, "men", "iy", null);
        Assert.Equal(343.0, heed.FixedHz);
        Assert.Equal(343.0 / 80.0, heed.Q, 12);
        double[] f = [343, 2323, 3001, 3500, 4500];
        double[] model = Cascade(f, SpeechBandwidths);
        for (int n = 1; n < 5; n++)
        {
            FormantPeak peak = heed.FormantPeaks[n - 1];
            Assert.Equal(f[n] / 343.0, peak.Ratio, 12);
            Assert.Equal(f[n] / SpeechBandwidths[n], peak.Q, 12);
            Assert.Equal(Math.Min(1.0, model[n - 1]), peak.Level, 12);
        }

        // children "hod" /ɑ/: F 1000 1689 2945 Hz; F4 4600, F5 5600.
        OperatorFilter hod = FormantPresets.Filter(English, "children", "ah", null);
        Assert.Equal(1000.0, hod.FixedHz);
        Assert.Equal(1.689, hod.FormantRatio, 12);
        Assert.Equal(2.945, hod.Formant3Ratio, 12);
        Assert.Equal(4.6, hod.Formant4Ratio, 12);
        Assert.Equal(5.6, hod.Formant5Ratio, 12);
        Assert.Equal(12.5, hod.Q, 12);
    }

    [Fact]
    public void F4F5_FollowTheRules()
    {
        // The defaults of each group of speakers.
        foreach ((FormantPresetSet set, string speaker, double f4, double f5) in new[] { (English, "men", 3500.0, 4500.0), (Dutch, "men", 3500.0, 4500.0), (English, "women", 4100.0, 5000.0), (Dutch, "women", 4100.0, 5000.0), (English, "children", 4600.0, 5600.0) })
        {
            foreach (FormantPresetVowel vowel in set.Vowels)
            {
                OperatorFilter filter = FormantPresets.Filter(set, speaker, vowel.Key, null);
                Assert.Equal(f4, filter.Formant4Ratio * filter.FixedHz, 9);
                Assert.Equal(f5, filter.Formant5Ratio * filter.FixedHz, 9);
            }
        }

        // No measured F3 comes within 300 Hz of these; the rule raises F4 (and then F5) where one would.
        Assert.Equal((3500.0, 4500.0), FormantPresets.HigherFormants(3001, 3500, 4500));
        Assert.Equal((3700.0, 4500.0), FormantPresets.HigherFormants(3400, 3500, 4500));
        Assert.Equal((4600.0, 4900.0), FormantPresets.HigherFormants(4300, 3500, 4500));
    }

    [Fact]
    public void CascadeLevels_FallWithFrequency_AndAreCapped()
    {
        // Every level of every speech preset is at most F1's.
        foreach (FormantPresetSet set in new[] { English, Dutch })
        {
            foreach (string speaker in set.Speakers)
            {
                foreach (FormantPresetVowel vowel in set.Vowels)
                {
                    Assert.All(FormantPresets.Filter(set, speaker, vowel.Key, null).FormantPeaks, static p => Assert.InRange(p.Level, 0.0, 1.0));
                }
            }
        }

        // A front vowel: F2 far above F1 is much weaker than F1, and F4 and F5 fall further.
        OperatorFilter heed = FormantPresets.Filter(English, "women", "iy", null);
        Assert.True(heed.FormantLevel < 0.5, $"F2 level {heed.FormantLevel}");
        Assert.True(heed.Formant5Level < heed.Formant4Level, "F5 below F4");
        Assert.True(heed.Formant4Level < heed.Formant3Level, "F4 below F3");

        // The model itself, as the code computes it, against the reference.
        double[] f = [437, 2761, 3373, 4100, 5000];
        Assert.Equal(Cascade(f, SpeechBandwidths), FormantPresets.CascadeLevels(f, SpeechBandwidths), new ToleranceComparer(1e-12));

        // A narrow F2 close above a broad F1 comes out louder than F1 in the model; the preset caps it at 1.
        double[] close = [300, 360, 2500, 3500, 4500];
        double[] wide = [300, 20, 150, 250, 300];
        Assert.True(FormantPresets.CascadeLevels(close, wide)[0] > 1.0);
        Assert.Equal(1.0, FormantPresets.CappedLevel(FormantPresets.CascadeLevels(close, wide)[0]));
    }

    [Fact]
    public void Preset_KeepsTheSweep()
    {
        var sweep = OperatorEnvelope.Adsr(0.005, 0.3, 0.0, 0.2);
        OperatorFilter swept = OperatorFilter.FormantAt(500, 2.0, 0.4, 6) with { EnvelopeOctaves = 1.5, Envelope = sweep };
        OperatorFilter preset = FormantPresets.Filter(Sung, "alto", "e", swept);
        Assert.Equal(1.5, preset.EnvelopeOctaves);
        Assert.Same(sweep, preset.Envelope);
        Assert.Equal(400.0, preset.FixedHz);
        Assert.Equal(FormantPresets.Filter(Sung, "alto", "e", null) with { EnvelopeOctaves = 1.5, Envelope = sweep }, preset);

        // Through the edit, on a low-pass with a sweep: it becomes a formant filter and keeps the sweep.
        OperatorMatrixPatch lowPass = Carrier(OperatorFilter.LowPass(3) with { EnvelopeOctaves = -2.0, Envelope = sweep });
        var editor = new PatchEditor(lowPass, Fmx6);
        EditOutcome outcome = editor.Apply(PatchEdits.SetFormantPreset(0, Dutch, "women", "aa"));
        Assert.False(outcome.IsRefused, outcome.Refusal);
        OperatorFilter filter = editor.Current.Operators[0].Filter!;
        Assert.Equal(OperatorFilterType.Formant, filter.Type);
        Assert.Equal(-2.0, filter.EnvelopeOctaves);
        Assert.Same(sweep, filter.Envelope);
        Assert.Equal(986.0, filter.FixedHz);

        // The rest of the operator stays as it was, but for the voice source the saw became.
        Assert.Equal(VoiceSourceCarrier(OperatorFilter.LowPass(3)).Operators[0] with { Filter = null, AmplitudeEnvelope = lowPass.Operators[0].AmplitudeEnvelope }, editor.Current.Operators[0] with { Filter = null });

        // Without a filter, the preset gives the operator one, without a sweep.
        var bare = new PatchEditor(Carrier(), Fmx6);
        Assert.False(bare.Apply(PatchEdits.SetFormantPreset(0, English, "children", "uw")).IsRefused);
        Assert.Equal(FormantPresets.Filter(English, "children", "uw", null), bare.Current.Operators[0].Filter);
    }

    [Fact]
    public void Preset_IsOneUndoStep()
    {
        OperatorMatrixPatch original = Carrier(OperatorFilter.FormantAt(700, 1.8, 0.5, 8));
        var editor = new PatchEditor(original, Fmx6);
        Assert.False(editor.Apply(PatchEdits.SetFormantPreset(0, Sung, "soprano", "o")).IsRefused);
        Assert.True(editor.CanUndo);
        Assert.Equal(450.0, editor.Current.Operators[0].Filter!.FixedHz);

        editor.Undo();
        Assert.False(editor.CanUndo);
        Assert.Equal(original.ComputeHash(), editor.Current.ComputeHash());

        editor.Redo();
        Assert.Equal(450.0, editor.Current.Operators[0].Filter!.FixedHz);

        // The same preset again is no step at all.
        Assert.False(editor.Apply(PatchEdits.SetFormantPreset(0, Sung, "soprano", "o")).IsRefused);
        editor.Undo();
        Assert.False(editor.CanUndo);

        // A missing operator, an unknown speaker or vowel is refused, with no step.
        Assert.True(editor.Apply(PatchEdits.SetFormantPreset(6, Sung, "soprano", "o")).IsRefused);
        Assert.True(editor.Apply(PatchEdits.SetFormantPreset(0, Dutch, "children", "ie")).IsRefused);
        Assert.True(editor.Apply(PatchEdits.SetFormantPreset(0, English, "men", "piet")).IsRefused);
        Assert.False(editor.CanUndo);
        Assert.Throws<ArgumentException>(() => FormantPresets.Filter(Dutch, "children", "ie", null));
        Assert.Throws<ArgumentException>(() => FormantPresets.Filter(new FormantPresetSet("Sung", "", ["soprano"], [new("a", "a", "/a/")]), "soprano", "a", null));
    }

    [Fact]
    public void Preset_OnAFormantOperator_LeavesTheSourceAlone()
    {
        // A saw, and a pulse of a tuned width, that have a formant filter: a preset changes only the formants.
        OperatorMatrixPatch saw = Carrier(OperatorFilter.FormantAt(700, 1.8, 0.5, 8));
        OperatorMatrixPatch pulse = saw.WithOperator(0, saw.Operators[0].WithWaveform(OperatorWaveform.Pulse) with { Shape = 0.3, HarmonicLimit = 16 });
        foreach (OperatorMatrixPatch patch in new[] { saw, pulse })
        {
            var editor = new PatchEditor(patch, Fmx6);
            Assert.False(editor.Apply(PatchEdits.SetFormantPreset(0, Sung, "bass", "a")).IsRefused);
            OperatorFilter preset = FormantPresets.Filter(Sung, "bass", "a", patch.Operators[0].Filter);
            Assert.Equal(patch.WithOperator(0, patch.Operators[0] with { Filter = preset }).ComputeHash(), editor.Current.ComputeHash());
        }
    }

    [Fact]
    public void Preset_OnAnotherFilter_MakesAVoiceSource()
    {
        // A saw through a low-pass, and one without a filter: the preset makes it the voice source in the same step.
        foreach (OperatorFilter? before in new[] { OperatorFilter.LowPass(3), null })
        {
            OperatorMatrixPatch patch = Carrier(before);
            var editor = new PatchEditor(patch, Fmx6);
            Assert.False(editor.Apply(PatchEdits.SetFormantPreset(0, English, "women", "ae")).IsRefused);
            OperatorFilter preset = FormantPresets.Filter(English, "women", "ae", before);
            Assert.Equal(VoiceSourceCarrier(preset).ComputeHash(), editor.Current.ComputeHash());

            editor.Undo();
            Assert.Equal(patch.ComputeHash(), editor.Current.ComputeHash());
            Assert.False(editor.CanUndo);
        }

        // Noise stays noise: a whispered vowel.
        OperatorMatrixPatch noise = Carrier(OperatorFilter.LowPass(3));
        noise = noise.WithOperator(0, noise.Operators[0].WithWaveform(OperatorWaveform.FilteredNoise) with { Shape = 0.7 });
        var whisper = new PatchEditor(noise, Fmx6);
        Assert.False(whisper.Apply(PatchEdits.SetFormantPreset(0, Dutch, "men", "aa")).IsRefused);
        Assert.Equal(noise.WithOperator(0, noise.Operators[0] with { Filter = FormantPresets.Filter(Dutch, "men", "aa", noise.Operators[0].Filter) }).ComputeHash(), whisper.Current.ComputeHash());
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
            EditOutcome outcome = editor.Apply(PatchEdits.SetFormantPreset(0, Sung, "soprano", "a"));
            Assert.True(outcome.IsRefused);
            Assert.Equal("This profile has no operator filters.", outcome.Refusal);
            Assert.Equal(sine.ComputeHash(), editor.Current.ComputeHash());
            Assert.False(editor.CanUndo);
        }
    }

    /// <summary>
    /// The non-empty lines of the first fenced block under the heading that contains <paramref name="heading"/>; other
    /// blocks of the spec are left out.
    /// </summary>
    private static List<string> FencedBlock(string path, string heading)
    {
        var rows = new List<string>();
        bool underHeading = false, inBlock = false;
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#'))
            {
                if (inBlock)
                {
                    break;
                }

                underHeading = line.Contains(heading, StringComparison.Ordinal);
                continue;
            }

            if (underHeading && line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inBlock)
                {
                    break;
                }

                inBlock = true;
                continue;
            }

            if (inBlock && !string.IsNullOrWhiteSpace(line))
            {
                rows.Add(line);
            }
        }

        return rows;
    }

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance * Math.Max(1.0, Math.Abs(x));

        public int GetHashCode(double obj) => 0;
    }

    /// <summary>A test that reads the design spec of the five formants; skipped when the spec is not found.</summary>
    private sealed class SpecFactAttribute : FactAttribute
    {
        public SpecFactAttribute()
        {
            if (SpecPath() is null)
            {
                Skip = "The design spec docs/superpowers/specs/2026-10-19-five-formants-design.md was not found above the test folder.";
            }
        }

        /// <summary>The spec, found from the test's folder upwards; null when it is not there.</summary>
        public static string? SpecPath()
        {
            string spec = Path.Combine("docs", "superpowers", "specs", "2026-10-19-five-formants-design.md");
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                string path = Path.Combine(directory.FullName, spec);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }
    }
}
