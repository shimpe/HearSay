using HearSay.Analysis;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.IntegrationTests;

/// <summary>The diagnosis of any finalist against the target notes, as the search gives it for the best one; a small search.</summary>
public sealed class FinalistComparisonTests
{
    private static readonly Lazy<PatchMatchResult> Result = new(() =>
    {
        ReferencePatch reference = ReferencePatches.Find("simple-2op")!;
        RenderedAudio audio = new Fm7CompatibleOperatorMatrixRenderer().Render(reference.Patch, RenderRequest.Create(reference.FrequencyHz, reference.NoteSeconds, reference.ReleaseSeconds));
        return new OperatorMatrixPatchMatcher().Match(new AudioTarget(audio.Samples, audio.SampleRate, reference.FrequencyHz), new PatchMatchOptions { EvaluationBudget = 300, StructureLibrarySize = 0, RandomSeed = 3 });
    });

    [Fact]
    public void CompareNotes_OfTheBestPatch_GivesTheSearchsOwnDiagnosis()
    {
        PatchMatchResult result = Result.Value;

        IReadOnlyList<NoteMatch> notes = result.CompareNotes(result.BestPatch);

        Assert.Equal(result.Notes.Count, notes.Count);
        for (int i = 0; i < notes.Count; i++)
        {
            Assert.Equal(result.Notes[i].Request, notes[i].Request);
            Assert.Equal(result.Notes[i].Comparison.Scores.Similarity, notes[i].Comparison.Scores.Similarity, 12);
        }
    }

    [Fact]
    public void CompareNotes_OfAnotherFinalist_IsItsOwn()
    {
        PatchMatchResult result = Result.Value;
        Assert.True(result.AlternativePatches.Count > 1);
        PatchCandidateResult other = result.AlternativePatches[^1];

        IReadOnlyList<NoteMatch> notes = result.CompareNotes(other.Patch);

        double similarity = Assert.Single(notes).Comparison.Scores.Similarity;
        Assert.InRange(similarity, 0.0, 1.0);
        Assert.NotEqual(result.Comparison.Scores.Similarity, similarity);
    }
}
