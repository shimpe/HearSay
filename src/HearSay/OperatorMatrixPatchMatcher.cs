using HearSay.Analysis;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Models;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.SuperCollider.FM7;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay;

/// <summary>
/// Connects synthesis profiles with the components that implement them. This is the one place that knows which
/// renderer, which prior providers and which exporter belong to a profile; the search itself never does.
/// </summary>
public static class SynthesisBackends
{
    /// <summary>Reference renderer of a profile.</summary>
    /// <param name="profile">Synthesis profile.</param>
    /// <exception cref="NotSupportedException">No renderer implements the profile.</exception>
    public static IOperatorMatrixRenderer RendererFor(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFm7.Capabilities))
        {
            return new Fm7CompatibleOperatorMatrixRenderer(profile);
        }

        if (profile.RenderProfile == RenderProfile.ExperimentalHighQuality
            && profile.Capabilities.IsSubsetOf(SynthesisProfiles.ExperimentalSineTriangle.Capabilities))
        {
            return new ExtendedOperatorMatrixRenderer(profile);
        }

        throw new NotSupportedException($"No renderer implements synthesis profile '{profile.Name}' yet.");
    }

    /// <summary>Prior providers of a profile.</summary>
    /// <param name="profile">Synthesis profile.</param>
    public static IReadOnlyList<ISynthesisPriorProvider> PriorProvidersFor(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ISynthesisPriorProvider sine = new SinePhaseModulationPriorProvider();
        return profile.Capabilities.SupportedWaveforms.Count > 1 ? [new WaveformVariantPriorProvider(sine)] : [sine];
    }

    /// <summary>Exporter of a profile; null when the profile has no export backend.</summary>
    /// <param name="profile">Synthesis profile.</param>
    public static IPatchExporter? ExporterFor(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFm7.Capabilities) ? new SuperColliderFm7Exporter() : null;
    }
}

/// <summary>Result of matching a target.</summary>
/// <param name="BestPatch">Best patch found, in canonical form.</param>
/// <param name="Comparison">Full HearSay diagnosis of the preview against the target.</param>
/// <param name="PreviewAudio">Offline render of the best patch with the note of the target.</param>
/// <param name="SearchSummary">Course of the search and reproducibility data.</param>
/// <param name="AlternativePatches">All finalists, best first; the first entry is <paramref name="BestPatch"/>.</param>
public sealed record PatchMatchResult(
    OperatorMatrixPatch BestPatch,
    ToneComparisonResult Comparison,
    RenderedAudio PreviewAudio,
    SearchSummary SearchSummary,
    IReadOnlyList<PatchCandidateResult> AlternativePatches)
{
    /// <summary>Analysis of the target.</summary>
    public required TargetAnalysis Analysis { get; init; }

    /// <summary>Note the preview was rendered with.</summary>
    public required RenderRequest Request { get; init; }

    /// <summary>Synthesis profile of the search.</summary>
    public required SynthesisProfile Profile { get; init; }
}

/// <summary>
/// Audio-to-operator-matrix solver: analyses a monophonic target, searches a patch within a synthesis profile and
/// returns it with a preview and a diagnosis. Thread-safe.
/// </summary>
public sealed class OperatorMatrixPatchMatcher
{
    private readonly IOperatorMatrixRenderer? _renderer;
    private readonly IReadOnlyList<ISynthesisPriorProvider>? _providers;

    /// <summary>Creates a matcher that picks renderer and prior providers from the synthesis profile of each search.</summary>
    public OperatorMatrixPatchMatcher()
    {
    }

    /// <summary>Creates a matcher for a custom backend.</summary>
    /// <param name="renderer">Renderer of the backend.</param>
    /// <param name="providers">Sources of structural hypotheses.</param>
    public OperatorMatrixPatchMatcher(IOperatorMatrixRenderer renderer, IReadOnlyList<ISynthesisPriorProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(providers);
        _renderer = renderer;
        _providers = providers;
    }

    /// <summary>Matches a target on a background thread.</summary>
    /// <param name="target">Target audio.</param>
    /// <param name="options">Search options.</param>
    /// <param name="progress">Receives progress.</param>
    /// <param name="cancellationToken">Stops the search early and returns the best patch so far.</param>
    public Task<PatchMatchResult> MatchAsync(AudioTarget target, PatchMatchOptions? options = null, IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Match(target, options, progress, cancellationToken), CancellationToken.None);

    /// <summary>
    /// Runs the search with several consecutive seeds and returns the result with the lowest loss. The landscape of a
    /// patch search has many optima and a single run can settle in a poor one; independent runs are the simplest
    /// remedy, and because every run is reproducible, so is the choice between them. Each run gets the full budget.
    /// </summary>
    /// <param name="target">Target audio.</param>
    /// <param name="seedCount">Number of seeds, starting at <see cref="PatchMatchOptions.RandomSeed"/>.</param>
    /// <param name="options">Search options.</param>
    /// <param name="progress">Receives progress of all runs.</param>
    /// <param name="cancellationToken">Stops the current run early and skips the remaining ones.</param>
    public PatchMatchResult MatchBestOf(AudioTarget target, int seedCount, PatchMatchOptions? options = null, IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seedCount, 1);
        options ??= new PatchMatchOptions();
        PatchMatchResult? best = null;
        for (int i = 0; i < seedCount && !(cancellationToken.IsCancellationRequested && best is not null); i++)
        {
            PatchMatchResult result = Match(target, options with { RandomSeed = options.RandomSeed + i }, progress, cancellationToken);
            if (best is null || result.AlternativePatches[0].Loss < best.AlternativePatches[0].Loss)
            {
                best = result;
            }
        }

        return best!;
    }

    /// <summary>Matches a target.</summary>
    /// <param name="target">Target audio.</param>
    /// <param name="options">Search options.</param>
    /// <param name="progress">Receives progress.</param>
    /// <param name="cancellationToken">Stops the search early and returns the best patch so far.</param>
    public PatchMatchResult Match(AudioTarget target, PatchMatchOptions? options = null, IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new PatchMatchOptions();
        IOperatorMatrixRenderer renderer = _renderer ?? SynthesisBackends.RendererFor(options.SynthesisProfile);
        IReadOnlyList<ISynthesisPriorProvider> providers = _providers ?? SynthesisBackends.PriorProvidersFor(options.SynthesisProfile);
        var coordinator = new OperatorMatrixPatchSearchCoordinator(renderer, providers);
        SearchOutcome outcome = coordinator.Search(target, options, progress, cancellationToken);

        OperatorMatrixPatch best = outcome.Candidates[0].Patch;
        RenderedAudio preview = renderer.Render(best, outcome.Request);
        var comparer = new ToneComparer();
        var targetNote = new AudioNote(outcome.Analysis.Audio.Samples, outcome.Analysis.Audio.SampleRate, outcome.Analysis.FundamentalHz);
        ToneComparisonResult comparison = comparer.Compare(
            targetNote,
            preview.ToAudioNote(outcome.Analysis.FundamentalHz),
            new ToneComparisonOptions { LoudnessMode = LoudnessMode.SeparateLoudness, ExpectedFundamentalHz = outcome.Analysis.FundamentalHz });

        return new PatchMatchResult(best, comparison, preview, outcome.Summary, outcome.Candidates)
        {
            Analysis = outcome.Analysis,
            Request = outcome.Request,
            Profile = options.SynthesisProfile,
        };
    }
}
