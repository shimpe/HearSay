using System.Globalization;
using HearSay;
using HearSay.Comparison;
using HearSay.Models;
using HearSay.Modeling;
using HearSay.Modeling.Guidance;
using HearSay.Modeling.History;
using HearSay.Modeling.Parameters;
using HearSay.Modeling.Sensitivity;
using HearSay.OptimizationDemo;

// Closed-loop demo without any external synth: HearSay measures, a deliberately simple optimizer decides.
// The optimizer knows nothing about the synth; everything it uses comes out of the library:
//   1. exploration  - one isolated probe per parameter teaches the effect model what each parameter does
//   2. exploitation - follow the adjustment suggestions; shrink the step when a step makes things worse
//   3. re-explore   - when the advisor reports that a relevant parameter is poorly understood at the new position
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
bool verbose = args.Contains("--verbose");
int maxIterations = 30;

var hidden = new SynthParameterState(new Dictionary<string, double>
{
    ["brightness"] = 0.72, ["attack"] = 0.12, ["release"] = 0.55, ["noise"] = 0.15, ["harmonic2"] = 0.35,
});
var start = new SynthParameterState(new Dictionary<string, double>
{
    ["brightness"] = 0.30, ["attack"] = 0.50, ["release"] = 0.15, ["noise"] = 0.60, ["harmonic2"] = 0.00,
});

var comparer = new ToneComparer();
PreparedTarget target = comparer.PrepareTarget(DemoSynth.Render(hidden));
var session = new ToneOptimizationSession(target, DemoSynth.Parameters);

Console.WriteLine("HearSay closed-loop demo");
Console.WriteLine($"Target parameters (hidden from the optimizer): {Describe(hidden)}");
Console.WriteLine($"Start parameters:                              {Describe(start)}");
Console.WriteLine();

ToneComparisonResult first = Evaluate(start, OptimizationStepType.Validation, "start");
PrintIteration(first, null);

// Phase 1: exploration.
Console.WriteLine("--- Exploration: one isolated probe per parameter ---");
foreach (SynthParameter parameter in session.ParameterSpace.Parameters)
{
    Probe(start, parameter, 0.06);
}

PrintSensitivities(session.EstimateJacobian(start));

// Phase 2: exploitation with a simple trust region.
Console.WriteLine("--- Exploitation: follow the suggestions ---");
double trust = 0.15;
while (session.History.Count < maxIterations)
{
    OptimizationObservation best = session.History.Best!;
    GuidanceResult guidance = session.SuggestAdjustments(best, session.Options.Guidance with { MaximumNormalizedStep = trust });
    var empirical = guidance.Suggestions.Where(s => s.Source == SuggestionSource.Empirical).ToList();
    if (empirical.Count == 0 || trust < 0.004)
    {
        Console.WriteLine("No further confident suggestions; stopping.");
        break;
    }

    SynthParameterState next = best.Parameters;
    foreach (ParameterAdjustmentSuggestion suggestion in empirical)
    {
        next = next.With(suggestion.ParameterId, suggestion.SuggestedValue);
    }

    ToneComparisonResult result = Evaluate(next, OptimizationStepType.Exploitation, null);
    bool improved = ReferenceEquals(session.History.Best, session.History.Latest);
    PrintIteration(result, empirical);
    Console.WriteLine($"  Predicted error norm {guidance.CurrentErrorNorm:F2} -> {guidance.PredictedErrorNorm:F2}; step {(improved ? "accepted" : "rejected, shrinking trust region")}");
    Console.WriteLine();

    if (improved)
    {
        trust = Math.Min(0.15, trust * 1.5);
    }
    else
    {
        // Phase 3: a rejected step means the local model was wrong. Take smaller steps, and ask the advisor which
        // parameter is least understood around the best state; probing it is worth more than another blind step.
        trust *= 0.5;
        ExperimentSuggestion? experiment = session.SuggestExperiments()
            .FirstOrDefault(e => e.StepType == OptimizationStepType.Exploration && e.ExpectedInformationGain > 0.5);
        if (experiment is not null && session.History.Count < maxIterations)
        {
            SynthParameter parameter = session.ParameterSpace.Find(experiment.ParameterId)!;
            Console.WriteLine($"  Advisor: explore '{parameter.Id}' (information gain {experiment.ExpectedInformationGain:F2})");
            Probe(session.History.Best!.Parameters, parameter, 0.04);
        }
    }

    OptimizationProgress progress = session.GetProgress()!;
    if (progress.BestLoss < 0.01)
    {
        Console.WriteLine("Loss below 0.01; stopping.");
        break;
    }
}

// Summary.
OptimizationProgress final = session.GetProgress()!;
Console.WriteLine();
Console.WriteLine("=== Summary ===");
Console.WriteLine($"Evaluations: {final.Iterations}");
Console.WriteLine($"Loss: {first.Scores.Overall:F3} -> {final.BestLoss:F3} (iteration {final.BestIteration})");
Console.WriteLine($"Found:  {Describe(session.History.Best!.Parameters)}");
Console.WriteLine($"Hidden: {Describe(hidden)}");
Console.WriteLine($"Stagnating: {final.IsStagnating}; oscillating: {string.Join(", ", session.DetectOscillations().Select(o => o.Id).DefaultIfEmpty("none"))}");
Console.WriteLine();
Console.WriteLine("Loss per evaluation:");
Console.WriteLine("  " + string.Join(" ", session.History.Observations.Select(o => o.OverallLoss.ToString("F3"))));
Console.WriteLine();
Console.WriteLine("Strongest observed parameter-feature correlations (not proof of causation):");
foreach (ParameterFeatureRelationship r in session.GetRelationships(0.6).Take(8))
{
    Console.WriteLine($"  {r.ParameterId,-11} {r.FeatureId,-26} r = {r.Correlation,6:+0.00;-0.00}  confidence {r.Confidence:F2}  n = {r.ObservationCount}");
}

