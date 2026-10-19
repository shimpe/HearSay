using HearSay.Synthesis.Compilation;
using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.Synthesis.Tests;

public sealed class ControlStreamTests
{
    private const int BlockSize = 64;
    private const int SampleRate = 48000;

    private static readonly Dictionary<string, (OperatorMatrixPatch Patch, double Hz, int? Midi)> Patches = new()
    {
        ["sine-stack"] = (ReferencePatches.Find("bright-fm")!.Patch, 165, null),
        ["adsr-sustain"] = (ReferencePatches.Find("pure-sine")!.Patch, 440, null),
        ["two-stage-decay"] = (ReferencePatches.Find("bell")!.Patch, 330, null),
        ["fixed-frequency"] = (FixedFrequency(), 220, null),
        ["edge-amount-envelope"] = (EdgeAmountEnvelope(), 196, null),
        ["fmx6-filter-sweep"] = (FilterSweep(), 110, null),
        ["fmx6-five-formants"] = (FiveFormants(), 110, null),
        ["pitch-envelope"] = (ReferencePatches.Find("simple-2op")!.Patch with
        {
            Pitch = new PitchModel(new OperatorEnvelope([new EnvelopePoint(0, 30), new EnvelopePoint(0.05, 0)], null, 2)),
        }, 220, null),
        ["vibrato-delay-fade"] = (ReferencePatches.Find("brass-like")!.Patch with
        {
            Pitch = new PitchModel(Vibrato: new LfoSettings(5.5, 20, 0.1, 0.2)),
        }, 233, null),
        ["key-scaling-note-40"] = (KeyScaled(), 82.40688922821748, 40),
    };

