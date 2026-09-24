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

/// <summary>One note of a target set: what to render and what to compare it with.</summary>
/// <param name="Target">Prepared target of this note.</param>
/// <param name="Request">Note to render every candidate with; frequency, velocity and length follow the target.</param>
/// <param name="Weight">Share of this note in the aggregated loss.</param>
public sealed record TargetNote(PreparedObjectiveTarget Target, RenderRequest Request, double Weight = 1.0);

/// <summary>
/// Renders and scores patches against a target set, keeps count of the budget and caches results by patch hash. With
/// several notes, every candidate is rendered at every note and the losses are combined by weight, so that a
/// structure which only fits one octave loses against one that fits all of them. Evaluating a batch runs in
/// parallel; results do not depend on the degree of parallelism. Thread-safe.
/// </summary>
public sealed class PatchEvaluator
{
    private readonly IOperatorMatrixRenderer _renderer;
    private readonly AudioObjective _objective;
    private readonly IReadOnlyList<TargetNote> _notes;
    private readonly double _weightSum;
    private readonly double _complexityWeight;
    private readonly ComplexityOptions _complexity;
    private readonly int _maxParallel;
    private readonly ConcurrentDictionary<(string Hash, EvaluationLevel Level), PatchEvaluation> _cache = new();
    private readonly BudgetCounter _counter;

    /// <summary>Creates an evaluator for one note.</summary>
    /// <param name="renderer">Renderer of the selected synthesis profile.</param>
    /// <param name="objective">Audio objective.</param>
    /// <param name="target">Prepared target.</param>
    /// <param name="request">Note that every candidate is rendered with.</param>
    /// <param name="budget">Number of candidate evaluations allowed in total.</param>
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
        : this(renderer, objective, [new TargetNote(target ?? throw new ArgumentNullException(nameof(target)), request ?? throw new ArgumentNullException(nameof(request)))], budget, complexityWeight, complexity, maxParallel)
    {
    }

    /// <summary>Creates an evaluator for a note set.</summary>
    /// <param name="renderer">Renderer of the selected synthesis profile.</param>
    /// <param name="objective">Audio objective.</param>
    /// <param name="notes">Notes of the target set; the first one is the reference note for renders and diagnostics.</param>
    /// <param name="budget">Number of candidate evaluations allowed in total. One evaluation renders all notes.</param>
    /// <param name="complexityWeight">Weight of the complexity in the total loss.</param>
    /// <param name="complexity">Complexity weights.</param>
    /// <param name="maxParallel">Largest number of simultaneous evaluations.</param>
    public PatchEvaluator(
        IOperatorMatrixRenderer renderer,
        AudioObjective objective,
        IReadOnlyList<TargetNote> notes,
        int budget,
        double complexityWeight,
        ComplexityOptions? complexity = null,
        int maxParallel = 1)
        : this(renderer, objective, notes, budget, complexityWeight, complexity, maxParallel, new BudgetCounter())
    {
    }

    private PatchEvaluator(
        IOperatorMatrixRenderer renderer,
        AudioObjective objective,
        IReadOnlyList<TargetNote> notes,
        int budget,
        double complexityWeight,
        ComplexityOptions? complexity,
        int maxParallel,
        BudgetCounter counter)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(notes);
        if (notes.Count == 0)
        {
            throw new ArgumentException("A target set needs at least one note.", nameof(notes));
        }

        _renderer = renderer;
        _objective = objective;
        _notes = notes;
        _weightSum = notes.Sum(static n => Math.Max(0.0, n.Weight));
        if (_weightSum <= 0)
        {
            throw new ArgumentException("The note weights must not all be zero.", nameof(notes));
        }

