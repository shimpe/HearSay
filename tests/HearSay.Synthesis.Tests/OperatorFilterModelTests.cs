using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Synthesis.Tests;

public sealed class OperatorFilterModelTests
{
    private static OperatorMatrixPatch Carrier(OperatorFilter? filter, bool enabled = true) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Enabled = enabled, Filter = filter })
            .WithOutputLevel(0, 1.0);

    [Fact]
    public void PatchWithoutFilter_HasNoFilterInItsJson_AndKeepsItsHash()
    {
        OperatorMatrixPatch plain = ReferencePatches.Find("electric-piano")!.Patch;
        string json = plain.ToJson();
        Assert.DoesNotContain("\"filter\"", json, StringComparison.Ordinal);
        Assert.Equal(plain.ComputeHash(), OperatorMatrixPatch.FromJson(json).ComputeHash());
    }

    [Fact]
    public void Filter_RoundTripsThroughJson()
    {
        OperatorFilter filter = OperatorFilter.FormantAt(700, 1.8, 0.6, 8) with
        {
            EnvelopeOctaves = 1.5,
            Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1),
        };
        OperatorMatrixPatch patch = Carrier(filter);
        OperatorMatrixPatch back = OperatorMatrixPatch.FromJson(patch.ToJson());

        Assert.Contains("\"filter\"", patch.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("isKeyTracked", patch.ToJson(), StringComparison.Ordinal);
        Assert.Equal(patch.ComputeHash(), back.ComputeHash());
        Assert.Equal(OperatorFilterType.Formant, back.Operators[0].Filter!.Type);
        Assert.Equal(1.8, back.Operators[0].Filter!.FormantRatio);
    }

    [Fact]
    public void OldFormantPatch_IsByteIdentical()
    {
        // Captured from the two-formant model, before F2 had its own Q and before F3..F5 existed.
        const string FilterJson =
            "\"filter\":{\"type\":\"Formant\",\"poles\":2,\"cutoffRatio\":4,\"fixedHz\":700,\"q\":8,\"formantRatio\":1.8,\"formantLevel\":0.6,\"peakGainDb\":6,\"envelopeOctaves\":1.5,"
            + "\"envelope\":{\"points\":[{\"timeSeconds\":0,\"level\":0,\"curve\":\"Linear\"},{\"timeSeconds\":0.01,\"level\":1,\"curve\":\"Linear\"},{\"timeSeconds\":0.2,\"level\":0.3,\"curve\":\"Exponential\"},{\"timeSeconds\":0.1,\"level\":0,\"curve\":\"Exponential\"}],"
            + "\"sustainPoint\":2,\"releasePoint\":3,\"hasExponentialSegments\":true,\"releaseSeconds\":0.1}},\"harmonicLimit\":32}";
        const string Hash = "596E75144D9D980A0C002A8CB2D34A8B";
        OperatorMatrixPatch patch = Carrier(OperatorFilter.FormantAt(700, 1.8, 0.6, 8) with { EnvelopeOctaves = 1.5, Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1) });

        string json = patch.ToJson(indented: false);
        Assert.Contains(FilterJson, json, StringComparison.Ordinal);
        Assert.Equal(Hash, patch.ComputeHash());

        // Read back, the old JSON has the defaults of the new fields: F2 with the shared Q, F3..F5 off.
        OperatorFilter back = OperatorMatrixPatch.FromJson(json).Operators[0].Filter!;
        Assert.Equal(patch.Operators[0].Filter! with { Envelope = null }, back with { Envelope = null });
        Assert.Equal(json, OperatorMatrixPatch.FromJson(json).ToJson(indented: false));
        Assert.Equal([new FormantPeak(1.8, 0.6, 0), new FormantPeak(1, 0, 0), new FormantPeak(1, 0, 0), new FormantPeak(1, 0, 0)], back.FormantPeaks);
        Assert.False(back.UsesExtendedFormants);
    }

    [Fact]
    public void Formants_RoundTripThroughJson_AndOnlyTheSetFieldsAreWritten()
    {
        OperatorFilter filter = OperatorFilter.Formants(800, 41, new FormantPeak(1.4375, 0.5, 12.8), new FormantPeak(3.625, 0.025, 0), new FormantPeak(4.875, 0.1, 30));
        OperatorMatrixPatch patch = Carrier(filter);
        string json = patch.ToJson(indented: false);

        Assert.Contains("\"formantQ2\":12.8", json, StringComparison.Ordinal);
        Assert.Contains("\"formant3Ratio\":3.625,\"formant3Level\":0.025,\"formant4Ratio\":4.875,\"formant4Level\":0.1,\"formant4Q\":30", json, StringComparison.Ordinal);
        Assert.DoesNotContain("formant3Q", json, StringComparison.Ordinal);
        Assert.DoesNotContain("formant5", json, StringComparison.Ordinal);
        Assert.DoesNotContain("formantPeaks", json, StringComparison.Ordinal);
        Assert.DoesNotContain("usesExtendedFormants", json, StringComparison.Ordinal);

        OperatorMatrixPatch back = OperatorMatrixPatch.FromJson(json);
        Assert.Equal(filter, back.Operators[0].Filter);
        Assert.Equal(patch.ComputeHash(), back.ComputeHash());
        Assert.True(back.Operators[0].Filter!.UsesExtendedFormants);

        // A ratio away from 1 is written even when its peak is off, so that the patch keeps every value it has.
        OperatorFilter offPeak = OperatorFilter.FormantAt(700, 1.8, 0.5, 8) with { Formant5Ratio = 7 };
        string offJson = Carrier(offPeak).ToJson(indented: false);
        Assert.Contains("\"formant5Ratio\":7", offJson, StringComparison.Ordinal);
        Assert.Equal(offPeak, OperatorMatrixPatch.FromJson(offJson).Operators[0].Filter);
        Assert.False(offPeak.UsesExtendedFormants);
    }

    [Fact]
    public void Formants_ValidatesRangesAndOwnQ()
    {
        OperatorFilter five = OperatorFilter.Formants(800, 41, new FormantPeak(1.4375, 0.5, 12.8), new FormantPeak(3.625, 0.025, 24.2), new FormantPeak(4.875, 0.1, 30), new FormantPeak(6.1875, 0.003, 35.4));
        Assert.Empty(Carrier(five).Validate());
        Assert.Equal(800, five.FixedHz);
        Assert.Equal(41, five.Q);
        Assert.Equal(OperatorFilterType.Formant, five.Type);
        Assert.Equal(new FormantPeak(6.1875, 0.003, 35.4), five.FormantPeaks[3]);
        Assert.Equal((1.4375, 0.5, 12.8), (five.FormantRatio, five.FormantLevel, five.FormantQ2));
        Assert.Equal((3.625, 0.025, 24.2), (five.Formant3Ratio, five.Formant3Level, five.Formant3Q));

        // Peaks not given are off; FormantAt is the two-formant filter of before.
        OperatorFilter one = OperatorFilter.Formants(500, 6);
        Assert.Equal([new FormantPeak(1, 0, 0), new FormantPeak(1, 0, 0), new FormantPeak(1, 0, 0), new FormantPeak(1, 0, 0)], one.FormantPeaks);
        Assert.Equal(OperatorFilter.FormantAt(700, 1.8, 0.5, 8), OperatorFilter.Formants(700, 8, new FormantPeak(1.8, 0.5, 0)));
        Assert.Throws<ArgumentException>(() => OperatorFilter.Formants(700, 8, new(2, 1, 0), new(3, 1, 0), new(4, 1, 0), new(5, 1, 0), new(6, 1, 0)));

        // Formant Qs go to 50, 0 meaning "the same as Q" for the peaks; other types keep 20.
        Assert.Equal(50, OperatorFilter.MaxFormantQ);
        Assert.Equal(32, OperatorFilter.MaxFormantRatio);
        Assert.Empty(Carrier(OperatorFilter.FormantAt(700, 32, 0.5, 50)).Validate());
        Assert.Empty(Carrier(five with { FormantQ2 = 0, Formant3Q = 0, Formant4Q = 0.5, Formant5Q = 50 }).Validate());
        Assert.NotEmpty(Carrier(OperatorFilter.LowPass(4, q: 41)).Validate());
        Assert.NotEmpty(Carrier(OperatorFilter.BandPass(2, 20.5)).Validate());
        OperatorFilter[] invalid =
        [
            OperatorFilter.FormantAt(700, 1.8, 0.5, 51),
            OperatorFilter.FormantAt(700, 1.8, 0.5, 0),
            OperatorFilter.FormantAt(700, 33, 0.5, 8),
            five with { FormantQ2 = 0.3 },
            five with { FormantQ2 = 51 },
            five with { Formant3Ratio = 0.9 },
            five with { Formant3Ratio = 33 },
            five with { Formant3Level = -0.1 },
            five with { Formant3Q = -1 },
            five with { Formant4Level = 1.2 },
            five with { Formant4Q = 0.2 },
            five with { Formant5Ratio = double.NaN },
            five with { Formant5Q = 60 },
        ];
        Assert.All(invalid, f => Assert.Contains(Carrier(f).Validate(), p => p.StartsWith("OP1 filter", StringComparison.Ordinal)));
    }

    public static TheoryData<OperatorFilter> InvalidFilters => new()
    {
        OperatorFilter.LowPass(4) with { Poles = 3 },
        OperatorFilter.BandPass(2, 2) with { Poles = 4 },
        OperatorFilter.LowPass(0.1),
        OperatorFilter.LowPass(80),
        OperatorFilter.LowPass(4, q: 0.2),
        OperatorFilter.LowPass(4, q: 25),
        OperatorFilter.FormantAt(700, 0.8, 0.5, 8),
        OperatorFilter.FormantAt(700, 2, 1.5, 8),
        OperatorFilter.PeakAt(1000, 30, 2),
        OperatorFilter.PeakAt(5, 6, 2),
        OperatorFilter.LowPass(4) with { EnvelopeOctaves = 5 },
        OperatorFilter.LowPass(double.NaN),
    };

    [Theory]
    [MemberData(nameof(InvalidFilters))]
    public void OutOfRangeFilter_IsNotWellFormed(OperatorFilter filter) =>
        Assert.Contains(Carrier(filter).Validate(), p => p.StartsWith("OP1 filter", StringComparison.Ordinal));

    [Fact]
    public void ValidFilters_AreWellFormed()
    {
        Assert.Empty(Carrier(OperatorFilter.LowPass(4, 2, 4)).Validate());
        Assert.Empty(Carrier(OperatorFilter.HighPass(0.5)).Validate());
        Assert.Empty(Carrier(OperatorFilter.Notch(3, 4)).Validate());
        Assert.Empty(Carrier(OperatorFilter.PeakAt(1200, -12, 3)).Validate());
        Assert.Empty(Carrier(OperatorFilter.FormantAt(300, 7.5, 0.2, 12) with { EnvelopeOctaves = -4, Envelope = OperatorEnvelope.Adsr(0.01, 0.1, 0.5, 0.1) }).Validate());
    }

    [Fact]
    public void OnlyFmx6_PlaysFilters_ButADisabledOperatorsFilterDoesNotMatter()
    {
        OperatorMatrixPatch filtered = Carrier(OperatorFilter.LowPass(4));
        Assert.True(PatchCompatibilityValidator.Instance.Validate(filtered, SynthesisProfiles.SuperColliderFmx6.Capabilities).IsCompatible);
        foreach (SynthesisProfile profile in new[] { SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderNome6 })
        {
            PatchCompatibilityResult result = PatchCompatibilityValidator.Instance.Validate(filtered, profile.Capabilities);
            Assert.Contains(result.Issues, i => i.Code == PatchCompatibilityCodes.OperatorFilter);

            // Review focus 4: what cannot be heard does not have to be representable.
            OperatorMatrixPatch disabled = Carrier(OperatorFilter.LowPass(4), enabled: false)
                .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)))
                .WithOutputLevel(1, 1.0);
            Assert.True(PatchCompatibilityValidator.Instance.Validate(disabled, profile.Capabilities).IsCompatible);
        }

        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        Assert.True(fmx6.SupportsOperatorFilters);
        Assert.False(fmx6.IsSubsetOf(fmx6 with { SupportsOperatorFilters = false }));
        Assert.True((fmx6 with { SupportsOperatorFilters = false }).IsSubsetOf(fmx6));
        Assert.Equal(8, SynthesisProfiles.SuperColliderFmx6.StructureLibraryVersion);
        Assert.Equal(1, SynthesisProfiles.SuperColliderFm7.StructureLibraryVersion);
        Assert.Equal(2, SynthesisProfiles.SuperColliderNome6.StructureLibraryVersion);
    }

    [Fact]
    public void RandomFilters_LeaveTheRestOfThePatchAsTheSeedGaveIt()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var plain = new RandomPatchGenerator(fmx6, new RandomPatchOptions());
        var filtered = new RandomPatchGenerator(fmx6, new RandomPatchOptions { FilterProbability = 1.0 });
        int withFilter = 0;
        for (int seed = 0; seed < 20; seed++)
        {
            OperatorMatrixPatch a = plain.Generate(seed);
            OperatorMatrixPatch b = filtered.Generate(seed);
            Assert.Empty(b.Validate());
            Assert.True(PatchCompatibilityValidator.Instance.Validate(b, fmx6).IsCompatible);
            withFilter += b.Operators.Count(static o => o.Filter is not null);

            // A formant filter makes an operator a rich source (waveform, shape, harmonics and NoiseM); everything else is
            // what the seed gave.
            for (int i = 0; i < b.OperatorCount; i++)
            {
                OperatorPatch expected = b.Operators[i].Filter?.Type == OperatorFilterType.Formant
                    ? VoiceSource.AsRichSource(a.Operators[i])
                    : a.Operators[i];
                OperatorPatch got = b.Operators[i];
                Assert.Equal((expected.Waveform, expected.Shape, expected.HarmonicLimit, expected.NoiseM), (got.Waveform, got.Shape, got.HarmonicLimit, got.NoiseM));
            }

            OperatorMatrixPatch stripped = b with
            {
                Operators = [.. b.Operators.Select((o, i) => o with
                {
                    Filter = null,
                    Waveform = a.Operators[i].Waveform,
                    Shape = a.Operators[i].Shape,
                    HarmonicLimit = a.Operators[i].HarmonicLimit,
                    NoiseM = a.Operators[i].NoiseM,
                })],
            };
            Assert.Equal(a.ComputeHash(), stripped.ComputeHash());
        }

        Assert.True(withFilter > 20);

        // Profiles without filters never get one, whatever the option says.
        var fm7 = new RandomPatchGenerator(SynthesisProfiles.SuperColliderFm7.Capabilities, new RandomPatchOptions { FilterProbability = 1.0 });
        Assert.All(Enumerable.Range(0, 10), seed => Assert.All(fm7.Generate(seed).Operators, static o => Assert.Null(o.Filter)));
    }

    [Fact]
    public void Generator_EveryFormantFilter_HasF3()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var generator = new RandomPatchGenerator(fmx6, new RandomPatchOptions { FilterProbability = 1.0 });
        var formants = new List<OperatorFilter>();
        for (int seed = 0; seed < 400; seed++)
        {
            OperatorMatrixPatch patch = generator.Generate(seed);
            Assert.Empty(patch.Validate());
            Assert.True(PatchCompatibilityValidator.Instance.Validate(patch, fmx6).IsCompatible);
            formants.AddRange(patch.Operators.Select(static o => o.Filter).OfType<OperatorFilter>().Where(static f => f.Type == OperatorFilterType.Formant));
        }

        // Every formant filter gets a third peak above the second, with its own Q, as the search uses F1 to F3 on
        // every formant filter; F4 and F5 are never set.
        Assert.True(formants.Count > 100, $"{formants.Count} formant filters");
        Assert.All(formants, static f =>
        {
            Assert.True(f.Formant3Level > 0.0, $"F3 level {f.Formant3Level}");
            Assert.True(f.Formant3Ratio > f.FormantRatio, $"F3 ratio {f.Formant3Ratio} above F2 ratio {f.FormantRatio}");
            Assert.InRange(f.Formant3Ratio, 1.0, OperatorFilter.MaxFormantRatio);
            Assert.InRange(f.Formant3Level, 0.1, 0.6);
            Assert.InRange(f.Formant3Q, 8.0, 30.0);
            Assert.Equal((1.0, 0.0, 0.0, 1.0, 0.0, 0.0), (f.Formant4Ratio, f.Formant4Level, f.Formant4Q, f.Formant5Ratio, f.Formant5Level, f.Formant5Q));
        });
    }

    [Fact]
    public void Generator_EveryFormantOperator_IsARichSource()
    {
        // A formant filter shapes the harmonics it is given: every tonal operator with one is a saw, or a pulse at its
        // drawn width, with at least 64 harmonics, and a sine, triangle or square becomes the 1 % voice source; a noise
        // operator with one stays noise (a whisper). Other operators are left as drawn.
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var generator = new RandomPatchGenerator(fmx6, new RandomPatchOptions { FilterProbability = 1.0 });
        var tonal = new List<OperatorPatch>();
        var noise = new List<OperatorPatch>();
        var other = new List<OperatorPatch>();
        for (int seed = 0; seed < 400; seed++)
        {
            foreach (OperatorPatch op in generator.Generate(seed).Operators.Where(static o => o.Enabled && o.Filter is not null))
            {
                (op.Filter!.Type != OperatorFilterType.Formant ? other : op.Waveform.IsNoise() ? noise : tonal).Add(op);
            }
        }

        Assert.True(tonal.Count > 60, $"{tonal.Count} tonal formant operators");
        Assert.All(tonal, static o => Assert.True(o.Waveform is OperatorWaveform.Saw or OperatorWaveform.Pulse, $"{o.Waveform}"));
        Assert.All(tonal, static o => Assert.True(o.HarmonicLimit >= 64, $"{o.HarmonicLimit} harmonics"));
        Assert.Contains(tonal, static o => o.HarmonicLimit == 128);
        Assert.Contains(tonal, static o => o.Waveform == OperatorWaveform.Saw);
        Assert.Contains(tonal, static o => o.Waveform == OperatorWaveform.Pulse && o.Shape == VoiceSource.Width);
        Assert.Contains(tonal, static o => o.Waveform == OperatorWaveform.Pulse && o.Shape > 0.05);
        Assert.True(noise.Count > 20, $"{noise.Count} noise formant operators");
        Assert.Contains(noise, static o => o.Waveform == OperatorWaveform.FilteredNoise);
        Assert.Contains(noise, static o => o.Waveform == OperatorWaveform.Noise);
        Assert.Contains(other, static o => o.HarmonicLimit < 64);
        Assert.Contains(other, static o => o.Waveform is OperatorWaveform.Saw or OperatorWaveform.Sine);
    }

    [Fact]
    public void AsVoiceSource_MakesATonalOperatorANarrowPulse_AndKeepsNoise()
    {
        OperatorFilter vowel = OperatorFilter.FormantAt(700, 1.8, 0.5, 8);
        OperatorPatch saw = OperatorPatch.Sine(OperatorFrequency.AtRatio(2)).WithWaveform(OperatorWaveform.Saw) with { HarmonicLimit = 8, Filter = vowel, NoisePhase = 0.2 };
        OperatorPatch source = VoiceSource.AsVoiceSource(saw, keepPulseWidth: true);
        Assert.Equal((OperatorWaveform.Pulse, 0.01, 64), (source.Waveform, source.Shape, source.HarmonicLimit));
        Assert.Equal(saw with { Waveform = OperatorWaveform.Pulse, Shape = 0.01, HarmonicLimit = 64 }, source);

        // A pulse keeps its width only when asked; more harmonics than 64 stay; noise on m goes.
        OperatorPatch pulse = saw with { Waveform = OperatorWaveform.Pulse, Shape = 0.3, HarmonicLimit = 128, NoiseM = 0.1 };
        Assert.Equal(pulse with { NoiseM = 0.0 }, VoiceSource.AsVoiceSource(pulse, keepPulseWidth: true));
        Assert.Equal(pulse with { Shape = 0.01, NoiseM = 0.0 }, VoiceSource.AsVoiceSource(pulse, keepPulseWidth: false));
        OperatorPatch elliptic = VoiceSource.AsVoiceSource(saw.WithWaveform(OperatorWaveform.Elliptic) with { NoiseM = 0.2 }, keepPulseWidth: true);
        Assert.Equal((OperatorWaveform.Pulse, 0.01, 64, 0.0), (elliptic.Waveform, elliptic.Shape, elliptic.HarmonicLimit, elliptic.NoiseM));

        // Noise stays as it is.
        foreach (OperatorWaveform waveform in new[] { OperatorWaveform.Noise, OperatorWaveform.SampleHoldNoise, OperatorWaveform.FilteredNoise })
        {
            OperatorPatch noise = saw with { Waveform = waveform, Shape = 0.7 };
            Assert.Same(noise, VoiceSource.AsVoiceSource(noise, keepPulseWidth: false));
        }

        Assert.Equal(64, VoiceSource.Harmonics);
    }

    [Fact]
    public void AsRichSource_KeepsASawOrAPulse_AndMakesTheRestTheVoiceSource()
    {
        OperatorFilter vowel = OperatorFilter.FormantAt(700, 1.8, 0.5, 8);
        OperatorPatch sine = OperatorPatch.Sine(OperatorFrequency.AtRatio(2)) with { HarmonicLimit = 8, Filter = vowel, NoisePhase = 0.2 };

        // A saw stays a saw, a pulse keeps its width; both get 64 harmonics, and more stay.
        OperatorPatch saw = sine.WithWaveform(OperatorWaveform.Saw);
        Assert.Equal(saw with { HarmonicLimit = 64 }, VoiceSource.AsRichSource(saw));
        Assert.Equal(saw with { HarmonicLimit = 128 }, VoiceSource.AsRichSource(saw with { HarmonicLimit = 128 }));
        OperatorPatch pulse = sine.WithWaveform(OperatorWaveform.Pulse) with { Shape = 0.2 };
        Assert.Equal(pulse with { HarmonicLimit = 64 }, VoiceSource.AsRichSource(pulse));
        Assert.Equal(pulse with { Shape = 0.6, HarmonicLimit = 96 }, VoiceSource.AsRichSource(pulse with { Shape = 0.6, HarmonicLimit = 96 }));

        // A sine, triangle, square or elliptic operator, which leaves a formant filter little to shape, becomes the
        // 1 % voice source.
        foreach (OperatorWaveform waveform in new[] { OperatorWaveform.Sine, OperatorWaveform.Triangle, OperatorWaveform.Square, OperatorWaveform.Elliptic })
        {
            OperatorPatch op = sine.WithWaveform(waveform) with { NoiseM = 0.1 };
            Assert.Equal(VoiceSource.AsVoiceSource(op, keepPulseWidth: false), VoiceSource.AsRichSource(op));
            Assert.Equal((OperatorWaveform.Pulse, 0.01, 64, 0.0), (VoiceSource.AsRichSource(op).Waveform, VoiceSource.AsRichSource(op).Shape, VoiceSource.AsRichSource(op).HarmonicLimit, VoiceSource.AsRichSource(op).NoiseM));
        }

        // Noise stays as it is.
        foreach (OperatorWaveform waveform in new[] { OperatorWaveform.Noise, OperatorWaveform.SampleHoldNoise, OperatorWaveform.FilteredNoise })
        {
            OperatorPatch noise = sine with { Waveform = waveform, Shape = 0.7 };
            Assert.Same(noise, VoiceSource.AsRichSource(noise));
        }
    }

    [Fact]
    public void InaudibleOperators_DoNotHaveToBeRepresentable()
    {
        // OP2 is enabled but neither heard nor modulating anything heard: a saw with a filter and self-feedback.
        OperatorMatrixPatch patch = Carrier(null)
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2)) with { Waveform = OperatorWaveform.Saw, Filter = OperatorFilter.LowPass(4) })
            .WithEdge(new ModulationEdge(1, 1, true, 0.5));
        Assert.False(patch.FindAudibleOperators()[1]);
        SynthesisCapabilities fm7 = SynthesisProfiles.SuperColliderFm7.Capabilities with { SupportsSelfFeedback = false };
        PatchCompatibilityResult result = PatchCompatibilityValidator.Instance.Validate(patch, fm7);
        Assert.True(result.IsCompatible, result.ToString());

        // Heard, the same operator is refused for each of the three reasons.
        PatchCompatibilityResult heard = PatchCompatibilityValidator.Instance.Validate(patch.WithOutputLevel(1, 0.5), fm7);
        Assert.Contains(heard.Issues, static i => i.Code == PatchCompatibilityCodes.OperatorFilter);
        Assert.Contains(heard.Issues, static i => i.Code == PatchCompatibilityCodes.UnsupportedWaveform);
        Assert.Contains(heard.Issues, static i => i.Code == PatchCompatibilityCodes.SelfFeedback);
    }
}