Console.WriteLine();
PrintSensitivities(session.EstimateJacobian());

if (verbose)
{
    Console.WriteLine("Session state (checkpoint JSON):");
    Console.WriteLine(session.ExportState());
}
else
{
    string json = session.ExportState();
    ToneOptimizationSession restored = ToneOptimizationSession.ImportState(json, DemoSynth.Render(hidden));
    Console.WriteLine($"Checkpoint: {json.Length / 1024} KiB of JSON; restored session has {restored.History.Count} observations. Use --verbose to print it.");
}

return final.BestLoss < 0.5 * first.Scores.Overall ? 0 : 1;

ToneComparisonResult Evaluate(SynthParameterState state, OptimizationStepType type, string? id)
{
    long started = System.Diagnostics.Stopwatch.GetTimestamp();
    var audio = DemoSynth.Render(state);
    return session.Evaluate(state, audio, type, id, System.Diagnostics.Stopwatch.GetElapsedTime(started));
}

void Probe(SynthParameterState around, SynthParameter parameter, double normalizedStep)
{
    double current = around.Values[parameter.Id];
    double direction = current + normalizedStep <= parameter.MaxValue ? 1.0 : -1.0;
    OptimizationObservation before = session.History.Observations.Last(o => o.Parameters == around || o.Parameters.Values.SequenceEqual(around.Values));
    SynthParameterState probe = around.With(parameter.Id, parameter.Coerce(current + (direction * normalizedStep * parameter.Range)));
    Evaluate(probe, OptimizationStepType.Exploration, $"probe:{parameter.Id}");
    OptimizationObservation after = session.History.Latest!;

    var analyzer = new OptimizationHistoryAnalyzer();
    ParameterChange change = analyzer.GetParameterChanges(before, after, session.ParameterSpace).Single();
    var strongest = analyzer.GetFeatureChanges(before, after)
        .OrderByDescending(c => Math.Abs(c.Delta) / target.Schema.Features[target.Schema.IndexOf(c.FeatureId)].TypicalScale)
        .Take(3);
    Console.WriteLine($"Iteration {after.Iteration}  (exploration)  loss {after.OverallLoss:F3}");
    Console.WriteLine($"  Observed: {change.ParameterId} {change.NormalizedDelta:+0.00;-0.00}");
    foreach (FeatureChange c in strongest)
    {
        FeatureDefinition definition = target.Schema.Features[target.Schema.IndexOf(c.FeatureId)];
        Console.WriteLine($"    -> {c.FeatureId} {c.Delta:+0.0;-0.0;0.0} {definition.Unit}");
    }

    Console.WriteLine();
}

void PrintIteration(ToneComparisonResult result, List<ParameterAdjustmentSuggestion>? applied)
{
    OptimizationObservation observation = session.History.Latest!;
    Console.WriteLine($"Iteration {observation.Iteration}  loss {result.Scores.Overall:F3}  (spectral {result.Scores.Spectral:F3}, perceptual {result.Scores.Perceptual:F3}, harmonic {result.Scores.Harmonic:F3}, envelope {result.Scores.Envelope:F3})");
    if (applied is not null)
    {
        Console.WriteLine("  Applied: " + string.Join(", ", applied.Select(s => $"{s.ParameterId} {s.SuggestedDelta:+0.000;-0.000} (conf {s.Confidence:F2})")));
    }

    Console.WriteLine("  Main errors:");
    foreach (ToneDifference d in result.Differences.Take(4))
    {
        string range = d.FrequencyStartHz is double a && d.FrequencyEndHz is double b && a != b ? $" [{a:F0}-{b:F0} Hz]" : string.Empty;
        string region = d.TimeRegion is null ? string.Empty : $" during {d.TimeRegion}";
        Console.WriteLine($"    {d.Feature,-24} {d.Direction,-9} {d.SignedDelta,8:+0.0;-0.0;0.0} {d.Unit}{range}{region}  importance {d.Importance:F2}");
    }

    if (applied is null)
    {
        Console.WriteLine();
    }
}

void PrintSensitivities(ParameterFeatureJacobian jacobian)
{
    Console.WriteLine("Learned local sensitivities (feature units per full parameter range):");
    for (int p = 0; p < jacobian.ParameterIds.Count; p++)
    {
        var entries = new List<(string Feature, double Normalized, double Value, double Confidence)>();
        for (int f = 0; f < jacobian.FeatureIds.Count; f++)
        {
            double normalized = jacobian.Values[f, p] / target.Schema.Features[f].TypicalScale * jacobian.Confidence[f, p];
            entries.Add((jacobian.FeatureIds[f], normalized, jacobian.Values[f, p], jacobian.Confidence[f, p]));
        }

        foreach ((string feature, _, double value, double confidence) in entries.OrderByDescending(e => Math.Abs(e.Normalized)).Take(2))
        {
            string unit = target.Schema.Features[target.Schema.IndexOf(feature)].Unit;
            Console.WriteLine($"  {jacobian.ParameterIds[p],-11} -> {feature,-24} = {value,9:+0.0;-0.0} {unit} / unit   confidence {confidence:F2}");
        }
    }

    Console.WriteLine();
}

static string Describe(SynthParameterState state) =>
    string.Join(", ", state.Values.Select(kv => $"{kv.Key}={kv.Value:F2}"));
