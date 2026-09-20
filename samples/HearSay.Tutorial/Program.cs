using System.Globalization;
using HearSay;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Features;
using HearSay.Models;
using HearSay.Optimization;
using HearSay.Optimization.Guidance;
using HearSay.Optimization.History;
using HearSay.Optimization.Parameters;
using HearSay.Optimization.Sensitivity;
using HearSay.Tutorial;

// Companion program of docs/tutorial.md. Every step of the tutorial is one method below.
//   dotnet run --project samples/HearSay.Tutorial            runs all steps
//   dotnet run --project samples/HearSay.Tutorial -- 5       runs step 5 only
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var steps = new (string Title, Action Run)[]
{
    ("Step 1 - a first comparison", Steps.FirstComparison),
    ("Step 2 - reading the differences", Steps.ReadingDifferences),
    ("Step 3 - looking at the detail", Steps.Detail),
    ("Step 4 - options", Steps.Options),
    ("Step 5 - WAV files and JSON", Steps.WavAndJson),
    ("Step 6 - one target, many candidates", Steps.PreparedTargets),
    ("Step 7 - feature vectors", Steps.FeatureVectors),
    ("Step 8 - a session: learning what the parameters do", Steps.LearnParameters),
    ("Step 9 - closing the loop", Steps.CloseTheLoop),
    ("Step 10 - your own optimizer", Steps.OwnOptimizer),
    ("Step 11 - checkpoints", Steps.Checkpoints),
};

int? only = args.Length > 0 && int.TryParse(args[0], out int n) ? n : null;
for (int i = 0; i < steps.Length; i++)
{
    if (only is null || only == i + 1)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {steps[i].Title} ===");
        steps[i].Run();
    }
}

internal static class Steps
{
    private static readonly ToneComparer Comparer = new();

    // The sound we want, and a first attempt that is too dark and too slow.
    private static readonly SynthParameterState Wanted = TutorialSynth.State(cutoff: 5000, attack: 10, waveform: 1);
    private static readonly SynthParameterState Attempt = TutorialSynth.State(cutoff: 1500, attack: 80, waveform: 1);

    public static void FirstComparison()
    {
        AudioNote target = TutorialSynth.Render(Wanted);
        AudioNote candidate = TutorialSynth.Render(Attempt);

        ToneComparisonResult result = Comparer.Compare(target, candidate);

        Console.WriteLine($"overall distance {result.Scores.Overall:F3}  (0 = perfect)");
        Console.WriteLine($"similarity       {result.Scores.Similarity:F2}   (1 = perfect)");
        Console.WriteLine($"spectral {result.Scores.Spectral:F3}  perceptual {result.Scores.Perceptual:F3}  harmonic {result.Scores.Harmonic:F3}  envelope {result.Scores.Envelope:F3}  loudness {result.Scores.Loudness:F3}");
    }

    public static void ReadingDifferences()
    {
        ToneComparisonResult result = Comparer.Compare(TutorialSynth.Render(Wanted), TutorialSynth.Render(Attempt));

        foreach (ToneDifference d in result.Differences.Take(6))
        {
            string where = d.FrequencyStartHz is double a && d.FrequencyEndHz is double b && a != b ? $" [{a:F0}-{b:F0} Hz]" : string.Empty;
            string when = d.TimeRegion is null ? string.Empty : $" during {d.TimeRegion}";
            Console.WriteLine($"{d.Feature,-26} {d.Direction,-9} {d.SignedDelta,8:+0.0;-0.0} {d.Unit}{where}{when}  importance {d.Importance:F2}  confidence {d.Confidence:F2}");
        }

        Console.WriteLine();
        foreach (ParameterHint hint in result.ParameterHints.Take(3))
        {
            Console.WriteLine($"hint: {hint.Feature} -> {hint.SuggestedDirection} via {string.Join(", ", hint.PotentialParameterCategories)}  (confidence {hint.Confidence:F2})");
        }
    }

