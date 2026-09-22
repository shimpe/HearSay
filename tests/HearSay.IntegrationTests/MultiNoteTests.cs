using HearSay.Analysis;
using HearSay.Audio;
using HearSay.Objective;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;
using Xunit.Abstractions;

namespace HearSay.IntegrationTests;

/// <summary>Matching one patch to the same sound at several pitches.</summary>
public sealed class MultiNoteTests(ITestOutputHelper output)
{
    private static readonly Fm7CompatibleOperatorMatrixRenderer Renderer = new();

    private static OperatorMatrixPatch Hidden() => ReferencePatches.Find("electric-piano-keyed")!.Patch;

    private static AudioTarget TargetAt(OperatorMatrixPatch patch, int midi)
    {
        double hz = 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
        RenderedAudio audio = Renderer.Render(patch, new RenderRequest(hz, midi, 1.0, TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(0.3), 48000));
        return new AudioTarget(audio.Samples, audio.SampleRate, MidiNote: midi);
    }

    [Fact]
    public void ASetOfNotes_IsMatchedByOnePatchThatGeneralizesBetterThanASingleNoteMatch()
    {
        OperatorMatrixPatch hidden = Hidden();
        AudioTarget[] set = [TargetAt(hidden, 48), TargetAt(hidden, 60), TargetAt(hidden, 72)];
        AudioTarget unseen = TargetAt(hidden, 55);
        var matcher = new OperatorMatrixPatchMatcher();
        var options = new PatchMatchOptions { EvaluationBudget = 20000 };

        PatchMatchResult multi = matcher.Match(set, options);
        PatchMatchResult single = matcher.Match(set[1], options);

        SearchSummary s = multi.SearchSummary;
        output.WriteLine($"set: {s.InitialAudioLoss:F4} -> {s.FinalAudioLoss:F4}, {s.EvaluationsUsed} evaluations, {s.RendersUsed} renders, {s.Elapsed.TotalSeconds:F1} s");
        output.WriteLine($"  {multi.AlternativePatches[0].Regime} | {multi.AlternativePatches[0].Origin}");
        foreach (NoteMatch note in multi.Notes)
        {
            output.WriteLine($"  note {note.Request.MidiNote}: similarity {note.Comparison.Scores.Similarity:F3}");
        }

        Assert.Equal(3, multi.Notes.Count);
        Assert.Equal(60, multi.Request.MidiNote);
        Assert.InRange(s.RendersUsed, s.EvaluationsUsed + 1, 3 * s.EvaluationsUsed);
        Assert.True(s.FinalAudioLoss * 2 < s.InitialAudioLoss, $"{s.InitialAudioLoss} -> {s.FinalAudioLoss}");
        Assert.All(multi.Notes, static n => Assert.True(n.Comparison.Scores.Similarity > 0.6, $"note {n.Request.MidiNote}: {n.Comparison.Scores.Similarity}"));

        // Judged on a note neither search has seen, the patch fitted to the set has to do at least as well.
        double multiUnseen = LossAt(multi.BestPatch, unseen, 55);
        double singleUnseen = LossAt(single.BestPatch, unseen, 55);
        output.WriteLine($"unseen note 55: set patch {multiUnseen:F4}, single-note patch {singleUnseen:F4}");
        Assert.True(multiUnseen <= singleUnseen * 1.1, $"set {multiUnseen} versus single {singleUnseen}");
    }

    [Fact]
    public void ASetOfNotes_IsReproducibleAndWritesAFilePerNote()
    {
        OperatorMatrixPatch hidden = Hidden();
        AudioTarget[] set = [TargetAt(hidden, 57), TargetAt(hidden, 69)];
        var matcher = new OperatorMatrixPatchMatcher();
        var options = new PatchMatchOptions { EvaluationBudget = 800, RandomSeed = 9 };

        PatchMatchResult a = matcher.Match(set, options);
        PatchMatchResult b = matcher.Match(set, options with { MaxParallelEvaluations = 3 });
        Assert.Equal(a.BestPatch.ComputeHash(), b.BestPatch.ComputeHash());

        string directory = Path.Combine(Path.GetTempPath(), "hearsay-multi-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> files = PatchMatchResultWriter.Write(a, directory);
            Assert.Contains(files, f => f.EndsWith("preview-note1-220Hz.wav", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("preview-note2-440Hz.wav", StringComparison.Ordinal));
            Assert.Contains(files, f => f.EndsWith("comparison-note2-440Hz.json", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(directory, "patch.scd")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static double LossAt(OperatorMatrixPatch patch, AudioTarget target, int midi)
    {
        var objective = new AudioObjective();
        PreparedObjectiveTarget prepared = objective.Prepare(target);
        TargetAnalysis a = prepared.Analysis;
        var request = new RenderRequest(a.FundamentalHz, midi, 1.0, TimeSpan.FromSeconds(a.NoteOffSeconds), TimeSpan.FromSeconds(a.DurationSeconds - a.NoteOffSeconds), 48000);
        RenderedAudio audio = Renderer.Render(patch, request);
        return objective.Evaluate(prepared, new AudioNote(audio.Samples, audio.SampleRate, a.FundamentalHz), EvaluationLevel.Normal).Loss;
    }
}
