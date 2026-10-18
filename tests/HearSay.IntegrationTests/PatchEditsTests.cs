using HearSay.Editing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>The structural edits of the patch editor: adding and removing parts, refusals at the profile limits; fast, no rendering.</summary>
public sealed class PatchEditsTests
{
    private static readonly SynthesisProfile Fm7 = SynthesisProfiles.SuperColliderFm7;
    private static readonly SynthesisProfile Fmx6 = SynthesisProfiles.SuperColliderFmx6;
    private static readonly SynthesisProfile Nome6 = SynthesisProfiles.SuperColliderNome6;

    /// <summary>OP2 modulates OP1 by 2 rad; OP1 is heard; OP3 to OP6 are free.</summary>
    private static OperatorMatrixPatch TwoOperators()
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.01, 0.4, 0.3, 0.1)))
            .WithOutputLevel(0, 1.0)
            .WithEdge(new ModulationEdge(1, 0, true, 2.0));

    private static OperatorMatrixPatch WithOperator(OperatorMatrixPatch patch, int op, Func<OperatorPatch, OperatorPatch> change)
        => patch.WithOperator(op, change(patch.Operators[op]));

    /// <summary>Applies an edit through an editor and checks that the outcome is the editor's current patch.</summary>
    private static EditOutcome Apply(OperatorMatrixPatch patch, SynthesisProfile profile, PatchEdit edit)
    {
        var editor = new PatchEditor(patch, profile);
        string before = patch.ComputeHash();
        EditOutcome outcome = editor.Apply(edit);
        Assert.Equal(before, editor.Original.ComputeHash());
        Assert.Equal(outcome.Patch.ComputeHash(), editor.Current.ComputeHash());
        if (outcome.IsRefused)
        {
            Assert.False(string.IsNullOrWhiteSpace(outcome.Refusal));
            Assert.Equal(before, editor.Current.ComputeHash());
        }

        return outcome;
    }

    private static OperatorMatrixPatch Done(OperatorMatrixPatch patch, SynthesisProfile profile, PatchEdit edit)
    {
        EditOutcome outcome = Apply(patch, profile, edit);
        Assert.False(outcome.IsRefused, outcome.Refusal);
        return outcome.Patch;
    }

    private static string Refused(OperatorMatrixPatch patch, SynthesisProfile profile, PatchEdit edit)
    {
        EditOutcome outcome = Apply(patch, profile, edit);
        Assert.True(outcome.IsRefused);
        return outcome.Refusal!;
    }

    [Fact]
    public void AddModulator_TakesTheFirstFreeSlot_WithTheDefaults()
    {
        OperatorMatrixPatch patch = TwoOperators();
        OperatorMatrixPatch edited = Done(patch, Fmx6, PatchEdits.AddModulator(0));

        OperatorPatch added = edited.Operators[2];
        Assert.True(added.Enabled);
        Assert.Equal(OperatorWaveform.Sine, added.Waveform);
        Assert.Equal(OperatorFrequency.AtRatio(1.0), added.Frequency);
        Assert.Equal(OperatorEnvelope.Adsr(0.005, 0.3, 0.2, 0.2).Points, added.AmplitudeEnvelope.Points);
        Assert.Equal(2, added.AmplitudeEnvelope.SustainPoint);
        Assert.Null(added.Filter);
        Assert.Equal(0.0, edited.OutputLevels[2]);
        Assert.Equal(new ModulationEdge(2, 0, true, 1.0), edited.ModulationMatrix.Find(2, 0));
        Assert.Equal(2, edited.ModulationMatrix.Edges.Count);
        Assert.All(edited.Operators.Skip(3), static op => Assert.False(op.Enabled));

        // The next one goes to OP4, and can modulate the new modulator.
        OperatorMatrixPatch again = Done(edited, Fmx6, PatchEdits.AddModulator(2));
        Assert.True(again.Operators[3].Enabled);
        Assert.Equal(1.0, again.ModulationMatrix.Find(3, 2)!.Amount);
    }

    [Fact]
    public void AddModulator_WithNoFreeSlot_IsRefused()
    {
        OperatorMatrixPatch patch = TwoOperators();
        for (int i = 2; i < 6; i++)
        {
            patch = patch.WithOperator(i, OperatorPatch.Sine(OperatorFrequency.AtRatio(i)));
        }

        string why = Refused(patch, Fmx6, PatchEdits.AddModulator(0));
        Assert.Contains("free", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveOperator_RemovesItsConnectionsOnly()
    {
        // OP3 -> OP2 -> OP1, OP2 feeds itself back, and OP3 also modulates OP1.
        OperatorMatrixPatch patch = TwoOperators()
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3)))
            .WithEdge(new ModulationEdge(2, 1, true, 1.0))
            .WithEdge(new ModulationEdge(1, 1, true, 0.5))
            .WithEdge(new ModulationEdge(2, 0, true, 0.5));

        OperatorMatrixPatch edited = Done(patch, Fmx6, PatchEdits.RemoveOperator(1));

        Assert.False(edited.Operators[1].Enabled);
        Assert.True(edited.Operators[2].Enabled);
        ModulationEdge left = Assert.Single(edited.ModulationMatrix.Edges);
        Assert.Equal(new ModulationEdge(2, 0, true, 0.5), left);
        Assert.Equal(patch.Operators[2], edited.Operators[2]);
    }

    [Fact]
    public void RemoveOperator_LastHeardCarrier_IsRefused()
    {
        OperatorMatrixPatch patch = TwoOperators();
        string why = Refused(patch, Fmx6, PatchEdits.RemoveOperator(0));
        Assert.Contains("OP1", why, StringComparison.Ordinal);

        // A modulator can go, which leaves a plain sine.
        OperatorMatrixPatch edited = Done(patch, Fmx6, PatchEdits.RemoveOperator(1));
        Assert.Empty(edited.ModulationMatrix.Edges);

        // With a second heard carrier, the first can go.
        OperatorMatrixPatch twoCarriers = patch.WithOutputLevel(1, 0.5);
        OperatorMatrixPatch one = Done(twoCarriers, Fmx6, PatchEdits.RemoveOperator(0));
        Assert.False(one.Operators[0].Enabled);
        Assert.Empty(one.ModulationMatrix.Edges);
    }

    [Fact]
    public void Connect_SelfFeedback()
    {
        OperatorMatrixPatch edited = Done(TwoOperators(), Fmx6, PatchEdits.Connect(1, 1, 0.5));
        Assert.Equal(new ModulationEdge(1, 1, true, 0.5), edited.ModulationMatrix.Find(1, 1));

        OperatorMatrixPatch upwards = Done(TwoOperators(), Fmx6, PatchEdits.Connect(0, 1));
        Assert.Equal(new ModulationEdge(0, 1, true, 1.0), upwards.ModulationMatrix.Find(0, 1));

        // Already connected, or to a free slot: refused.
        Refused(TwoOperators(), Fmx6, PatchEdits.Connect(1, 0));
        Refused(TwoOperators(), Fmx6, PatchEdits.Connect(2, 0));
    }

    [Fact]
    public void Connect_OverTheModulationLimit_IsRefused()
    {
        // OP1 receives 2 rad from OP2; the limit is 24.
        Assert.Equal(24.0, Fmx6.Capabilities.MaxModulationRadiansPerDestination);
        string why = Refused(TwoOperators(), Fmx6, PatchEdits.Connect(0, 0, 22.5));
        Assert.Contains("OP1", why, StringComparison.Ordinal);
        Assert.Contains("24", why, StringComparison.Ordinal);

        OperatorMatrixPatch edited = Done(TwoOperators(), Fmx6, PatchEdits.Connect(0, 0, 22.0));
        Assert.Equal(22.0, edited.ModulationMatrix.Find(0, 0)!.Amount);

        // A new modulator adds 1 rad: refused when that goes over the limit.
        Refused(edited.WithEdge(new ModulationEdge(0, 0, true, 21.5)), Fmx6, PatchEdits.AddModulator(0));
    }

    [Fact]
    public void SetWaveform_OnFm7_IsRefused()
    {
        Assert.Equal([OperatorWaveform.Sine], PatchEdits.WaveformsFor(Fm7.Capabilities));
        Assert.Equal([OperatorWaveform.Sine, OperatorWaveform.Noise, OperatorWaveform.Elliptic], PatchEdits.WaveformsFor(Nome6.Capabilities));
        Assert.Equal(8, PatchEdits.WaveformsFor(Fmx6.Capabilities).Count);

        string why = Refused(TwoOperators(), Fm7, PatchEdits.SetWaveform(1, OperatorWaveform.Saw));
        Assert.Contains("saw", why, StringComparison.OrdinalIgnoreCase);
        Refused(TwoOperators(), Fmx6, PatchEdits.SetWaveform(1, OperatorWaveform.Elliptic));

        OperatorMatrixPatch saw = Done(TwoOperators(), Fmx6, PatchEdits.SetWaveform(1, OperatorWaveform.Saw));
        Assert.Equal(OperatorWaveform.Saw, saw.Operators[1].Waveform);

        OperatorMatrixPatch elliptic = Done(TwoOperators(), Nome6, PatchEdits.SetWaveform(0, OperatorWaveform.Elliptic));
        Assert.Equal(OperatorPatch.DefaultEllipticM, elliptic.Operators[0].Shape);
    }

    [Fact]
    public void SetFrequencyMode_KeepsThePitchAtA4()
    {
        OperatorMatrixPatch patch = TwoOperators();
        double before = patch.Operators[1].FrequencyHz(440.0);

        OperatorMatrixPatch fixedHz = Done(patch, Fmx6, PatchEdits.SetFrequencyMode(1, OperatorFrequencyMode.Fixed));
        Assert.Equal(OperatorFrequencyMode.Fixed, fixedHz.Operators[1].Frequency.Mode);
        Assert.Equal(880.0, fixedHz.Operators[1].Frequency.FixedFrequencyHz, 9);
        Assert.Equal(before, fixedHz.Operators[1].FrequencyHz(440.0), 9);

        OperatorMatrixPatch back = Done(fixedHz, Fmx6, PatchEdits.SetFrequencyMode(1, OperatorFrequencyMode.Ratio));
        Assert.Equal(OperatorFrequencyMode.Ratio, back.Operators[1].Frequency.Mode);
        Assert.Equal(2.0, back.Operators[1].Frequency.Ratio, 9);

        OperatorMatrixPatch at330 = WithOperator(patch, 1, static op => op with { Frequency = OperatorFrequency.AtFixed(330) });
        Assert.Equal(0.75, Done(at330, Fmx6, PatchEdits.SetFrequencyMode(1, OperatorFrequencyMode.Ratio)).Operators[1].Frequency.Ratio, 9);
    }

    [Fact]
    public void SetFilter_AddChangeRemove()
    {
        Assert.True(PatchEdits.HasFilters(Fmx6.Capabilities));

        OperatorMatrixPatch lowPass = Done(TwoOperators(), Fmx6, PatchEdits.SetFilter(0, OperatorFilterType.LowPass));
        Assert.Equal(OperatorFilter.LowPass(4), lowPass.Operators[0].Filter);

        // A changed cutoff is kept by the types that follow the note.
        OperatorMatrixPatch at3 = WithOperator(lowPass, 0, static op => op with { Filter = op.Filter! with { CutoffRatio = 3.0 } });
        OperatorMatrixPatch highPass = Done(at3, Fmx6, PatchEdits.SetFilter(0, OperatorFilterType.HighPass));
        Assert.Equal(OperatorFilter.HighPass(3.0), highPass.Operators[0].Filter);

        OperatorMatrixPatch peak = Done(highPass, Fmx6, PatchEdits.SetFilter(0, OperatorFilterType.Peak));
        Assert.Equal(OperatorFilter.PeakAt(1000, 6, 2), peak.Operators[0].Filter);

        // Peak and formant share the frequency in hertz.
        OperatorMatrixPatch peakAt500 = WithOperator(peak, 0, static op => op with { Filter = op.Filter! with { FixedHz = 500.0 } });
        OperatorMatrixPatch formant = Done(peakAt500, Fmx6, PatchEdits.SetFilter(0, OperatorFilterType.Formant));
        Assert.Equal(OperatorFilter.FormantAt(500, 1.8, 0.5, 8), formant.Operators[0].Filter);

        OperatorMatrixPatch fresh = Done(TwoOperators(), Fmx6, PatchEdits.SetFilter(0, OperatorFilterType.Formant));
        Assert.Equal(OperatorFilter.FormantAt(700, 1.8, 0.5, 8), fresh.Operators[0].Filter);

        OperatorMatrixPatch bandPass = Done(formant, Fmx6, PatchEdits.SetFilter(0, OperatorFilterType.BandPass));
        Assert.Equal(OperatorFilterType.BandPass, bandPass.Operators[0].Filter!.Type);
        Assert.Equal(4.0, bandPass.Operators[0].Filter!.CutoffRatio);

        OperatorMatrixPatch removed = Done(formant, Fmx6, PatchEdits.SetFilter(0, null));
        Assert.Null(removed.Operators[0].Filter);
        Assert.Equal(TwoOperators().ComputeHash(), removed.ComputeHash());
    }

    [Fact]
    public void SetFilter_OnFm7_IsRefused()
    {
        Assert.False(PatchEdits.HasFilters(Fm7.Capabilities));
        Assert.False(PatchEdits.HasFilters(Nome6.Capabilities));
        string why = Refused(TwoOperators(), Fm7, PatchEdits.SetFilter(0, OperatorFilterType.LowPass));
        Assert.Contains("filter", why, StringComparison.OrdinalIgnoreCase);
        Refused(TwoOperators(), Nome6, PatchEdits.SetFilter(0, OperatorFilterType.Peak));
    }

    [Fact]
    public void SetSweep_OnAndOff()
    {
        // No filter, no sweep.
        Refused(TwoOperators(), Fmx6, PatchEdits.SetSweep(0, true));

        OperatorMatrixPatch filtered = Done(TwoOperators(), Fmx6, PatchEdits.SetFilter(0, OperatorFilterType.LowPass));
        OperatorMatrixPatch swept = Done(filtered, Fmx6, PatchEdits.SetSweep(0, true));
        OperatorFilter filter = swept.Operators[0].Filter!;
        Assert.Equal(2.0, filter.EnvelopeOctaves);
        Assert.Equal(OperatorEnvelope.Adsr(0.005, 0.3, 0.0, 0.2).Points, filter.Envelope!.Points);

        // The sweep survives a change of filter type.
        OperatorMatrixPatch peak = Done(swept, Fmx6, PatchEdits.SetFilter(0, OperatorFilterType.Peak));
        Assert.NotNull(peak.Operators[0].Filter!.Envelope);

        OperatorMatrixPatch off = Done(swept, Fmx6, PatchEdits.SetSweep(0, false));
        Assert.Equal(filtered.ComputeHash(), off.ComputeHash());
    }

    [Fact]
    public void SetConnectionEnvelope_OnAndOff()
    {
        Refused(TwoOperators(), Fmx6, PatchEdits.SetConnectionEnvelope(0, 1, true));

        OperatorMatrixPatch on = Done(TwoOperators(), Fmx6, PatchEdits.SetConnectionEnvelope(1, 0, true));
        ModulationEdge edge = on.ModulationMatrix.Find(1, 0)!;
        Assert.Equal(2.0, edge.Amount);
        Assert.Equal(OperatorEnvelope.Adsr(0.005, 0.3, 0.5, 0.2).Points, edge.AmountEnvelope!.Points);

        OperatorMatrixPatch off = Done(on, Fmx6, PatchEdits.SetConnectionEnvelope(1, 0, false));
        Assert.Equal(TwoOperators().ComputeHash(), off.ComputeHash());
    }

    [Fact]
    public void SetPitchEnvelope_And_SetVibrato_OnAndOff()
    {
        OperatorMatrixPatch pitched = Done(TwoOperators(), Fm7, PatchEdits.SetPitchEnvelope(true));
        OperatorEnvelope envelope = pitched.Pitch.PitchEnvelope!;
        Assert.Equal([new EnvelopePoint(0, 0), new EnvelopePoint(0.1, 0)], envelope.Points);
        Assert.Null(envelope.SustainPoint);
        Assert.Empty(envelope.Validate());

        OperatorMatrixPatch both = Done(pitched, Fm7, PatchEdits.SetVibrato(true));
        Assert.Equal(new LfoSettings(5, 10), both.Pitch.Vibrato);
        Assert.Same(envelope, both.Pitch.PitchEnvelope);

        OperatorMatrixPatch vibratoOnly = Done(both, Fm7, PatchEdits.SetPitchEnvelope(false));
        Assert.Null(vibratoOnly.Pitch.PitchEnvelope);
        Assert.NotNull(vibratoOnly.Pitch.Vibrato);

        OperatorMatrixPatch none = Done(vibratoOnly, Fm7, PatchEdits.SetVibrato(false));
        Assert.True(none.Pitch.IsStatic);
        Assert.Equal(TwoOperators().ComputeHash(), none.ComputeHash());
    }

    [Fact]
    public void SetRoughness_OnNome6_And_RefusedOnFmx6()
    {
        Assert.True(PatchEdits.HasSharedNoise(Nome6.Capabilities));
        Assert.False(PatchEdits.HasSharedNoise(Fmx6.Capabilities));

        OperatorMatrixPatch rough = Done(TwoOperators(), Nome6, PatchEdits.SetRoughness(0, true));
        Assert.Equal(0.3, rough.Operators[0].NoisePhase);
        Assert.Equal(0.0, rough.Operators[0].NoiseM);

        OperatorMatrixPatch elliptic = Done(TwoOperators(), Nome6, PatchEdits.SetWaveform(1, OperatorWaveform.Elliptic));
        OperatorMatrixPatch roughElliptic = Done(elliptic, Nome6, PatchEdits.SetRoughness(1, true));
        Assert.Equal(0.3, roughElliptic.Operators[1].NoisePhase);
        Assert.Equal(0.1, roughElliptic.Operators[1].NoiseM);

        OperatorMatrixPatch smooth = Done(roughElliptic, Nome6, PatchEdits.SetRoughness(1, false));
        Assert.Equal(elliptic.ComputeHash(), smooth.ComputeHash());

        string why = Refused(TwoOperators(), Fmx6, PatchEdits.SetRoughness(0, true));
        Assert.Contains("noise", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RandomEdits_AlwaysLeaveAPlayablePatch()
    {
        foreach (SynthesisProfile profile in new[] { Fm7, Fmx6, Nome6 })
        {
            SynthesisCapabilities capabilities = profile.Capabilities;
            var generator = new RandomPatchGenerator(capabilities);
            int done = 0;
            int refused = 0;
            for (int seed = 0; seed < 200; seed++)
            {
                var editor = new PatchEditor(generator.Generate(seed), profile);
                var random = new Random(seed);
                for (int step = 0; step < 20; step++)
                {
                    OperatorMatrixPatch before = editor.Current;
                    string beforeHash = before.ComputeHash();
                    (string name, PatchEdit edit) = RandomEdit(random, before, capabilities.OperatorCount);

                    // The edit itself refuses what the profile cannot play, before the editor's own check.
                    EditOutcome direct = edit(before, capabilities);
                    string context = $"{profile.Name} seed {seed} step {step}: {name}";
                    if (!direct.IsRefused)
                    {
                        PatchCompatibilityResult own = PatchCompatibilityValidator.Instance.Validate(direct.Patch, capabilities);
                        Assert.True(own.IsCompatible, $"{context}: {own}");
                    }

                    EditOutcome outcome = editor.Apply(edit);
                    Assert.Equal(direct.IsRefused, outcome.IsRefused);
                    Assert.Empty(editor.Current.Validate());
                    PatchCompatibilityResult result = PatchCompatibilityValidator.Instance.Validate(editor.Current, capabilities);
                    Assert.True(result.IsCompatible, $"{context}: {result}");
                    if (outcome.IsRefused)
                    {
                        Assert.Equal(beforeHash, editor.Current.ComputeHash());
                        refused++;
                    }
                    else
                    {
                        done++;
                    }
                }
            }

            // Both kinds of outcome occur, so the test exercises the edits and their refusals.
            Assert.True(done > 1000, $"{profile.Name}: {done} edits done");
            Assert.True(refused > 100, $"{profile.Name}: {refused} edits refused");
        }
    }

    private static (string Name, PatchEdit Edit) RandomEdit(Random random, OperatorMatrixPatch patch, int n)
    {
        int Op() => random.Next(n);
        bool On() => random.Next(2) == 0;

        // Disconnecting and envelopes mostly pick a connection that exists.
        (int Source, int Destination) Pair()
        {
            IReadOnlyList<ModulationEdge> edges = patch.ModulationMatrix.Edges;
            if (edges.Count > 0 && random.Next(4) != 0)
            {
                ModulationEdge edge = edges[random.Next(edges.Count)];
                return (edge.SourceOperator, edge.DestinationOperator);
            }

            return (Op(), Op());
        }

        OperatorWaveform[] waveforms = Enum.GetValues<OperatorWaveform>();
        switch (random.Next(12))
        {
            case 0:
            {
                int target = Op();
                return ($"AddModulator({target})", PatchEdits.AddModulator(target));
            }

            case 1:
            {
                int op = Op();
                return ($"RemoveOperator({op})", PatchEdits.RemoveOperator(op));
            }

            case 2:
            {
                int source = Op();
                int destination = Op();
                double amount = Math.Round(random.NextDouble() * 12.0, 2);
                return ($"Connect({source}, {destination}, {amount})", PatchEdits.Connect(source, destination, amount));
            }

            case 3:
            {
                (int source, int destination) = Pair();
                return ($"Disconnect({source}, {destination})", PatchEdits.Disconnect(source, destination));
            }

            case 4:
            {
                int op = Op();
                OperatorWaveform waveform = waveforms[random.Next(waveforms.Length)];
                return ($"SetWaveform({op}, {waveform})", PatchEdits.SetWaveform(op, waveform));
            }

            case 5:
            {
                int op = Op();
                OperatorFrequencyMode mode = On() ? OperatorFrequencyMode.Ratio : OperatorFrequencyMode.Fixed;
                return ($"SetFrequencyMode({op}, {mode})", PatchEdits.SetFrequencyMode(op, mode));
            }

            case 6:
            {
                int op = Op();
                int choice = random.Next(7);
                OperatorFilterType? type = choice == 6 ? null : (OperatorFilterType)choice;
                return ($"SetFilter({op}, {type})", PatchEdits.SetFilter(op, type));
            }

            case 7:
            {
                int op = Op();
                bool on = On();
                return ($"SetSweep({op}, {on})", PatchEdits.SetSweep(op, on));
            }

            case 8:
            {
                (int source, int destination) = Pair();
                bool on = On();
                return ($"SetConnectionEnvelope({source}, {destination}, {on})", PatchEdits.SetConnectionEnvelope(source, destination, on));
            }

            case 9:
            {
                bool on = On();
                return ($"SetPitchEnvelope({on})", PatchEdits.SetPitchEnvelope(on));
            }

            case 10:
            {
                bool on = On();
                return ($"SetVibrato({on})", PatchEdits.SetVibrato(on));
            }

            default:
            {
                int op = Op();
                bool on = On();
                return ($"SetRoughness({op}, {on})", PatchEdits.SetRoughness(op, on));
            }
        }
    }
}