    public static void Detail()
    {
        ToneComparisonResult result = Comparer.Compare(TutorialSynth.Render(Wanted), TutorialSynth.Render(Attempt));

        EnvelopeStageComparison attack = result.Envelope.Attack;
        Console.WriteLine($"attack: target {attack.Target:F1} ms, candidate {attack.Candidate:F1} ms, delta {attack.Difference:+0.0;-0.0} ms, confidence {attack.Confidence:F2}");
        Console.WriteLine($"release delta: {(result.Envelope.Release.Difference is double r ? $"{r:+0.0;-0.0} ms" : "not measurable")}");

        Console.WriteLine("harmonics (relative level, dB):");
        foreach (HarmonicComparison h in result.Harmonics.Where(h => h.HarmonicNumber is 1 or 3 or 9 or 15))
        {
            Console.WriteLine($"  h{h.HarmonicNumber,-2} {h.FrequencyHz,6:F0} Hz  target {h.TargetLevelDb,6:F1}  candidate {h.CandidateLevelDb,6:F1}  delta {h.DifferenceDb,6:+0.0;-0.0}");
        }

        Console.WriteLine("bands:");
        foreach (FrequencyBandDifference band in result.FrequencyBands)
        {
            Console.WriteLine($"  {band.Name,-9} {band.StartHz,5:F0}-{band.EndHz,5:F0} Hz  delta {band.DifferenceDb,6:+0.0;-0.0} dB");
        }

        Console.WriteLine("brightness per time segment:");
        foreach (TimeSegmentComparison segment in result.TimeSegments)
        {
            Console.WriteLine($"  {segment.Segment,-8} centroid delta {segment.CentroidDifferenceHz,7:+0;-0} Hz");
        }

        foreach (AnalysisWarning warning in result.Quality.Warnings)
        {
            Console.WriteLine($"warning {warning.Code} ({warning.Source})");
        }
    }

    public static void Options()
    {
        AudioNote target = TutorialSynth.Render(Wanted);
        float[] louder = target.Samples.ToArray();
        for (int i = 0; i < louder.Length; i++)
        {
            louder[i] *= 1.6f;
        }

        var candidate = new AudioNote(louder, target.SampleRate, target.ExpectedFundamentalHz);

        foreach (LoudnessMode mode in Enum.GetValues<LoudnessMode>())
        {
            ToneComparisonResult r = Comparer.Compare(target, candidate, new ToneComparisonOptions { LoudnessMode = mode });
            Console.WriteLine($"{mode,-16} overall {r.Scores.Overall:F3}  spectral {r.Scores.Spectral:F3}  loudness delta {r.DifferenceVector.LoudnessDelta:+0.0;-0.0} dB  differences {r.Differences.Count}");
        }

        // A configuration that cares about the envelope more than about fine spectral detail.
        var custom = new ToneComparisonOptions
        {
            ExpectedFundamentalHz = TutorialSynth.NoteHz,
            Weights = new ScoreWeights { Spectral = 0.15, Perceptual = 0.25, Harmonic = 0.15, Envelope = 0.40, Loudness = 0.05 },
            Bands = new BandOptions
            {
                Bands = [new("body", 80, 1000), new("presence", 1000, 5000), new("top", 5000, 20000)],
            },
            Differences = new DifferenceOptions { MinimumLevelDifferenceDb = 2.0, MaximumDifferences = 5 },
        };
        ToneComparisonResult c = Comparer.Compare(target, TutorialSynth.Render(Attempt), custom);
        Console.WriteLine($"custom options: overall {c.Scores.Overall:F3}; bands {string.Join(", ", c.FrequencyBands.Select(b => $"{b.Name} {b.DifferenceDb:+0.0;-0.0}"))}");
    }

    public static void WavAndJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-tutorial");
        Directory.CreateDirectory(directory);
        string targetPath = Path.Combine(directory, "target.wav"), candidatePath = Path.Combine(directory, "candidate.wav");
        WavFile.Write(targetPath, TutorialSynth.Render(Wanted).Samples.Span, TutorialSynth.SampleRate);
        WavFile.Write(candidatePath, TutorialSynth.Render(Attempt).Samples.Span, TutorialSynth.SampleRate);

        AudioNote target = WavFile.Read(targetPath, expectedFundamentalHz: TutorialSynth.NoteHz);
        AudioNote candidate = WavFile.Read(candidatePath, expectedFundamentalHz: TutorialSynth.NoteHz);
        ToneComparisonResult result = Comparer.Compare(target, candidate);