        Budget = budget;
        _complexityWeight = complexityWeight;
        _complexity = complexity ?? new ComplexityOptions();
        _maxParallel = Math.Max(1, maxParallel);
        _counter = counter;
    }

    /// <summary>Candidate evaluations allowed in total.</summary>
    public int Budget { get; }

    /// <summary>Candidate evaluations used so far. Every evaluation counts, whatever its level; cache hits do not.</summary>
    public int Used => Volatile.Read(ref _counter.Used);

    /// <summary>Renders made so far: evaluations times notes.</summary>
    public int Renders => Volatile.Read(ref _counter.Renders);

    /// <summary>Evaluations left.</summary>
    public int Remaining => Math.Max(0, Budget - Used);

    /// <summary>Evaluations answered from the cache.</summary>
    public int CacheHits => Volatile.Read(ref _counter.CacheHits);

    /// <summary>Notes of the target set.</summary>
    public IReadOnlyList<TargetNote> Notes => _notes;

    /// <summary>Prepared target of the reference note.</summary>
    public PreparedObjectiveTarget Target => _notes[0].Target;

    /// <summary>Reference note every candidate is rendered with for diagnostics.</summary>
    public RenderRequest Request => _notes[0].Request;

    /// <summary>Envelope time limits that follow from the longest note: nothing longer can be told apart on this target set.</summary>
    public Parameters.EnvelopeTimeLimits TimeLimits => new(
        Math.Max(0.05, _notes.Max(static n => n.Request.NoteDuration.TotalSeconds)),
        Math.Max(0.1, 5.0 * _notes.Max(static n => n.Request.ReleaseDuration.TotalSeconds)));

    /// <summary>
    /// An evaluator over the reference note only that shares this budget. Structural stages use it: which structures
    /// are worth optimizing is decided as well by one note as by all, at a fraction of the renders.
    /// </summary>
    public PatchEvaluator ForReferenceNote() =>
        _notes.Count == 1 ? this : new PatchEvaluator(_renderer, _objective, [_notes[0]], Budget, _complexityWeight, _complexity, _maxParallel, _counter);

    /// <summary>Renders a patch with the reference note of this search.</summary>
    /// <param name="patch">Patch to render.</param>
    public RenderedAudio Render(OperatorMatrixPatch patch) => _renderer.Render(patch, Request);

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
                Interlocked.Increment(ref _counter.CacheHits);
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

        Interlocked.Add(ref _counter.Used, pending.Count);
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
    /// Evaluates one patch and also returns its render at the reference note, for callers that need more than the loss
    /// from the same audio. Counts as one evaluation; null when the budget is used up. Not cached.
    /// </summary>
    /// <param name="patch">Patch to evaluate.</param>
    /// <param name="level">Evaluation level.</param>
    public (PatchEvaluation Evaluation, RenderedAudio Audio)? EvaluateWithAudio(OperatorMatrixPatch patch, EvaluationLevel level)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Interlocked.Increment(ref _counter.Used) > Budget)
        {
            Interlocked.Decrement(ref _counter.Used);
            return null;
        }

        (PatchEvaluation evaluation, RenderedAudio? audio) = Measure(patch, level);
        _cache[(patch.ComputeHash(), level)] = evaluation;
        return (evaluation, audio ?? _renderer.Render(patch, Request));
    }

    private PatchEvaluation EvaluateOne(OperatorMatrixPatch patch, EvaluationLevel level) => Measure(patch, level).Evaluation;

    /// <summary>Renders the patch at every note and combines the losses; the render of the reference note is returned with them.</summary>
    private (PatchEvaluation Evaluation, RenderedAudio? Reference) Measure(OperatorMatrixPatch patch, EvaluationLevel level)
    {
        double complexity = PatchComplexity.Measure(patch, _complexity);
        double loss = 0;
        var terms = new double[8];
        var termWeights = new double[8];
        RenderedAudio? reference = null;
        try
        {
            for (int n = 0; n < _notes.Count; n++)
            {
                TargetNote note = _notes[n];
                if (note.Weight <= 0)
                {
                    continue;
                }

                RenderedAudio audio = _renderer.Render(patch, note.Request);
                Interlocked.Increment(ref _counter.Renders);
                reference ??= audio;
                ObjectiveResult result = _objective.Evaluate(note.Target, new AudioNote(audio.Samples, audio.SampleRate, note.Target.Analysis.FundamentalHz), level);
                double w = note.Weight / _weightSum;
                loss += w * result.Loss;
                Accumulate(terms, termWeights, result.Terms, w);
            }
        }
        catch (PatchNotSupportedException)
        {
            // A search must not propose such patches; if one slips through it simply loses.
            var unusable = new ObjectiveTerms(1, 1, 1, 1, 1, 1, 1, 1);
            return (new PatchEvaluation(patch, Target.Options.UnusableLoss + (_complexityWeight * complexity), Target.Options.UnusableLoss, complexity, unusable, level), null);
        }

        var combined = new ObjectiveTerms(
            Mean(terms, termWeights, 0),
            Mean(terms, termWeights, 1),
            Mean(terms, termWeights, 2),
            Mean(terms, termWeights, 3),
            Mean(terms, termWeights, 4),
            Mean(terms, termWeights, 5),
            Mean(terms, termWeights, 6),
            Mean(terms, termWeights, 7));
        return (new PatchEvaluation(patch, loss + (_complexityWeight * complexity), loss, complexity, combined, level), reference);
    }

    private static void Accumulate(double[] sums, double[] weights, ObjectiveTerms t, double w)
    {
        double[] values = [t.Stft, t.Mel, t.Partial, t.Trajectory, t.Envelope, t.Pitch, t.Ltas, t.BodyPartial];
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsNaN(values[i]))
            {
                sums[i] += w * values[i];
                weights[i] += w;
            }
        }
    }

    private static double Mean(double[] sums, double[] weights, int i) => weights[i] > 0 ? sums[i] / weights[i] : double.NaN;
}

/// <summary>Budget state shared by evaluators that draw on the same budget.</summary>
internal sealed class BudgetCounter
{
    public int Used;
    public int Renders;
    public int CacheHits;
}
