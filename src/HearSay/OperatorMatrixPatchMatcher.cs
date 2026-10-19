using System.Globalization;
using HearSay.Analysis;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Models;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.SuperCollider.FM7;
using HearSay.SuperCollider.FMX6;
using HearSay.SuperCollider.Nome6;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Nome6Compatible.Rendering;
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

        if (profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderNome6.Capabilities))
        {
            return new Nome6CompatibleOperatorMatrixRenderer(profile);
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
    /// <param name="structureLibrarySize">
    /// Patches in the structure library whose nearest neighbours by sound become hypotheses, in a group of their own;
    /// 0 for none. Only profiles with a backend-exact renderer get one.
    /// </param>
    /// <param name="libraryProgress">Fraction of the structure library rendered, when it has to be built.</param>
    public static IReadOnlyList<ISynthesisPriorProvider> PriorProvidersFor(SynthesisProfile profile, int structureLibrarySize = 0, IProgress<double>? libraryProgress = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        IReadOnlyList<ISynthesisPriorProvider> providers = ReadingProvidersFor(profile);
        return HasStructureLibrary(profile, structureLibrarySize)
            ? [.. providers, new NearestStructurePriorProvider(StructureLibrary.For(profile, structureLibrarySize, RendererFor(profile), progress: libraryProgress))]
            : providers;
    }

    internal static bool HasStructureLibrary(SynthesisProfile profile, int size) => size > 0 && profile.RenderProfile == RenderProfile.BackendExact;

    private static IReadOnlyList<ISynthesisPriorProvider> ReadingProvidersFor(SynthesisProfile profile)
    {
        ISynthesisPriorProvider sine = new SinePhaseModulationPriorProvider();
        ISynthesisPriorProvider tracks = new PartialTrackPriorProvider(sine);
        ISynthesisPriorProvider stacks = new SinePhaseModulationPriorProvider { StackedModulatorsOnly = true };
        // Separate groups, both for the hypothesis cut and for the survivors of screening: the sine structures, the
        // stacks with a second modulator read from its lines, the operators on the measured partial tracks, the sine
        // structures with other waveforms on their carriers or on one of their modulators, the waveforms and noise read
        // from the spectrum, and both kinds of structure with the noise of a noisy target added. The best of each group
        // always gets a basin, so one kind of hypothesis cannot crowd out the others.
        IReadOnlyList<ISynthesisPriorProvider> reading = profile.Capabilities.SupportedWaveforms.Count > 1
            ? [sine, stacks, tracks, new WaveformVariantPriorProvider(sine, includeInner: false), new HarmonicWaveformPriorProvider(), new ModulatorWaveformPriorProvider(sine), new NoisePriorProvider(sine), new NoisePriorProvider(tracks)]
            : [sine, stacks, tracks];

        // Filtered carriers read from the spectral envelope, each kind in a group of its own.
        return profile.Capabilities.SupportsOperatorFilters
            ? [.. reading, new FormantPriorProvider(), new SubtractivePriorProvider()]
            : reading;
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

        if (profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderFmx6.Capabilities))
        {
            return new SuperColliderFmx6Exporter();
        }

        return profile.Capabilities.IsSubsetOf(SynthesisProfiles.SuperColliderNome6.Capabilities) ? new SuperColliderNome6Exporter() : null;
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

    /// <summary>
    /// Every target note against another patch of the profile, such as another finalist: rendered and diagnosed as
    /// <see cref="Notes"/> are for the best patch, in the same order. Renders once per note; not for an interface thread.
    /// </summary>
    /// <param name="patch">Patch.</param>
    /// <exception cref="NotSupportedException">No renderer implements the profile.</exception>
    public IReadOnlyList<NoteMatch> CompareNotes(OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        IOperatorMatrixRenderer renderer = SynthesisBackends.RendererFor(Profile);
        return [.. Notes.Select(n => NoteMatch.Of(renderer, patch, n.Analysis, n.Request))];
    }
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
        var runs = new List<(int Seed, PatchMatchResult Result)>();
        for (int i = 0; i < seedCount && !(cancellationToken.IsCancellationRequested && best is not null); i++)
        {
            int run = i + 1;
            int seed = options.RandomSeed + i;
            double previous = best?.AlternativePatches[0].Loss ?? double.NaN;
            IProgress<SearchProgress>? runProgress = progress is null
                ? null
                : new RelayProgress<SearchProgress>(p => progress.Report(p with { Run = run, RunCount = seedCount, BestLossOfPreviousRuns = previous }));
            PatchMatchResult result = Match(targets, options with { RandomSeed = seed }, runProgress, cancellationToken);
            runs.Add((seed, result));
            if (best is null || result.AlternativePatches[0].Loss < best.AlternativePatches[0].Loss)
            {
                best = result;
            }
        }

        if (runs.Count == 1)
        {
            return best!;
        }

        // The result of the winning seed, with the finalists of every seed as alternatives: one entry per structure,
        // the best of each, best first. The winning patch stays first; ties go to the winning seed.
        var candidates = new List<(int Seed, PatchCandidateResult Candidate)>();
        foreach ((int seed, PatchMatchResult result) in runs.OrderBy(r => !ReferenceEquals(r.Result, best)))
        {
            candidates.AddRange(result.AlternativePatches.Select(c => (seed, c)));
        }

        var merged = new List<PatchCandidateResult>();
        var structures = new HashSet<string>(StringComparer.Ordinal);
        foreach ((int seed, PatchCandidateResult candidate) in candidates.OrderBy(static c => c.Candidate.Loss))
        {
            if (structures.Add(candidate.Regime.Value))
            {
                merged.Add(candidate with { Origin = FormattableString.Invariant($"seed {seed}: {candidate.Origin}") });
            }
        }

        return best! with { AlternativePatches = merged };
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
        IProgress<double>? building = null;
        if (_providers is null && progress is not null && SynthesisBackends.HasStructureLibrary(options.SynthesisProfile, options.StructureLibrarySize)
            && StructureLibrary.SoundsToRender(options.SynthesisProfile, options.StructureLibrarySize) is int toRender and > 0)
        {
            // Building takes minutes: say so, with the share done and the time left.
            var clock = new RemainingTime();
            string what = string.Create(CultureInfo.InvariantCulture, $"building the structure library of {options.SynthesisProfile.Name}: {toRender:N0} sounds to render, once; ");
            int budget = options.EvaluationBudget;
            building = new RelayProgress<double>(f => progress.Report(new SearchProgress(what + clock.Describe(f), 0, budget, double.NaN) { Preparation = f }));
            building.Report(0.0);
        }

        IReadOnlyList<ISynthesisPriorProvider> providers = _providers ?? SynthesisBackends.PriorProvidersFor(options.SynthesisProfile, options.StructureLibrarySize, building);
        var coordinator = new OperatorMatrixPatchSearchCoordinator(renderer, providers);
        SearchOutcome outcome = coordinator.Search(targets, options, progress, cancellationToken);

        OperatorMatrixPatch best = outcome.Candidates[0].Patch;
        var notes = new List<NoteMatch>(outcome.Analyses.Count);
        for (int i = 0; i < outcome.Analyses.Count; i++)
        {
            notes.Add(NoteMatch.Of(renderer, best, outcome.Analyses[i], outcome.Requests[i]));
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
/// <param name="Preview">Offline render of the patch at this note.</param>
/// <param name="Comparison">HearSay diagnosis of the preview against this target note.</param>
public sealed record NoteMatch(TargetAnalysis Analysis, RenderRequest Request, RenderedAudio Preview, ToneComparisonResult Comparison)
{
    /// <summary>Renders a patch with the note of a target and diagnoses it against that target note.</summary>
    /// <param name="renderer">Renderer of the patch's profile.</param>
    /// <param name="patch">Patch.</param>
    /// <param name="analysis">Analysis of the target note.</param>
    /// <param name="request">Note to render it with.</param>
    public static NoteMatch Of(IOperatorMatrixRenderer renderer, OperatorMatrixPatch patch, TargetAnalysis analysis, RenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(request);
        RenderedAudio preview = renderer.Render(patch, request);
        var targetNote = new AudioNote(analysis.Audio.Samples, analysis.Audio.SampleRate, analysis.FundamentalHz);
        ToneComparisonResult comparison = new ToneComparer().Compare(
            targetNote,
            preview.ToAudioNote(analysis.FundamentalHz),
            new ToneComparisonOptions { LoudnessMode = LoudnessMode.SeparateLoudness, ExpectedFundamentalHz = analysis.FundamentalHz });
        return new NoteMatch(analysis, request, preview, comparison);
    }
}

/// <summary>Progress that forwards synchronously to a delegate.</summary>
internal sealed class RelayProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
