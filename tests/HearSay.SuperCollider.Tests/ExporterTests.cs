using HearSay.SuperCollider.FM7;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.SuperCollider.Tests;

public sealed class ExporterTests
{
    private static readonly SuperColliderFm7Exporter Exporter = new();

    private static OperatorMatrixPatch Patch() =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.3)) with { VelocitySensitivity = 0.6 })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(3.5), OperatorEnvelope.TwoStageDecay(0.001, 0.1, 0.3, 0.8, 0.05, 0.2)) with { DetuneHz = 1.5 })
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtFixed(87.5), OperatorEnvelope.Constant()))
            .WithOutputLevel(0, 0.8)
            .WithOutputLevel(2, 0.25)
            .WithEdge(new ModulationEdge(1, 0, true, 2.75))
            .WithEdge(new ModulationEdge(0, 0, true, 0.4))
            .WithEdge(new ModulationEdge(0, 1, true, 0.3, OperatorEnvelope.Adsr(0.01, 0.1, 0.5, 0.1)));

    [Fact]
    public void Export_IsAPlayableSynthDefAroundTheFullFm7Matrix()
    {
        string scd = Exporter.Export(Patch());

        Assert.Contains("SynthDef(\\HearSayPatch, { |out = 0, freq = 440, gate = 1, velocity = 1, amp = 0.2|", scd);
        Assert.Contains("FM7.ar(ctlMatrix, modMatrix)", scd);
        Assert.DoesNotContain("arAlgo", scd);
        Assert.Contains("Out.ar(out, (signal * amp) ! 2);", scd);
        Assert.Contains("doneAction: Done.freeSelf", scd);
        Assert.Contains(".add;", scd);
        Assert.Contains("Synth(\\HearSayPatch", scd);
        Assert.Contains("Pbind(", scd);
        Assert.DoesNotContain("HearSay.dll", scd);
        Assert.Equal(".scd", Exporter.FileExtension);
    }

    [Fact]
    public void Export_MapsTheCanonicalModelOntoFm7Inputs()
    {
        string scd = Exporter.ExportSynthDef(Patch(), "Test");
        string[] lines = scd.Split('\n');

        // Ratio operators follow freq, fixed operators do not.
        Assert.Contains(lines, static l => l.Contains("[(freq * 1.0), 0.0, EnvGen.kr(", StringComparison.Ordinal) && l.Contains("// OP1", StringComparison.Ordinal));
        Assert.Contains(lines, static l => l.Contains("[(freq * 3.5) + 1.5,", StringComparison.Ordinal) && l.Contains("// OP2", StringComparison.Ordinal));
        Assert.Contains(lines, static l => l.TrimStart().StartsWith("[87.5, 0.0,", StringComparison.Ordinal) && l.Contains("// OP3", StringComparison.Ordinal));
        Assert.Contains(lines, static l => l.Contains("[0, 0, 0]", StringComparison.Ordinal) && l.Contains("// OP4 (off)", StringComparison.Ordinal));

        // Velocity and envelopes.
        Assert.Contains("* (0.4 + (0.6 * velocity))", scd);
        Assert.Contains("Env([0.00001, 1.0, 0.5, 0.00001], [0.01, 0.2, 0.3], [\\lin, \\exp, \\exp], 2), gate)", scd);

        // modMatrix[destination][source]: row "into OP1" has self-feedback in column 1 and OP2 in column 2.
        string intoOp1 = lines.Single(static l => l.Contains("// into OP1", StringComparison.Ordinal));
        Assert.StartsWith("[0.4, 2.75, 0, 0, 0, 0]", intoOp1.Trim());
        string intoOp2 = lines.Single(static l => l.Contains("// into OP2", StringComparison.Ordinal));
        Assert.StartsWith("[(0.3 * EnvGen.kr(", intoOp2.Trim());

        // Independent output mix.
        Assert.Contains("signal = (operators * [0.8, 0.0, 0.25, 0, 0, 0]).sum;", scd);
    }

    [Fact]
    public void Export_IncludesPitchModelAndKeyScalingOnlyWhenUsed()
    {
        OperatorMatrixPatch patch = Patch();
        Assert.DoesNotContain("pitch", Exporter.ExportSynthDef(patch));
        Assert.DoesNotContain("cpsmidi", Exporter.ExportSynthDef(patch));

        OperatorMatrixPatch moving = patch.WithOperator(0, patch.Operators[0] with { KeyScaling = new KeyScaling(-3, 0.5, 60) }) with
        {
            Pitch = new PitchModel(new OperatorEnvelope([new EnvelopePoint(0, 30), new EnvelopePoint(0.05, 0)], null, 2), new LfoSettings(5.5, 12, 0.1, 0.2)),
        };
        string scd = Exporter.ExportSynthDef(moving);

        Assert.Contains("var note = freq.cpsmidi;", scd);
        Assert.Contains("SinOsc.kr(5.5) * 12.0", scd);
        Assert.Contains("(freq * 1.0 * pitch)", scd);
        Assert.Contains("timeScale: (2 ** (-0.5 * ((note - 60.0) / 12)))", scd);
        Assert.Contains("(-3.0 * ((note - 60.0) / 12)).dbamp", scd);

        // Fixed operators ignore the pitch model.
        Assert.Contains("[87.5, 0.0,", scd);
    }

    [Fact]
    public void Export_RejectsWhatFm7CannotRepresentInsteadOfApproximating()
    {
        OperatorMatrixPatch saw = Patch().WithOperator(1, Patch().Operators[1] with { Waveform = OperatorWaveform.Saw });
        OperatorMatrixPatch four = OperatorMatrixPatch.Silent(4).WithOperator(0, Patch().Operators[0]).WithOutputLevel(0, 1);

        var error = Assert.Throws<PatchNotSupportedException>(() => Exporter.Export(saw));
        Assert.Contains(error.Result.Issues, static i => i.Code == PatchCompatibilityCodes.UnsupportedWaveform && i.OperatorIndex == 1);
        Assert.Contains("Saw", error.Message);
        Assert.Throws<PatchNotSupportedException>(() => Exporter.Export(four));
        Assert.Throws<ArgumentException>(() => Exporter.ExportSynthDef(Patch(), "bad name"));
    }

    [Fact]
    public void Export_WritesNumbersSuperColliderCanRead()
    {
        Assert.Equal("0.00001", SuperColliderFm7Exporter.Number(1e-5));
        Assert.Equal("2.0", SuperColliderFm7Exporter.Number(2));
        Assert.Equal("-0.5", SuperColliderFm7Exporter.Number(-0.5));
        Assert.Equal("12000.0", SuperColliderFm7Exporter.Number(12000));
        string scd = Exporter.Export(new RandomPatchGenerator(SynthesisProfiles.SuperColliderFm7.Capabilities).Generate(11), new PatchExportOptions { InstrumentName = "Rnd", IncludeUsageExample = false, HeaderComments = ["line one\nline two"] });
        Assert.DoesNotContain("E-", scd);
        Assert.Contains("// line one line two", scd);
        Assert.DoesNotContain("Pbind", scd);
    }
}
