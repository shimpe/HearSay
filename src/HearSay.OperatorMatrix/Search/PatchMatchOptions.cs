using HearSay.Objective;
using HearSay.OperatorMatrix.Canonicalization;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Search;

/// <summary>Configuration of a patch search. The same target, options, seed and profile give the same search.</summary>
public sealed record PatchMatchOptions
{
    /// <summary>Total number of candidate evaluations the search may spend, at any evaluation level. One evaluation renders the candidate at every target note.</summary>
    public int EvaluationBudget { get; init; } = 20000;

    /// <summary>Seed of all random decisions.</summary>
    public int RandomSeed { get; init; } = 1234;

    /// <summary>Largest number of simultaneous evaluations. It changes the speed of a search, never its result.</summary>
    public int MaxParallelEvaluations { get; init; } = Environment.ProcessorCount;

    /// <summary>Synthesis profile: what may be searched and how it is rendered.</summary>
    public SynthesisProfile SynthesisProfile { get; init; } = SynthesisProfiles.SuperColliderFm7;

    /// <summary>Sample rate of analysis and rendering. Use the rate the exported patch will run at: aliasing is part of a backend-exact sound.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Key velocity of the rendered note, 0..1.</summary>
    public double Velocity { get; init; } = 1.0;

    /// <summary>Longest stretch of the target that is matched, in seconds; the rest is ignored.</summary>
    public double MaxSeconds { get; init; } = 4.0;

    /// <summary>Structural hypotheses screened at the coarse level.</summary>
    public int HypothesisCount { get; init; } = 128;

    /// <summary>Hypotheses that survive the screening and receive a short continuous optimization.</summary>
    public int SurvivorCount { get; init; } = 32;

    /// <summary>Distinct basins that receive deep optimization.</summary>
    public int BasinCount { get; init; } = 6;

    /// <summary>Basins that reach local refinement; also the number of patches reported.</summary>
    public int FinalistCount { get; init; } = 3;

    /// <summary>Weight of the patch complexity in the total loss: a tie-breaker, small next to the audio loss.</summary>
    public double ComplexityWeight { get; init; } = 0.02;

    /// <summary>Complexity weights.</summary>
    public ComplexityOptions Complexity { get; init; } = new();

    /// <summary>Audio objective options.</summary>
    public ObjectiveOptions Objective { get; init; } = new();

    /// <summary>Generations without a relative improvement of <see cref="MinimumRelativeImprovement"/> after which a continuous optimization counts as stagnated.</summary>
    public int StagnationGenerations { get; init; } = 25;

    /// <summary>Relative improvement that counts as progress.</summary>
    public double MinimumRelativeImprovement { get; init; } = 0.01;

    /// <summary>How often a stagnated basin may try topology mutations before it is left alone.</summary>
    public int MaxRestartsPerBasin { get; init; } = 3;

    /// <summary>Largest number of smooth parameters optimized together.</summary>
    public int MaxActiveParameters { get; init; } = 32;

    /// <summary>Share of the budget for screening hypotheses.</summary>
    public double ScreeningShare { get; init; } = 0.05;

    /// <summary>Share of the budget for the short optimization of the survivors.</summary>
    public double SurvivorShare { get; init; } = 0.15;

    /// <summary>Share of the budget for deep optimization of the basins.</summary>
    public double DeepShare { get; init; } = 0.35;

    /// <summary>Share of what is left after deep optimization that goes into one long continuous run per finalist. The rest goes to local refinement.</summary>
    public double FinalistShare { get; init; } = 0.75;
}

/// <summary>One patch found by a search.</summary>
/// <param name="Patch">The patch.</param>
/// <param name="Loss">Total loss at the detailed level.</param>
/// <param name="AudioLoss">Audio part of the loss.</param>
/// <param name="Complexity">Complexity of the patch.</param>
/// <param name="Terms">Terms of the audio loss.</param>
/// <param name="Regime">Structural regime.</param>
/// <param name="Origin">How the structure came about: the hypothesis and the mutations applied to it.</param>
public sealed record PatchCandidateResult(
    OperatorMatrixPatch Patch,
    double Loss,
    double AudioLoss,
    double Complexity,
    ObjectiveTerms Terms,
    OperatorMatrixRegimeKey Regime,
    string Origin);

/// <summary>What one stage of a search did.</summary>
/// <param name="Name">Stage name.</param>
/// <param name="Evaluations">Renders spent in this stage.</param>
/// <param name="BestLoss">Best total loss at the end of the stage, at the level the stage evaluates at.</param>
/// <param name="Level">Evaluation level of <paramref name="BestLoss"/>.</param>
/// <param name="Notes">Free text: counts, decisions.</param>
public sealed record SearchStageSummary(string Name, int Evaluations, double BestLoss, EvaluationLevel Level, string Notes);

/// <summary>What happened to one basin.</summary>
/// <param name="Id">Basin number.</param>
/// <param name="Origin">Hypothesis and mutations.</param>
/// <param name="Regime">Final structural regime.</param>
/// <param name="BestLoss">Best total loss at the normal level.</param>
/// <param name="Evaluations">Renders spent on the basin.</param>
/// <param name="Restarts">Times the basin stagnated and was restarted or mutated.</param>
/// <param name="Fate">How far the basin got.</param>
public sealed record BasinSummary(int Id, string Origin, string Regime, double BestLoss, int Evaluations, int Restarts, string Fate);

/// <summary>Reproducibility data and the course of a search.</summary>
public sealed record SearchSummary
{
    /// <summary>Name of the synthesis profile.</summary>
    public required string Profile { get; init; }

    /// <summary>What was minimized: "AudioLoss" or "Similarity" (one minus the tone comparer's similarity).</summary>
    public string Criterion { get; init; } = "AudioLoss";

    /// <summary>Random seed.</summary>
    public required int RandomSeed { get; init; }

    /// <summary>Render budget.</summary>
    public required int EvaluationBudget { get; init; }

    /// <summary>Candidate evaluations actually used.</summary>
    public required int EvaluationsUsed { get; init; }

    /// <summary>Renders made: evaluations times target notes.</summary>
    public required int RendersUsed { get; init; }

    /// <summary>Evaluations answered from the cache without rendering.</summary>
    public required int CacheHits { get; init; }

    /// <summary>Audio loss of the best untouched hypothesis at the normal level: where the search started.</summary>
    public required double InitialAudioLoss { get; init; }

    /// <summary>Audio loss of the best patch at the normal level.</summary>
    public required double FinalAudioLoss { get; init; }

    /// <summary>Number of hypotheses generated.</summary>
    public required int HypothesisCount { get; init; }

    /// <summary>Stages in the order they ran.</summary>
    public required IReadOnlyList<SearchStageSummary> Stages { get; init; }

    /// <summary>All basins that received deep optimization.</summary>
    public required IReadOnlyList<BasinSummary> Basins { get; init; }

    /// <summary>Wall-clock duration. The only field that differs between two runs with the same inputs.</summary>
    public required TimeSpan Elapsed { get; init; }
}
