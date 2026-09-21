using System.Collections.Concurrent;
using HearSay.Audio;
using HearSay.Objective;
using HearSay.OperatorMatrix.Canonicalization;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Search;

/// <summary>Outcome of evaluating one patch.</summary>
/// <param name="Patch">Evaluated patch.</param>
/// <param name="Loss">Total loss: audio loss plus the weighted complexity.</param>
/// <param name="AudioLoss">How far the render is from the target; the dominant part.</param>
/// <param name="Complexity">Simplicity measure of the patch.</param>
/// <param name="Terms">Terms of the audio loss.</param>
/// <param name="Level">Evaluation level.</param>
public sealed record PatchEvaluation(
    OperatorMatrixPatch Patch,
    double Loss,
    double AudioLoss,
    double Complexity,
    ObjectiveTerms Terms,
    EvaluationLevel Level);

/// <summary>
/// Renders and scores patches against one prepared target, keeps count of the render budget and caches results by
/// patch hash. Evaluating a batch runs in parallel; results do not depend on the degree of parallelism. Thread-safe.
/// </summary>
public sealed class PatchEvaluator
{
    private readonly IOperatorMatrixRenderer _renderer;
    private readonly AudioObjective _objective;
    private readonly PreparedObjectiveTarget _target;
    private readonly RenderRequest _request;
    private readonly double _complexityWeight;
    private readonly ComplexityOptions _complexity;
    private readonly int _maxParallel;
    private readonly ConcurrentDictionary<(string Hash, EvaluationLevel Level), PatchEvaluation> _cache = new();
    private int _used;
    private int _cacheHits;

