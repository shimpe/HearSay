using HearSay.Analysis;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Models;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.SuperCollider.FM7;
using HearSay.SuperCollider.FMX6;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
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

        if (profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFmx6.Capabilities))
        {
            return new Fmx6CompatibleOperatorMatrixRenderer(profile);
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
        return profile.Capabilities.SupportedWaveforms.Count > 1
            ? [new WaveformVariantPriorProvider(sine), new HarmonicWaveformPriorProvider()]
            : [sine];
    }

    /// <summary>
    /// Exporter of a profile; null when the profile has no export backend. Only a profile that is rendered
    /// backend-exact gets one: what was heard during the search is then what the export plays.
    /// </summary>
    /// <param name="profile">Synthesis profile.</param>
    public static IPatchExporter? ExporterFor(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.RenderProfile != RenderProfile.BackendExact)
        {
            return null;
        }

        if (profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFm7.Capabilities))
        {
            return new SuperColliderFm7Exporter();
        }

        return profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFmx6.Capabilities) ? new SuperColliderFmx6Exporter() : null;
    }
}

/// <summary>Result of matching a target.</summary>
/// <param name="BestPatch">Best patch found, in canonical form.</param>
/// <param name="Comparison">Full HearSay diagnosis of the preview against the reference target note.</param>
/// <param name="PreviewAudio">Offline render of the best patch with the reference note.</param>
/// <param name="SearchSummary">Course of the search and reproducibility data.</param>
/// <param name="AlternativePatches">All finalists, best first; the first entry is <paramref name="BestPatch"/>.</param>
public sealed record PatchMatchResult(
    OperatorMatrixPatch BestPatch,
    ToneComparisonResult Comparison,
    RenderedAudio PreviewAudio,
    SearchSummary SearchSummary,
    IReadOnlyList<PatchCandidateResult> AlternativePatches)
{
    /// <summary>Analysis of the reference note.</summary>
    public required TargetAnalysis Analysis { get; init; }

    /// <summary>Reference note the preview was rendered with.</summary>
    public required RenderRequest Request { get; init; }

    /// <summary>Synthesis profile of the search.</summary>
    public required SynthesisProfile Profile { get; init; }

    /// <summary>All target notes with their previews and diagnoses, in the order the targets were given. One entry for a single-note match.</summary>
    public IReadOnlyList<NoteMatch> Notes { get; init; } = [];
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

    /// <summary>Matches a set of target notes on a background thread.</summary>
    /// <param name="targets">Target notes.</param>
    /// <param name="options">Search options.</param>
    /// <param name="progress">Receives progress.</param>
    /// <param name="cancellationToken">Stops the search early and returns the best patch so far.</param>
    public Task<PatchMatchResult> MatchAsync(IReadOnlyList<AudioTarget> targets, PatchMatchOptions? options = null, IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Match(targets, options, progress, cancellationToken), CancellationToken.None);

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
        ArgumentNullException.ThrowIfNull(target);
        return MatchBestOf([target], seedCount, options, progress, cancellationToken);
    }

    /// <summary>Runs the search of a target set with several consecutive seeds and keeps the best result.</summary>
    /// <param name="targets">Target notes.</param>
    /// <param name="seedCount">Number of seeds, starting at <see cref="PatchMatchOptions.RandomSeed"/>.</param>
    /// <param name="options">Search options.</param>
    /// <param name="progress">Receives progress of all runs.</param>
    /// <param name="cancellationToken">Stops the current run early and skips the remaining ones.</param>
    public PatchMatchResult MatchBestOf(IReadOnlyList<AudioTarget> targets, int seedCount, PatchMatchOptions? options = null, IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seedCount, 1);
        options ??= new PatchMatchOptions();
        PatchMatchResult? best = null;
        for (int i = 0; i < seedCount && !(cancellationToken.IsCancellationRequested && best is not null); i++)
        {
            int run = i + 1;
            double previous = best?.AlternativePatches[0].Loss ?? double.NaN;
            IProgress<SearchProgress>? runProgress = progress is null
                ? null
                : new RelayProgress(p => progress.Report(p with { Run = run, RunCount = seedCount, BestLossOfPreviousRuns = previous }));
            PatchMatchResult result = Match(targets, options with { RandomSeed = options.RandomSeed + i }, runProgress, cancellationToken);
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
        return Match([target], options, progress, cancellationToken);
    }

    /// <summary>
    /// Matches a set of target notes, for example the same sound at several octaves and velocities, with one patch.
    /// Every candidate is judged on all notes, so the structure has to fit the whole set; key scaling, velocity
    /// sensitivity and fixed-frequency operators are searched as soon as the set makes them audible.
    /// </summary>
    /// <param name="targets">Target notes, each with its own pitch and velocity when known.</param>
    /// <param name="options">Search options. The budget counts candidate evaluations; one evaluation renders all notes.</param>
    /// <param name="progress">Receives progress.</param>
    /// <param name="cancellationToken">Stops the search early and returns the best patch so far.</param>
    public PatchMatchResult Match(IReadOnlyList<AudioTarget> targets, PatchMatchOptions? options = null, IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        options ??= new PatchMatchOptions();
        IOperatorMatrixRenderer renderer = _renderer ?? SynthesisBackends.RendererFor(options.SynthesisProfile);
        IReadOnlyList<ISynthesisPriorProvider> providers = _providers ?? SynthesisBackends.PriorProvidersFor(options.SynthesisProfile);
        var coordinator = new OperatorMatrixPatchSearchCoordinator(renderer, providers);
        SearchOutcome outcome = coordinator.Search(targets, options, progress, cancellationToken);

        OperatorMatrixPatch best = outcome.Candidates[0].Patch;
        var comparer = new ToneComparer();
        var notes = new List<NoteMatch>(outcome.Analyses.Count);
        for (int i = 0; i < outcome.Analyses.Count; i++)
        {
            TargetAnalysis analysis = outcome.Analyses[i];
            RenderRequest request = outcome.Requests[i];
            RenderedAudio preview = renderer.Render(best, request);
            var targetNote = new AudioNote(analysis.Audio.Samples, analysis.Audio.SampleRate, analysis.FundamentalHz);
            ToneComparisonResult comparison = comparer.Compare(
                targetNote,
                preview.ToAudioNote(analysis.FundamentalHz),
                new ToneComparisonOptions { LoudnessMode = LoudnessMode.SeparateLoudness, ExpectedFundamentalHz = analysis.FundamentalHz });
            notes.Add(new NoteMatch(analysis, request, preview, comparison));
        }

        int reference = outcome.Requests.ToList().IndexOf(outcome.Request);
        NoteMatch main = notes[Math.Max(0, reference)];
        return new PatchMatchResult(best, main.Comparison, main.Preview, outcome.Summary, outcome.Candidates)
        {
            Analysis = main.Analysis,
            Request = main.Request,
            Profile = options.SynthesisProfile,
            Notes = notes,
        };
    }
}

/// <summary>One target note of a match: its analysis, the note it was rendered with, the preview and the diagnosis.</summary>
/// <param name="Analysis">Analysis of the target note.</param>
/// <param name="Request">Note the preview was rendered with.</param>
/// <param name="Preview">Offline render of the best patch at this note.</param>
/// <param name="Comparison">HearSay diagnosis of the preview against this target note.</param>
public sealed record NoteMatch(TargetAnalysis Analysis, RenderRequest Request, RenderedAudio Preview, ToneComparisonResult Comparison);

/// <summary>Progress that forwards synchronously to a delegate.</summary>
internal sealed class RelayProgress(Action<SearchProgress> report) : IProgress<SearchProgress>
{
    public void Report(SearchProgress value) => report(value);
}
