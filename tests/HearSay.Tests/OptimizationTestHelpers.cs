using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Features;
using HearSay.Models;
using HearSay.Optimization.History;
using HearSay.Optimization.Parameters;

namespace HearSay.Tests;

internal static class OptimizationTestHelpers
{
    public static readonly FeatureVectorSchema Schema = FeatureSchemaBuilder.Build(new ToneComparisonOptions());

    public static SynthParameterState State(params (string Id, double Value)[] values) =>
        new(values.ToDictionary(v => v.Id, v => v.Value));

    /// <summary>Synthetic observation: all features zero except the given ones.</summary>
    public static OptimizationObservation Observation(long iteration, SynthParameterState state, double loss, params (string Feature, double Value)[] features)
    {
        var values = new double[Schema.Features.Count];
        foreach ((string feature, double value) in features)
        {
            values[Schema.IndexOf(feature)] = value;
        }

        var vector = new FeatureVector(Schema.Features.Select(f => f.Id).ToArray(), values);
        var typed = new ToneDifferenceVector(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
        var scores = new SimilarityScores(loss, loss, loss, loss, loss, loss, Math.Exp(-3 * loss));
        return new OptimizationObservation(iteration, state, scores, typed, vector, DateTimeOffset.UnixEpoch.AddSeconds(iteration), loss);
    }

    /// <summary>Small deterministic additive synth: brightness sets the harmonic rolloff, attack the attack time.</summary>
    public static AudioNote Render(double brightness, double attack = 0.1, double harmonic2 = 0.0)
    {
        double[] harmonics = TestSignals.Rolloff(24, 2.6 - (2.0 * brightness));
        harmonics[1] += harmonic2;
        return TestSignals.Note(TestSignals.Tone(220, harmonics, seconds: 0.8, attackMs: 5 + (200 * attack)), 220);
    }
}