    /// <summary>Creates an evaluator.</summary>
    /// <param name="renderer">Renderer of the selected synthesis profile.</param>
    /// <param name="objective">Audio objective.</param>
    /// <param name="target">Prepared target.</param>
    /// <param name="request">Note that every candidate is rendered with.</param>
    /// <param name="budget">Number of renders allowed in total.</param>
    /// <param name="complexityWeight">Weight of the complexity in the total loss.</param>
    /// <param name="complexity">Complexity weights.</param>
    /// <param name="maxParallel">Largest number of simultaneous evaluations.</param>
    public PatchEvaluator(
        IOperatorMatrixRenderer renderer,
        AudioObjective objective,
        PreparedObjectiveTarget target,
        RenderRequest request,
        int budget,
        double complexityWeight,
        ComplexityOptions? complexity = null,
        int maxParallel = 1)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        _renderer = renderer;
        _objective = objective;
        _target = target;
        _request = request;
        Budget = budget;
        _complexityWeight = complexityWeight;
        _complexity = complexity ?? new ComplexityOptions();
        _maxParallel = Math.Max(1, maxParallel);
    }

    /// <summary>Renders allowed in total.</summary>
    public int Budget { get; }

    /// <summary>Renders used so far. Every render counts, whatever its level; cache hits do not.</summary>
    public int Used => Volatile.Read(ref _used);

    /// <summary>Renders left.</summary>
    public int Remaining => Math.Max(0, Budget - Used);

    /// <summary>Evaluations answered from the cache.</summary>
    public int CacheHits => Volatile.Read(ref _cacheHits);

    /// <summary>Prepared target.</summary>
    public PreparedObjectiveTarget Target => _target;

    /// <summary>Note every candidate is rendered with.</summary>
    public RenderRequest Request => _request;

    /// <summary>Envelope time limits that follow from the note: nothing longer can be told apart on this target.</summary>
    public Parameters.EnvelopeTimeLimits TimeLimits => new(
        Math.Max(0.05, _request.NoteDuration.TotalSeconds),
        Math.Max(0.1, 5.0 * _request.ReleaseDuration.TotalSeconds));

    /// <summary>Renders a patch with the note of this search.</summary>
    /// <param name="patch">Patch to render.</param>
    public RenderedAudio Render(OperatorMatrixPatch patch) => _renderer.Render(patch, _request);

    /// <summary>
    /// Evaluates patches in parallel. When the budget runs out the batch is cut short: the result then has fewer
    /// entries than the input, in input order.
    /// </summary>
    /// <param name="patches">Patches to evaluate.</param>
    /// <param name="level">Evaluation level.</param>
    public IReadOnlyList<PatchEvaluation> Evaluate(IReadOnlyList<OperatorMatrixPatch> patches, EvaluationLevel level)
    {
        ArgumentNullException.ThrowIfNull(patches);
        var results = new PatchEvaluation?[patches.Count];
        var hashes = new string[patches.Count];
        var pending = new List<int>();
        var scheduled = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicates = new List<(int Index, int Of)>();

        // The budget is assigned in input order before anything runs, so the outcome never depends on timing.
        int allowed = patches.Count;
        for (int i = 0; i < patches.Count; i++)
        {
            hashes[i] = patches[i].ComputeHash();
            if (_cache.TryGetValue((hashes[i], level), out PatchEvaluation? cached))
            {
                results[i] = cached with { Patch = patches[i] };
                Interlocked.Increment(ref _cacheHits);
            }
            else if (scheduled.TryGetValue(hashes[i], out int first))
            {
                duplicates.Add((i, first));
            }
            else if (Used + pending.Count < Budget)
            {
                scheduled[hashes[i]] = i;
                pending.Add(i);
            }
            else
            {
                allowed = i;
                break;
            }
        }

        Interlocked.Add(ref _used, pending.Count);
        Parallel.For(0, pending.Count, new ParallelOptions { MaxDegreeOfParallelism = _maxParallel }, p =>
        {
            int i = pending[p];
            PatchEvaluation evaluation = EvaluateOne(patches[i], level);
            _cache[(hashes[i], level)] = evaluation;
            results[i] = evaluation;
        });

        foreach ((int index, int of) in duplicates)
        {
            if (index < allowed && results[of] is { } original)
            {
                results[index] = original with { Patch = patches[index] };
            }
        }

        var list = new List<PatchEvaluation>(allowed);
        for (int i = 0; i < allowed; i++)
        {
            if (results[i] is { } evaluation)
            {
                list.Add(evaluation);
            }
            else
            {
                break;
            }
        }

        return list;
    }

    /// <summary>Evaluates one patch; null when the budget is used up.</summary>
    /// <param name="patch">Patch to evaluate.</param>
    /// <param name="level">Evaluation level.</param>
    public PatchEvaluation? Evaluate(OperatorMatrixPatch patch, EvaluationLevel level)
    {
        IReadOnlyList<PatchEvaluation> result = Evaluate([patch], level);
        return result.Count > 0 ? result[0] : null;
    }

    /// <summary>
    /// Evaluates one patch and also returns its render, for callers that need more than the loss from the same audio.
    /// Counts as one render; null when the budget is used up. Not cached.
    /// </summary>
    /// <param name="patch">Patch to evaluate.</param>
    /// <param name="level">Evaluation level.</param>
    public (PatchEvaluation Evaluation, RenderedAudio Audio)? EvaluateWithAudio(OperatorMatrixPatch patch, EvaluationLevel level)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Interlocked.Increment(ref _used) > Budget)
        {
            Interlocked.Decrement(ref _used);
            return null;
        }

        RenderedAudio audio = _renderer.Render(patch, _request);
        ObjectiveResult result = _objective.Evaluate(_target, new AudioNote(audio.Samples, audio.SampleRate, _target.Analysis.FundamentalHz), level);
        double complexity = PatchComplexity.Measure(patch, _complexity);
        var evaluation = new PatchEvaluation(patch, result.Loss + (_complexityWeight * complexity), result.Loss, complexity, result.Terms, level);
        _cache[(patch.ComputeHash(), level)] = evaluation;
        return (evaluation, audio);
    }

    private PatchEvaluation EvaluateOne(OperatorMatrixPatch patch, EvaluationLevel level)
    {
        double complexity = PatchComplexity.Measure(patch, _complexity);
        ObjectiveResult result;
        try
        {
            RenderedAudio audio = _renderer.Render(patch, _request);
            result = _objective.Evaluate(_target, new AudioNote(audio.Samples, audio.SampleRate, _target.Analysis.FundamentalHz), level);
        }
        catch (PatchNotSupportedException)
        {
            // A search must not propose such patches; if one slips through it simply loses.
            result = new ObjectiveResult(_target.Options.UnusableLoss, new ObjectiveTerms(1, 1, 1, 1, 1, 1, 1), level, false);
        }

        return new PatchEvaluation(patch, result.Loss + (_complexityWeight * complexity), result.Loss, complexity, result.Terms, level);
    }
}
