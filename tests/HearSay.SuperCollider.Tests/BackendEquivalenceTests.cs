using HearSay.SuperCollider.Validation;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.SuperCollider.Tests;

/// <summary>
/// Native renderer against the real FM7 UGen. These tests need SuperCollider with sc3-plugins and are skipped without.
/// </summary>
public sealed class BackendEquivalenceTests(ITestOutputHelper output)
{
    private static readonly SynthesisCapabilities Fm7 = SynthesisProfiles.SuperColliderFm7.Capabilities;

    private static BackendEquivalenceValidator Validator() =>
        new(new Fm7CompatibleOperatorMatrixRenderer(), new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!));

    [SuperColliderFact]
    public void RandomPatches_SoundTheSameInSuperCollider()
    {
        (string Name, RandomPatchOptions Options)[] families =
        [
            ("sparse", new RandomPatchOptions { EdgeDensity = 0.1, SelfFeedbackProbability = 0, AllowCircularFeedback = false }),
            ("dense", new RandomPatchOptions { EdgeDensity = 0.6, MaxEdgeAmount = 1.5 }),
            ("self-feedback", new RandomPatchOptions { SelfFeedbackProbability = 0.9, AllowCircularFeedback = false }),
            ("circular", new RandomPatchOptions { EdgeDensity = 0.45, AllowCircularFeedback = true, MaxEdgeAmount = 2.0 }),
            ("fixed-and-detuned", new RandomPatchOptions { FixedFrequencyProbability = 0.5, DetuneProbability = 0.9 }),
            ("moving", new RandomPatchOptions { EdgeEnvelopeProbability = 0.9, PitchModulationProbability = 1.0 }),
        ];

        var jobs = new List<(OperatorMatrixPatch, RenderRequest)>();
        var names = new List<string>();
        foreach ((string name, RandomPatchOptions options) in families)
        {
            var generator = new RandomPatchGenerator(Fm7, options);
            for (int seed = 0; seed < 5; seed++)
            {
                double frequency = 110.0 * Math.Pow(2.0, seed / 2.0);
                // Note lengths that do not fall on a control-block boundary: the gate timing is part of the contract.
                jobs.Add((generator.Generate(seed), new RenderRequest(frequency, null, 0.4 + (0.15 * seed), TimeSpan.FromSeconds(0.5 + (0.0137 * seed)), TimeSpan.FromSeconds(0.25), 48000)));
                names.Add($"{name}/{seed}");
            }
        }

        IReadOnlyList<BackendExportValidation> results = Validator().Validate(jobs);

        int sampleExact = 0;
        for (int i = 0; i < results.Count; i++)
        {
            BackendExportValidation r = results[i];
            output.WriteLine($"{names[i],-22} overall {r.OverallDistance:F5} spectral {r.SpectralDistance:F5} envelope {r.EnvelopeDistance:F5} partial {r.PartialDistance:F5} max sample diff {r.MaxSampleDifference:E2} (peak {r.PeakLevel:F2})");
            if (r.MaxSampleDifference < 1e-3 * Math.Max(0.05, r.PeakLevel))
            {
                sampleExact++;
            }
        }

        output.WriteLine($"{sampleExact} of {results.Count} patches match sample by sample");
        Assert.All(results, static r => Assert.True(r.IsEquivalent, r.ToString()));

        // Strong feedback is chaotic: rounding differences grow until the waveforms part while the sound stays the same.
        Assert.True(sampleExact >= results.Count * 3 / 4, $"only {sampleExact} of {results.Count} are sample-exact");
    }

    [SuperColliderFact]
    public void ReferencePatches_SoundTheSameInSuperCollider()
    {
        var jobs = ReferencePatches.All
            .Select(static p => (p.Patch, RenderRequest.Create(p.FrequencyHz, Math.Min(0.8, p.NoteSeconds), 0.3)))
            .ToList();

        IReadOnlyList<BackendExportValidation> results = Validator().Validate(jobs);

        for (int i = 0; i < results.Count; i++)
        {
            output.WriteLine($"{ReferencePatches.All[i].Name,-22} overall {results[i].OverallDistance:F5} max sample diff {results[i].MaxSampleDifference:E2}");
        }

        Assert.All(results, static r => Assert.True(r.IsEquivalent, r.ToString()));
    }

    [SuperColliderFact]
    public void ExportedPatch_IsAnInstrument_FreqGateAndVelocityWork()
    {
        ReferencePatch piano = ReferencePatches.Find("electric-piano")!;
        OperatorMatrixPatch patch = piano.Patch
            .WithOperator(1, piano.Patch.Operators[1] with { VelocitySensitivity = 0.8 })
            .WithOperator(4, OperatorPatch.Sine(OperatorFrequency.AtFixed(180), OperatorEnvelope.Adsr(0.01, 0.1, 0.5, 0.1)))
            .WithOutputLevel(4, 0.2);
        var sc = new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!);
        var native = new Fm7CompatibleOperatorMatrixRenderer();
        (OperatorMatrixPatch, RenderRequest)[] jobs =
        [
            (patch, new RenderRequest(131, null, 1.0, TimeSpan.FromSeconds(0.6), TimeSpan.FromSeconds(0.3), 48000)),
            (patch, new RenderRequest(523, null, 0.3, TimeSpan.FromSeconds(0.2071), TimeSpan.FromSeconds(0.3), 48000)),
        ];

        IReadOnlyList<RenderedAudio> rendered = sc.RenderMany(jobs);

        for (int j = 0; j < jobs.Length; j++)
        {
            float[] expected = native.Render(patch, jobs[j].Item2).Samples;
            double worst = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(expected[i] - rendered[j].Samples[i]));
            }

            output.WriteLine($"note {j}: max sample difference {worst:E2}");
            Assert.True(worst < 1e-3, $"note {j}: {worst}");
        }

        // The short note was released early: it is silent well before the long one.
        Assert.True(rendered[1].Samples.Skip(19200).Max(Math.Abs) < 0.02);
    }
}