        string json = HearSayJson.Serialize(result);
        string jsonPath = Path.Combine(directory, "comparison.json");
        File.WriteAllText(jsonPath, json);
        ToneComparisonResult restored = HearSayJson.Deserialize<ToneComparisonResult>(json);

        Console.WriteLine($"wrote {jsonPath} ({json.Length / 1024} KiB)");
        Console.WriteLine($"round trip: {restored.Differences.Count} differences, overall {restored.Scores.Overall:F3}");
    }

    public static void PreparedTargets()
    {
        PreparedTarget prepared = Comparer.PrepareTarget(TutorialSynth.Render(Wanted));
        double[] cutoffs = [1000, 2000, 3000, 4000, 5000, 6000, 8000, 10000];
        var losses = new double[cutoffs.Length];

        // The prepared target is immutable: comparing in parallel is safe and deterministic.
        Parallel.For(0, cutoffs.Length, i =>
        {
            AudioNote candidate = TutorialSynth.Render(TutorialSynth.State(cutoffs[i], 10, 1));
            losses[i] = Comparer.Compare(prepared, candidate).Scores.Overall;
        });

        for (int i = 0; i < cutoffs.Length; i++)
        {
            Console.WriteLine($"cutoff {cutoffs[i],6:F0} Hz -> distance {losses[i]:F3}");
        }
    }

    public static void FeatureVectors()
    {
        PreparedTarget prepared = Comparer.PrepareTarget(TutorialSynth.Render(Wanted));
        ToneComparisonResult result = Comparer.Compare(prepared, TutorialSynth.Render(Attempt));
        FeatureVectorSchema schema = prepared.Schema;

        Console.WriteLine($"{schema.Features.Count} features; the first six:");
        for (int i = 0; i < 6; i++)
        {
            FeatureDefinition f = schema.Features[i];
            Console.WriteLine($"  {f.Id,-18} raw {result.FeatureVector.Values.Span[i],8:+0.00;-0.00;0.00} {f.Unit,-5} / scale {f.TypicalScale,4} = {result.NormalizedDifferenceVector.Values.Span[i],6:+0.00;-0.00;0.00}   default weight {f.DefaultWeight:F2}");
        }

        // Typed access and lookup by id give the same numbers.
        Console.WriteLine($"typed: AttackDelta {result.DifferenceVector.AttackDelta:+0.0;-0.0} ms; by id: {result.FeatureVector.Get(ToneFeatureIds.AttackTime):+0.0;-0.0} ms");

        // e_weighted = W * e: start from the defaults, make the attack count double and ignore the release.
        var weights = schema.DefaultWeights().ToList();
        weights.RemoveAll(w => w.FeatureId is ToneFeatureIds.AttackTime or ToneFeatureIds.ReleaseTime);
        weights.Add(new OptimizationFeatureWeight(ToneFeatureIds.AttackTime, 1.0));
        weights.Add(new OptimizationFeatureWeight(ToneFeatureIds.ReleaseTime, 0.0));
        NormalizedFeatureVector weighted = FeatureSchemaBuilder.ApplyWeights(result.NormalizedDifferenceVector, weights);
        Console.WriteLine($"error norm: unweighted {result.NormalizedDifferenceVector.Norm():F2}, weighted {weighted.Norm():F2}");
    }

    public static void LearnParameters()
    {
        ToneOptimizationSession session = NewSession();
        SynthParameterState start = Attempt;
        session.Evaluate(start, TutorialSynth.Render(start), OptimizationStepType.Validation, "start");

        // One isolated probe per ordered parameter. Unordered parameters (the waveform) cannot be probed with a
        // small step; they are handled in step 9.
        var analyzer = new ParameterSensitivityAnalyzer();
        foreach (SynthParameter parameter in session.ParameterSpace.Parameters.Where(p => p.IsOrdered))
        {
            ParameterProbe probe = analyzer.PlanProbe(parameter, start.Values[parameter.Id], normalizedStep: 0.05);
            SynthParameterState probed = start.With(parameter.Id, probe.PositiveValue);
            session.Evaluate(new OptimizationStep(OptimizationStepType.Exploration, start, probed, parameter.Id), TutorialSynth.Render(probed));
            Console.WriteLine($"probed {parameter.Id}: {probe.CenterValue:F0} -> {probe.PositiveValue:F0}");
        }

        ParameterFeatureJacobian jacobian = session.EstimateJacobian(start);
        Console.WriteLine();
        Console.WriteLine("what one full range of each parameter does (near the start state):");
        foreach ((string parameter, string feature) in new[]
                 {
                     ("cutoff", ToneFeatureIds.SpectralCentroid), ("cutoff", ToneFeatureIds.HighBandEnergy), ("cutoff", ToneFeatureIds.AttackTime),
                     ("attack", ToneFeatureIds.AttackTime), ("attack", ToneFeatureIds.SpectralCentroid),
                 })
        {
            ParameterSensitivity s = jacobian.GetSensitivity(parameter, feature)!;
            string unit = session.Target.Schema.Features[session.Target.Schema.IndexOf(feature)].Unit;
            Console.WriteLine($"  d({feature}) / d({parameter}) = {s.Sensitivity,9:+0.0;-0.0} {unit}  confidence {s.Confidence:F2}");
        }

        // Predict a step without rendering it, then check the prediction.
        SynthParameterState proposed = start.With("attack", 50);
        var prediction = jacobian.Predict(start, proposed, session.ParameterSpace);
        double predicted = prediction.PredictedFeatureDelta.Get(ToneFeatureIds.AttackTime)!.Value;
        ToneComparisonResult before = Comparer.Compare(session.Target, TutorialSynth.Render(start));
        ToneComparisonResult after = Comparer.Compare(session.Target, TutorialSynth.Render(proposed));
        Console.WriteLine();
        Console.WriteLine($"attack 80 -> 50 ms: predicted change of the attack delta {predicted:+0.0;-0.0} ms, measured {after.DifferenceVector.AttackDelta - before.DifferenceVector.AttackDelta:+0.0;-0.0} ms (prediction confidence {prediction.Confidence:F2})");
    }

    public static void CloseTheLoop()
    {
        ToneOptimizationSession session = NewSession();

        // Deliberately start with the wrong waveform as well.
        SynthParameterState start = TutorialSynth.State(cutoff: 1500, attack: 80, waveform: 0);
        ToneComparisonResult first = session.Evaluate(start, TutorialSynth.Render(start), OptimizationStepType.Validation);
        Console.WriteLine($"start: loss {first.Scores.Overall:F3}, top difference {first.Differences[0].Feature} {first.Differences[0].Direction}");

        double trust = 0.15;
        for (int iteration = 0; iteration < 25 && session.History.Best!.OverallLoss > 0.01; iteration++)
        {
            OptimizationObservation best = session.History.Best!;

            // 1. Explore whatever the advisor says is poorly understood: untried selector values, unknown parameters.
            ExperimentSuggestion? experiment = session.SuggestExperiments()
                .FirstOrDefault(e => e.StepType == OptimizationStepType.Exploration && e.ExpectedInformationGain >= 0.5);
            if (experiment is not null)
            {
                SynthParameterState probe = best.Parameters.With(experiment.ParameterId, experiment.SuggestedValue);
                ToneComparisonResult r = session.Evaluate(
                    new OptimizationStep(OptimizationStepType.Exploration, best.Parameters, probe, experiment.ParameterId), TutorialSynth.Render(probe));
                Console.WriteLine($"explore {experiment.ParameterId} = {experiment.SuggestedValue:F0} (gain {experiment.ExpectedInformationGain:F2}) -> loss {r.Scores.Overall:F3}");
                continue;
            }

            // 2. Otherwise exploit: apply the suggested adjustments inside the current trust region.
            GuidanceResult guidance = session.SuggestAdjustments(best, session.Options.Guidance with { MaximumNormalizedStep = trust });
            var empirical = guidance.Suggestions.Where(s => s.Source == SuggestionSource.Empirical).ToList();
            if (empirical.Count == 0 || trust < 0.005)
            {
                break;
            }

            SynthParameterState next = best.Parameters;
            foreach (ParameterAdjustmentSuggestion s in empirical)
            {
                next = next.With(s.ParameterId, s.SuggestedValue);
            }

            ToneComparisonResult result = session.Evaluate(next, TutorialSynth.Render(next));
            bool improved = ReferenceEquals(session.History.Best, session.History.Latest);
            trust = improved ? Math.Min(0.15, trust * 1.5) : trust * 0.5;
            Console.WriteLine($"exploit {string.Join(", ", empirical.Select(s => $"{s.ParameterId} {s.SuggestedDelta:+0;-0}"))} -> loss {result.Scores.Overall:F3} {(improved ? string.Empty : "(rejected)")}");
        }

        OptimizationProgress progress = session.GetProgress()!;
        SynthParameterState found = session.History.Best!.Parameters;
        Console.WriteLine($"best after {progress.Iterations} renders: loss {progress.BestLoss:F3}; cutoff {found.Values["cutoff"]:F0} (wanted 5000), attack {found.Values["attack"]:F0} (wanted 10), waveform {found.Values["waveform"]:F0} (wanted 1)");
        Console.WriteLine($"stagnating: {progress.IsStagnating}; oscillating parameters: {string.Join(", ", session.DetectOscillations().Where(o => o.IsParameter).Select(o => o.Id).DefaultIfEmpty("none"))}");
    }

    public static void OwnOptimizer()
    {
        // HearSay as a plain objective function for any optimizer; here a (1+1) evolution strategy in normalized space.
        ToneOptimizationSession session = NewSession();
        var random = new Random(1);
        SynthParameter[] ordered = [.. session.ParameterSpace.Parameters.Where(p => p.IsOrdered)];
        SynthParameterState current = TutorialSynth.State(1500, 80, 1);
        double currentLoss = session.Evaluate(current, TutorialSynth.Render(current)).Scores.Overall;
        double sigma = 0.15;

        for (int i = 0; i < 40; i++)
        {
            SynthParameterState trial = current;
            foreach (SynthParameter p in ordered)
            {
                double gaussian = Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());
                trial = trial.With(p.Id, p.Denormalize(p.Normalize(trial.Values[p.Id]) + (sigma * gaussian)));
            }

            double loss = session.Evaluate(trial, TutorialSynth.Render(trial)).Scores.Overall;
            if (loss < currentLoss)
            {
                (current, currentLoss, sigma) = (trial, loss, sigma * 1.3);
            }
            else
            {
                sigma *= 0.85;
            }
        }

        Console.WriteLine($"evolution strategy: loss {currentLoss:F3} after {session.History.Count} renders; cutoff {current.Values["cutoff"]:F0}, attack {current.Values["attack"]:F0}");

        // The session learned from those random steps as well, even though several parameters moved at once.
        ParameterFeatureRelationship strongest = session.GetRelationships(0.5).First();
        Console.WriteLine($"strongest correlation: {strongest.ParameterId} ~ {strongest.FeatureId}  r = {strongest.Correlation:+0.00;-0.00;0.00} (n = {strongest.ObservationCount}; correlation, not causation)");
        ParameterSensitivity learned = session.EstimateJacobian().GetSensitivity("attack", ToneFeatureIds.AttackTime)!;
        Console.WriteLine($"learned d(attack time)/d(attack) = {learned.Sensitivity:+0;-0} ms per full range (true value 299), confidence {learned.Confidence:F2}");
    }

    public static void Checkpoints()
    {
        AudioNote targetAudio = TutorialSynth.Render(Wanted);
        var session = new ToneOptimizationSession(Comparer.PrepareTarget(targetAudio), TutorialSynth.Parameters);
        session.Evaluate(Attempt, TutorialSynth.Render(Attempt));
        session.Evaluate(Attempt.With("attack", 60), TutorialSynth.Render(Attempt.With("attack", 60)));

        string json = session.ExportState();
        ToneOptimizationSession restored = ToneOptimizationSession.ImportState(json, targetAudio);
        restored.Evaluate(Attempt.With("attack", 40), TutorialSynth.Render(Attempt.With("attack", 40)));

        Console.WriteLine($"checkpoint: {json.Length / 1024} KiB; restored run continues at iteration {restored.History.Latest!.Iteration} with {restored.History.Count} observations");
        try
        {
            ToneOptimizationSession.ImportState(json, TutorialSynth.Render(Attempt));
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"wrong target is refused: {ex.Message}");
        }
    }

    private static ToneOptimizationSession NewSession() =>
        new(Comparer.PrepareTarget(TutorialSynth.Render(Wanted)), TutorialSynth.Parameters);
}