    public static TheoryData<string, int> Cases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (string name in Patches.Keys)
            {
                foreach (int gateOffBlock in new[] { 0, 1, 37 })
                {
                    data.Add(name, gateOffBlock);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ControlStream_StepByStep_EqualsTheCompiledArrays(string name, int gateOffBlock)
    {
        (OperatorMatrixPatch patch, double hz, int? midi) = Patches[name];
        var request = new RenderRequest(
            hz,
            midi,
            0.7,
            TimeSpan.FromSeconds(gateOffBlock * (double)BlockSize / SampleRate),
            TimeSpan.FromSeconds(0.5),
            SampleRate);
        Assert.Equal(gateOffBlock, request.NoteSamples / BlockSize);

        CompiledOperatorMatrixPatch compiled = PatchCompiler.Compile(patch, request, BlockSize);
        var stream = new ControlStream(patch, request.ToVoiceRequest(), BlockSize);

        Assert.Equal(compiled.SampleRate, stream.SampleRate);
        Assert.Equal(compiled.BlockSize, stream.BlockSize);
        Assert.Equal(compiled.OperatorIndices, stream.OperatorIndices);
        Assert.Equal(compiled.Waveforms, stream.Waveforms);
        Assert.Equal(compiled.Phases, stream.Phases);
        Assert.Equal(compiled.Shapes, stream.Shapes);
        Assert.Equal(compiled.Morphs, stream.Morphs);
        Assert.Equal(compiled.NoisePhases, stream.NoisePhases);
        Assert.Equal(compiled.NoiseMs, stream.NoiseMs);
        Assert.Equal(compiled.NoiseColor, stream.NoiseColor);
        Assert.Equal(compiled.NoiseCutoffHz, stream.NoiseCutoffHz);
        Assert.Equal(compiled.HarmonicLimits, stream.HarmonicLimits);
        Assert.Equal(compiled.OutputGains, stream.OutputGains);
        Assert.Equal(compiled.EdgeSources, stream.EdgeSources);
        Assert.Equal(compiled.EdgeDestinations, stream.EdgeDestinations);
        Assert.Equal(compiled.Filters is not null, stream.HasFilters);
        if (compiled.Filters is CompiledFilters filters)
        {
            Assert.Equal(filters.Types, stream.FilterTypes);
            Assert.Equal(filters.BaseHz, stream.FilterBaseHz);
            Assert.Equal(filters.Q, stream.FilterQ);
            Assert.Equal(filters.FormantRatios, stream.FormantRatios);
            Assert.Equal(filters.FormantLevels, stream.FormantLevels);
            Assert.Equal(filters.FormantFields, stream.FormantFields);
            Assert.Equal(filters.GainsDb, stream.FilterGainsDb);
        }

        AssertCurrentValues(compiled, stream, 0);
        for (int b = 0; b < compiled.BlockCount; b++)
        {
            stream.Advance(b < gateOffBlock);
            AssertCurrentValues(compiled, stream, b + 1);
        }
    }

    private static void AssertCurrentValues(CompiledOperatorMatrixPatch compiled, ControlStream stream, int step)
    {
        Assert.Equal(compiled.OperatorCount, stream.Frequencies.Length);
        Assert.Equal(compiled.OperatorCount, stream.Amplitudes.Length);
        Assert.Equal(compiled.EdgeCount, stream.EdgeAmounts.Length);
        Assert.Equal(compiled.OperatorCount, stream.FilterOctaves.Length);
        for (int k = 0; k < compiled.OperatorCount; k++)
        {
            Assert.Equal(At(compiled.Frequencies[k], step), stream.Frequencies[k]);
            Assert.Equal(At(compiled.Amplitudes[k], step), stream.Amplitudes[k]);
            Assert.Equal(compiled.Filters is null ? 0f : At(compiled.Filters.Octaves[k], step), stream.FilterOctaves[k]);
        }

        for (int e = 0; e < compiled.EdgeCount; e++)
        {
            Assert.Equal(At(compiled.EdgeAmounts[e], step), stream.EdgeAmounts[e]);
        }
    }

    private static float At(float[] values, int step) => values.Length == 1 ? values[0] : values[step];

    private static OperatorMatrixPatch FixedFrequency() =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.6, 0.3)))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtFixed(60), OperatorEnvelope.TwoStageDecay(0.002, 0.08, 0.2, 0.3, 0.01, 0.1)) with { DetuneHz = 1.5 })
            .WithOutputLevel(0, 0.7)
            .WithOutputLevel(1, 0.3)
            .WithEdge(new ModulationEdge(1, 0, true, 1.2));

    private static OperatorMatrixPatch EdgeAmountEnvelope() =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.3, 0.7, 0.25)) with { VelocitySensitivity = 0.8 })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.005, 0.4, 0.6, 0.25)))
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 2.5, OperatorEnvelope.Adsr(0.02, 0.15, 0.4, 0.2)))
            .WithEdge(new ModulationEdge(0, 0, true, 0.4));

    [Fact]
    public void FormantFields_AreTheExtraInputsOfTheUGen_InSinglePrecision()
    {
        // Per operator [q2, ratio3, level3, q3, ratio4, level4, q4, ratio5, level5, q5]; the defaults for no filter
        // and for filters that use none of them.
        var stream = new ControlStream(FiveFormants(), new VoiceRequest(110, null, 0.7, SampleRate), BlockSize);
        Assert.Equal(OperatorFilterKernel.FormantFieldCount * stream.OperatorCount, stream.FormantFields.Length);
        Assert.Equal(
            [12.8f, 3.625f, 0.025f, 0f, 4.875f, 0.1f, 30f, 6.1875f, 0.003f, 35.4f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
            stream.FormantFields);
        Assert.Equal(stream.FormantFields, PatchCompiler.Compile(FiveFormants(), RenderRequest.Create(110, 0.2, 0.1)).Filters!.FormantFields);
    }

    private static OperatorMatrixPatch FiveFormants() =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.5, 0.8, 0.3)).WithWaveform(OperatorWaveform.Saw) with
            {
                Filter = OperatorFilter.Formants(800, 41, new FormantPeak(1.4375, 0.5, 12.8), new FormantPeak(3.625, 0.025, 0), new FormantPeak(4.875, 0.1, 30), new FormantPeak(6.1875, 0.003, 35.4)),
            })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Adsr(0.005, 0.3, 0.5, 0.3)) with { Filter = OperatorFilter.LowPass(3) })
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3), OperatorEnvelope.Adsr(0.005, 0.2, 0.4, 0.3)))
            .WithOutputLevel(0, 0.6)
            .WithOutputLevel(1, 0.3)
            .WithEdge(new ModulationEdge(2, 0, true, 1.1));

    private static OperatorMatrixPatch FilterSweep() =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.5, 0.8, 0.3)).WithWaveform(OperatorWaveform.Saw) with
            {
                Filter = OperatorFilter.LowPass(2, 4, 4) with { EnvelopeOctaves = 3, Envelope = OperatorEnvelope.Adsr(0.01, 0.25, 0.3, 0.2) },
            })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.005, 0.3, 0.5, 0.3)) with
            {
                Filter = OperatorFilter.PeakAt(1200, -6, 3),
            })
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3), OperatorEnvelope.Adsr(0.005, 0.2, 0.4, 0.3)))
            .WithOutputLevel(0, 0.6)
            .WithOutputLevel(1, 0.3)
            .WithEdge(new ModulationEdge(2, 0, true, 1.1));

    private static OperatorMatrixPatch KeyScaled() =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.TwoStageDecay(0.002, 0.25, 0.5, 2.0, 0.1, 0.25)) with
            {
                KeyScaling = new KeyScaling(-6.0, 0.5, 60),
            })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(14), OperatorEnvelope.TwoStageDecay(0.001, 0.06, 0.1, 0.3, 0.005, 0.1)) with
            {
                KeyScaling = new KeyScaling(3.0, 1.0, 72),
            })
            .WithOutputLevel(0, 0.8)
            .WithEdge(new ModulationEdge(1, 0, true, 1.4));
}
