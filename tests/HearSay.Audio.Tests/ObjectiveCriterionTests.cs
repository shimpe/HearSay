using HearSay.Analysis;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Models;
using HearSay.Objective;

namespace HearSay.Tests;

public sealed class ObjectiveCriterionTests
{
    private static readonly double[] Saw = TestSignals.Rolloff(20, 1.0);
    private static readonly double[] Square = [.. TestSignals.Rolloff(20, 1.0).Select((a, i) => i % 2 == 0 ? a : 0.0)];

    private static AudioTarget Target(float[] samples) => new(samples, TestSignals.SampleRate, 440.0);

    [Fact]
    public void Similarity_IsOneMinusTheComparerScore_AtNormalAndDetailed()
    {
        float[] target = TestSignals.Tone(440, Saw);
        float[] candidate = TestSignals.Tone(440, Square);
        var objective = new AudioObjective();
        var options = new ObjectiveOptions { Criterion = ObjectiveCriterion.Similarity, Analysis = new TargetAnalysisOptions { SampleRate = TestSignals.SampleRate } };
        PreparedObjectiveTarget prepared = objective.Prepare(Target(target), options);

        ObjectiveResult normal = objective.Evaluate(prepared, TestSignals.Note(candidate), EvaluationLevel.Normal);
        ObjectiveResult detailed = objective.Evaluate(prepared, TestSignals.Note(candidate), EvaluationLevel.Detailed);
        // The same comparison a result reports: the analysed (preprocessed) target against the raw candidate.
        ToneComparisonResult comparison = new ToneComparer().Compare(
            new AudioNote(prepared.Analysis.Audio.Samples, prepared.Analysis.Audio.SampleRate, prepared.Analysis.FundamentalHz),
            TestSignals.Note(candidate),
            new ToneComparisonOptions { LoudnessMode = LoudnessMode.SeparateLoudness, ExpectedFundamentalHz = prepared.Analysis.FundamentalHz });

        Assert.True(normal.IsUsable);
        Assert.Equal(1.0 - comparison.Scores.Similarity, normal.Loss, 3);
        Assert.Equal(normal.Loss, detailed.Loss, 6);
        Assert.Equal(1.0 - comparison.Scores.Harmonic, normal.Terms.Partial, 3);
        Assert.True(double.IsNaN(normal.Terms.Trajectory));
    }

    [Fact]
    public void Similarity_IsNearZeroForTheSameNote_AndCoarseStaysTheAudioLoss()
    {
        float[] target = TestSignals.Tone(440, Saw);
        var objective = new AudioObjective();
        var analysis = new TargetAnalysisOptions { SampleRate = TestSignals.SampleRate };
        PreparedObjectiveTarget bySimilarity = objective.Prepare(Target(target), new ObjectiveOptions { Criterion = ObjectiveCriterion.Similarity, Analysis = analysis });
        PreparedObjectiveTarget byLoss = objective.Prepare(Target(target), new ObjectiveOptions { Analysis = analysis });

        // The analysed target is trimmed and normalized, the candidate is not: the same note keeps a small floor,
        // the same one the reported similarity of a perfect match has.
        Assert.InRange(objective.Evaluate(bySimilarity, TestSignals.Note((float[])target.Clone()), EvaluationLevel.Normal).Loss, 0.0, 0.04);
        Assert.Equal(
            objective.Evaluate(byLoss, TestSignals.Note(TestSignals.Tone(440, Square)), EvaluationLevel.Coarse).Loss,
            objective.Evaluate(bySimilarity, TestSignals.Note(TestSignals.Tone(440, Square)), EvaluationLevel.Coarse).Loss,
            9);
    }
}
