using HearSay.Comparison;
using HearSay.Features;
using HearSay.Models;
using HearSay.Optimization;
using HearSay.Optimization.Effects;
using HearSay.Optimization.Guidance;
using HearSay.Optimization.History;
using HearSay.Optimization.Parameters;
using HearSay.Optimization.Prediction;
using HearSay.Optimization.Sensitivity;
using static HearSay.Tests.OptimizationTestHelpers;

namespace HearSay.Tests;

public class OptimizationTests
{
    private static readonly SynthParameter Brightness = new("brightness", "Brightness", 0.5, 0, 1);
    private static readonly SynthParameter Attack = new("attack", "Attack", 0.1, 0, 1);
    private static readonly SynthParameter Cutoff = new("cutoff", "Cutoff", 0.5, 0, 1, Categories: [SynthParameterCategories.FilterCutoff]);
    private static readonly SynthParameter Resonance = new("resonance", "Resonance", 0.2, 0, 1);

    [Fact]
    public void SynthParameter_NormalizesAndCoerces()
    {
        var cutoff = new SynthParameter("cutoff", "Cutoff", 1000, 20, 20020);
        var waveform = new SynthParameter("wave", "Waveform", 0, 0, 3, SynthParameterKind.Categorical);

        Assert.Equal(0.5, cutoff.Normalize(10020), 9);
        Assert.Equal(10020, cutoff.Denormalize(0.5), 6);
        Assert.Equal(20020, cutoff.Coerce(99999));
        Assert.Equal(2, waveform.Denormalize(0.6));
        Assert.True(waveform.IsDiscrete);
        Assert.False(waveform.IsOrdered);
        Assert.True(new SynthParameter("n", "Voices", 1, 1, 8, SynthParameterKind.Integer).IsOrdered);
        Assert.Throws<ArgumentException>(() => new SynthParameterSpace([cutoff, cutoff]));
    }

    [Fact]
    public void BrightnessParameter_HasPositiveSensitivityOnHighBandAndCentroid()
    {
        var comparer = new ToneComparer();
        var session = new ToneOptimizationSession(comparer.PrepareTarget(Render(0.7)), [Brightness, Attack]);
        session.Evaluate(State(("brightness", 0.4), ("attack", 0.1)), Render(0.4));
        session.Evaluate(State(("brightness", 0.5), ("attack", 0.1)), Render(0.5));

        IReadOnlyList<ParameterEffectObservation> effects = ParameterEffectExtractor.FromHistory(session.History, session.ParameterSpace);
        IReadOnlyList<ParameterSensitivity> sensitivities = new ParameterSensitivityAnalyzer().Estimate(effects);
        ParameterSensitivity highBand = sensitivities.Single(s => s.ParameterId == "brightness" && s.Feature == ToneFeatureIds.HighBandEnergy);
        ParameterSensitivity centroid = sensitivities.Single(s => s.ParameterId == "brightness" && s.Feature == ToneFeatureIds.SpectralCentroid);

        Assert.True(highBand.Sensitivity > 0, $"high band {highBand.Sensitivity}");
        Assert.True(centroid.Sensitivity > 0, $"centroid {centroid.Sensitivity}");
        Assert.True(highBand.Confidence > 0.5);

        ParameterFeatureJacobian jacobian = session.EstimateJacobian();
        Assert.True(jacobian.GetSensitivity("brightness", ToneFeatureIds.HighBandEnergy)!.Sensitivity > 0);
        Assert.Equal(0.0, jacobian.GetSensitivity("attack", ToneFeatureIds.HighBandEnergy)!.Confidence);
    }

    [Fact]
    public void AttackParameter_HasPositiveSensitivityOnAttackTime()
    {
        var comparer = new ToneComparer();
        var session = new ToneOptimizationSession(comparer.PrepareTarget(Render(0.5, 0.5)), [Brightness, Attack]);
        session.Evaluate(State(("brightness", 0.5), ("attack", 0.2)), Render(0.5, 0.2), OptimizationStepType.Exploration);
        session.Evaluate(State(("brightness", 0.5), ("attack", 0.3)), Render(0.5, 0.3), OptimizationStepType.Exploration);

        ParameterSensitivity attack = session.EstimateJacobian().GetSensitivity("attack", ToneFeatureIds.AttackTime)!;
        ParameterSensitivity sustainSpectrum = session.EstimateJacobian().GetSensitivity("attack", ToneFeatureIds.Harmonic(3))!;

        // The test synth maps +0.1 to +20 ms, so the slope is about 200 ms per normalized unit.
        Assert.InRange(attack.Sensitivity, 150, 250);
        Assert.True(attack.Confidence > 0.5);
        Assert.True(Math.Abs(sustainSpectrum.Sensitivity) < 5, $"harmonic 3 slope {sustainSpectrum.Sensitivity}");
    }

    [Fact]
    public void Correlation_IdentifiesTheParameterThatDrivesAFeature()
    {
        var history = new OptimizationHistory();
        var random = new Random(7);
        for (int i = 0; i < 24; i++)
        {
            double cutoff = random.NextDouble(), resonance = random.NextDouble();
            history.Add(Observation(
                i,
                State(("cutoff", cutoff), ("resonance", resonance)),
                1.0,
                (ToneFeatureIds.SpectralCentroid, (4000 * cutoff) + (50 * (random.NextDouble() - 0.5))),
                (ToneFeatureIds.AttackTime, 3 * (random.NextDouble() - 0.5))));
        }

        IReadOnlyList<ParameterFeatureRelationship> relationships =
            new OptimizationHistoryAnalyzer().GetRelationships(history, new SynthParameterSpace([Cutoff, Resonance]));
        ParameterFeatureRelationship strongest = relationships[0];
        ParameterFeatureRelationship unrelated = relationships.Single(r => r.ParameterId == "resonance" && r.FeatureId == ToneFeatureIds.SpectralCentroid);

        Assert.Equal("cutoff", strongest.ParameterId);
        Assert.Equal(ToneFeatureIds.SpectralCentroid, strongest.FeatureId);
        Assert.True(strongest.Correlation > 0.95);
        Assert.True(strongest.Confidence > 0.99);
        Assert.Equal(24, strongest.ObservationCount);
        Assert.True(Math.Abs(unrelated.Correlation) < 0.5);
        Assert.True(unrelated.Confidence < strongest.Confidence);
    }

    [Fact]
    public void ConfoundedChange_GetsLowerConfidenceThanCleanProbe()
    {
        var space = new SynthParameterSpace([Cutoff, Resonance]);
        OptimizationObservation start = Observation(0, State(("cutoff", 0.5), ("resonance", 0.2)), 1.0, (ToneFeatureIds.HighBandEnergy, 0.0));
        OptimizationObservation clean = Observation(1, State(("cutoff", 0.6), ("resonance", 0.2)), 1.0, (ToneFeatureIds.HighBandEnergy, 5.0));
        OptimizationObservation confounded = Observation(1, State(("cutoff", 0.6), ("resonance", 0.3)), 1.0, (ToneFeatureIds.HighBandEnergy, 5.0));

        ParameterEffectObservation cleanEffect = Assert.Single(ParameterEffectExtractor.FromPair(start, clean, space));
        IReadOnlyList<ParameterEffectObservation> confoundedEffects = ParameterEffectExtractor.FromPair(start, confounded, space);
        Assert.Equal(2, confoundedEffects.Count);
        Assert.Equal(1, cleanEffect.ChangedParameterCount);
        Assert.All(confoundedEffects, e => Assert.True(e.Confidence < 0.5 * cleanEffect.Confidence));

        var cleanModel = new LocalLinearEffectModel(space, Schema);
        cleanModel.Observe(start);
        cleanModel.Observe(clean);
        var confoundedModel = new LocalLinearEffectModel(space, Schema);
        confoundedModel.Observe(start);
        confoundedModel.Observe(confounded);

        double cleanConfidence = cleanModel.EstimateJacobian(start.Parameters).GetSensitivity("cutoff", ToneFeatureIds.HighBandEnergy)!.Confidence;
        double confoundedConfidence = confoundedModel.EstimateJacobian(start.Parameters).GetSensitivity("cutoff", ToneFeatureIds.HighBandEnergy)!.Confidence;
        Assert.True(cleanConfidence > 0.6, $"clean {cleanConfidence}");
        Assert.True(confoundedConfidence < 0.2 * cleanConfidence, $"confounded {confoundedConfidence} vs clean {cleanConfidence}");
    }

    [Fact]
    public void Stagnation_IsDetectedAfterTwentyFlatIterations()
    {
        var analyzer = new OptimizationHistoryAnalyzer();
        var history = new OptimizationHistory();
        for (int i = 0; i < 10; i++)
        {
            history.Add(Observation(i, State(("cutoff", 0.1 * i)), 1.0 - (0.05 * i)));
        }

        Assert.False(analyzer.GetProgress(history)!.IsStagnating);

        for (int i = 10; i < 30; i++)
        {
            history.Add(Observation(i, State(("cutoff", 0.9)), 0.55 - (0.00001 * i) + (i % 2 == 0 ? 0.01 : 0.0)));
        }

        OptimizationProgress progress = analyzer.GetProgress(history)!;
        Assert.True(progress.IsStagnating);
        Assert.Equal(30, progress.Iterations);
        Assert.InRange(progress.ImprovementFromStart, 0.44, 0.46);
        Assert.True(progress.RecentImprovementRate < 0.001);
        Assert.Equal(history.Best!.OverallLoss, progress.BestLoss);
    }

    [Fact]
    public void Oscillation_IsDetectedForParametersAndFeatures()
    {
        var analyzer = new OptimizationHistoryAnalyzer();
        var space = new SynthParameterSpace([Cutoff, Resonance]);
        double[] cutoffs = [0.42, 0.51, 0.43, 0.50, 0.44];
        var history = new OptimizationHistory();
        for (int i = 0; i < cutoffs.Length; i++)
        {
            double error = cutoffs[i] > 0.47 ? 4.0 : -4.0;
            history.Add(Observation(i, State(("cutoff", cutoffs[i]), ("resonance", 0.2 + (0.02 * i))), 0.5, (ToneFeatureIds.HighBandEnergy, error)));
        }

        IReadOnlyList<OscillationReport> reports = analyzer.DetectOscillations(history, space, Schema);

        OscillationReport parameter = Assert.Single(reports, r => r.IsParameter);
        Assert.Equal("cutoff", parameter.Id);
        Assert.Equal(3, parameter.Reversals);
        OscillationReport feature = Assert.Single(reports, r => !r.IsParameter);
        Assert.Equal(ToneFeatureIds.HighBandEnergy, feature.Id);

        var converging = new OptimizationHistory();
        for (int i = 0; i < 6; i++)
        {
            converging.Add(Observation(i, State(("cutoff", 0.3 + (0.04 * i)), ("resonance", 0.2)), 0.5, (ToneFeatureIds.HighBandEnergy, -6.0 + i)));
        }

        Assert.Empty(analyzer.DetectOscillations(converging, space, Schema));
    }

    [Fact]
    public void LocalModel_WeighsNearbyObservationsMoreThanDistantOnes()
    {
        // Non-linear response: centroid = 4000 * cutoff^2, so the true slope is 8000 * cutoff.
        var space = new SynthParameterSpace([Cutoff]);
        var model = new LocalLinearEffectModel(space, Schema);
        double[] points = [0.10, 0.15, 0.20, 0.25, 0.75, 0.80, 0.85, 0.90];
        for (int i = 0; i < points.Length; i++)
        {
            model.Observe(Observation(i, State(("cutoff", points[i])), 1.0, (ToneFeatureIds.SpectralCentroid, 4000 * points[i] * points[i])));
        }

        ParameterSensitivity high = model.EstimateJacobian(State(("cutoff", 0.85))).GetSensitivity("cutoff", ToneFeatureIds.SpectralCentroid)!;
        ParameterSensitivity low = model.EstimateJacobian(State(("cutoff", 0.15))).GetSensitivity("cutoff", ToneFeatureIds.SpectralCentroid)!;
        ParameterSensitivity middle = model.EstimateJacobian(State(("cutoff", 0.5))).GetSensitivity("cutoff", ToneFeatureIds.SpectralCentroid)!;

        Assert.InRange(high.Sensitivity, 5800, 7400);
        Assert.InRange(low.Sensitivity, 800, 2200);
        Assert.True(middle.Confidence < high.Confidence, "no observations near 0.5, so the estimate there is less trustworthy");

        // The same holds for the finite-difference estimate.
        var history = new OptimizationHistory();
        for (int i = 0; i < points.Length; i++)
        {
            history.Add(Observation(i, State(("cutoff", points[i])), 1.0, (ToneFeatureIds.SpectralCentroid, 4000 * points[i] * points[i])));
        }

        IReadOnlyList<ParameterEffectObservation> effects = ParameterEffectExtractor.FromHistory(history, space);
        var analyzer = new ParameterSensitivityAnalyzer();
        double nearHigh = analyzer.Estimate(effects, State(("cutoff", 0.85)), space)
            .Single(s => s.Feature == ToneFeatureIds.SpectralCentroid).Sensitivity;
        double nearLow = analyzer.Estimate(effects, State(("cutoff", 0.15)), space)
            .Single(s => s.Feature == ToneFeatureIds.SpectralCentroid).Sensitivity;
        Assert.True(nearHigh > 2 * nearLow, $"high {nearHigh}, low {nearLow}");
    }

    [Fact]
    public void Prediction_ScalesLearnedLinearEffect()
    {
        var space = new SynthParameterSpace([Cutoff, Resonance]);
        var model = new LocalLinearEffectModel(space, Schema);
        double[] cutoffs = [0.4, 0.5, 0.6, 0.5, 0.5];
        double[] resonances = [0.2, 0.2, 0.2, 0.1, 0.3];
        for (int i = 0; i < cutoffs.Length; i++)
        {
            // cutoff +0.1 -> high band +2.0 dB; resonance does nothing to the high band.
            model.Observe(Observation(i, State(("cutoff", cutoffs[i]), ("resonance", resonances[i])), 1.0, (ToneFeatureIds.HighBandEnergy, 20 * (cutoffs[i] - 0.5))));
        }

        SynthParameterState current = State(("cutoff", 0.5), ("resonance", 0.2));
        ParameterEffectPrediction single = model.Predict(current, "cutoff", 0.05);
        ParameterChangePrediction joint = model.EstimateJacobian(current).Predict(current, State(("cutoff", 0.55), ("resonance", 0.25)), space);

        Assert.InRange(single.PredictedFeatureDelta.Get(ToneFeatureIds.HighBandEnergy)!.Value, 0.9, 1.1);
        Assert.InRange(joint.PredictedFeatureDelta.Get(ToneFeatureIds.HighBandEnergy)!.Value, 0.9, 1.1);
        Assert.Equal(0.0, single.PredictedFeatureDelta.Get(ToneFeatureIds.AttackTime)!.Value, 6);
        Assert.True(single.Confidence > 0.5, $"confidence {single.Confidence}");
        Assert.True(model.Predict(current, "cutoff", 0.4).Confidence < single.Confidence, "large steps leave the region where the linear model holds");
    }

    [Fact]
    public void SensitivityEstimate_RejectsOutliers_AndIgnoresTinySteps()
    {
        var effects = new List<ParameterEffectObservation>();
        double[] slopes = [10.0, 10.5, 9.5, 10.2, 9.8, 80.0];
        foreach (double slope in slopes)
        {
            effects.Add(new ParameterEffectObservation("cutoff", 0.1, [new FeatureEffect(ToneFeatureIds.HighBandEnergy, slope * 0.1)], 0.9, 0.5));
        }

        effects.Add(new ParameterEffectObservation("cutoff", 0.0001, [new FeatureEffect(ToneFeatureIds.HighBandEnergy, 5.0)], 0.9, 0.5));

        ParameterSensitivity result = Assert.Single(new ParameterSensitivityAnalyzer().Estimate(effects));

        Assert.InRange(result.Sensitivity, 9.5, 10.5);
        Assert.Equal(5, result.SampleCount);
        Assert.True(result.Confidence > 0.6);
    }

    [Fact]
    public void Probe_RespectsBounds_AndYieldsCentralDifference()
    {
        var analyzer = new ParameterSensitivityAnalyzer();
        ParameterProbe probe = analyzer.PlanProbe(Cutoff, 0.5, 0.02);
        ParameterProbe atEdge = analyzer.PlanProbe(Cutoff, 1.0, 0.02);
        ParameterProbe selector = analyzer.PlanProbe(new SynthParameter("wave", "Wave", 1, 0, 3, SynthParameterKind.Categorical), 1);

        Assert.Equal(0.48, probe.NegativeValue, 9);
        Assert.Equal(0.52, probe.PositiveValue, 9);
        Assert.Equal(1.0, atEdge.PositiveValue);
        Assert.Equal(selector.CenterValue, selector.PositiveValue);

        OptimizationObservation low = Observation(0, State(("cutoff", 0.48)), 1, (ToneFeatureIds.SpectralCentroid, -14.0));
        OptimizationObservation mid = Observation(1, State(("cutoff", 0.50)), 1, (ToneFeatureIds.SpectralCentroid, 0.0));
        OptimizationObservation high = Observation(2, State(("cutoff", 0.52)), 1, (ToneFeatureIds.SpectralCentroid, 14.0));
        ParameterProbeResult result = analyzer.AnalyzeProbe(probe, Cutoff, low, high, mid);
        ParameterSensitivity centroid = result.Sensitivities.Single(s => s.Feature == ToneFeatureIds.SpectralCentroid);

        Assert.Equal(700.0, centroid.Sensitivity, 6);
        Assert.True(centroid.Confidence > 0.8);
    }

    [Fact]
    public void Guidance_UsesEmpiricalSensitivities_AndRespectsBounds()
    {
        var space = new SynthParameterSpace([Cutoff, Resonance]);
        string[] parameterIds = ["cutoff", "resonance"];
        string[] featureIds = [.. Schema.Features.Select(f => f.Id)];
        var values = new double[featureIds.Length, 2];
        var confidence = new double[featureIds.Length, 2];
        int highBand = Schema.IndexOf(ToneFeatureIds.HighBandEnergy), centroid = Schema.IndexOf(ToneFeatureIds.SpectralCentroid);
        values[highBand, 0] = 50;      // cutoff +0.1 -> high band +5 dB
        values[centroid, 0] = 7000;    // cutoff +0.1 -> centroid +700 Hz
        for (int f = 0; f < featureIds.Length; f++)
        {
            confidence[f, 0] = 0.9;
        }

        var jacobian = new ParameterFeatureJacobian(parameterIds, featureIds, values, confidence);
        OptimizationObservation current = Observation(
            0, State(("cutoff", 0.5), ("resonance", 0.2)), 0.4, (ToneFeatureIds.HighBandEnergy, 4.2), (ToneFeatureIds.SpectralCentroid, 610));

        GuidanceResult result = new OptimizationGuidance().Suggest(current.Features, current.Parameters, jacobian, space, Schema);
        ParameterAdjustmentSuggestion suggestion = Assert.Single(result.Suggestions);

        Assert.Equal("cutoff", suggestion.ParameterId);
        Assert.Equal(SuggestionSource.Empirical, suggestion.Source);
        Assert.InRange(suggestion.SuggestedDelta, -0.095, -0.06);
        Assert.Equal(0.5 + suggestion.SuggestedDelta, suggestion.SuggestedValue, 9);
        Assert.True(suggestion.Confidence > 0.8);
        Assert.Contains(ToneFeatureIds.HighBandEnergy, suggestion.ExpectedAffectedFeatures);
        Assert.True(result.PredictedErrorNorm < 0.3 * result.CurrentErrorNorm);

        // Near the lower bound the suggestion is clamped to the range.
        GuidanceResult clamped = new OptimizationGuidance().Suggest(current.Features, State(("cutoff", 0.03), ("resonance", 0.2)), jacobian, space, Schema);
        Assert.Equal(0.0, Assert.Single(clamped.Suggestions).SuggestedValue);
    }

    [Fact]
    public void Guidance_FallsBackToLowConfidencePriors_OnlyWithoutEmpiricalKnowledge()
    {
        var space = new SynthParameterSpace([Cutoff, Resonance]);
        OptimizationObservation current = Observation(
            0, State(("cutoff", 0.5), ("resonance", 0.2)), 0.4, (ToneFeatureIds.HighBandEnergy, 6.0), (ToneFeatureIds.SpectralCentroid, 900));
        var model = new LocalLinearEffectModel(space, Schema);
        model.Observe(current);

        GuidanceResult result = new OptimizationGuidance().Suggest(current.Features, current.Parameters, model.EstimateJacobian(current.Parameters), space, Schema);
        ParameterAdjustmentSuggestion prior = Assert.Single(result.Suggestions);

        Assert.Equal("cutoff", prior.ParameterId);
        Assert.Equal(SuggestionSource.GenericPrior, prior.Source);
        Assert.True(prior.SuggestedDelta < 0);
        Assert.True(prior.Confidence <= 0.3);

        // The synth is unusual: raising cutoff lowers the high band. Measured effects must win over the prior.
        model.Observe(Observation(1, State(("cutoff", 0.55), ("resonance", 0.2)), 0.4, (ToneFeatureIds.HighBandEnergy, 3.0), (ToneFeatureIds.SpectralCentroid, 450)));
        GuidanceResult empirical = new OptimizationGuidance().Suggest(current.Features, current.Parameters, model.EstimateJacobian(current.Parameters), space, Schema);
        ParameterAdjustmentSuggestion measured = Assert.Single(empirical.Suggestions);
        Assert.Equal(SuggestionSource.Empirical, measured.Source);
        Assert.True(measured.SuggestedDelta > 0);
    }

    [Fact]
    public void DiscreteParameters_AreNeverDifferentiated_ButTheirEffectsAreLearned()
    {
        var waveform = new SynthParameter("wave", "Waveform", 0, 0, 2, SynthParameterKind.Categorical);
        var space = new SynthParameterSpace([Cutoff, waveform]);
        var model = new LocalLinearEffectModel(space, Schema);
        OptimizationObservation sine = Observation(0, State(("cutoff", 0.5), ("wave", 0)), 1.0, (ToneFeatureIds.EvenOddRatio, 0.0));
        OptimizationObservation square = Observation(1, State(("cutoff", 0.5), ("wave", 1)), 1.0, (ToneFeatureIds.EvenOddRatio, -30.0));
        model.Observe(sine);
        model.Observe(square);

        ParameterFeatureJacobian jacobian = model.EstimateJacobian(sine.Parameters);
        ParameterEffectObservation effect = Assert.Single(ParameterEffectExtractor.FromPair(sine, square, space));
        ParameterEffectPrediction forward = model.Predict(sine.Parameters, "wave", 1);
        ParameterEffectPrediction backward = model.Predict(square.Parameters, "wave", -1);
        ParameterEffectPrediction unseen = model.Predict(sine.Parameters, "wave", 2);

        Assert.DoesNotContain("wave", jacobian.ParameterIds);
        Assert.True(effect.IsDiscrete);
        Assert.Empty(new ParameterSensitivityAnalyzer().Estimate([effect]));
        Assert.Equal(-30.0, forward.PredictedFeatureDelta.Get(ToneFeatureIds.EvenOddRatio)!.Value, 6);
        Assert.Equal(30.0, backward.PredictedFeatureDelta.Get(ToneFeatureIds.EvenOddRatio)!.Value, 6);
        Assert.Equal(0.0, unseen.Confidence);

        var history = new OptimizationHistory([sine, square]);
        IReadOnlyList<ExperimentSuggestion> experiments = new NextExperimentAdvisor().Advise(
            square.Features, square.Parameters, jacobian, space, Schema, history);
        ExperimentSuggestion untried = Assert.Single(experiments, e => e.ParameterId == "wave");
        Assert.Equal(2.0, untried.SuggestedValue);
        Assert.Equal(OptimizationStepType.Exploration, untried.StepType);
    }

    [Fact]
    public void Advisor_PrefersParametersNothingIsKnownAbout()
    {
        var space = new SynthParameterSpace([Cutoff, Resonance]);
        var model = new LocalLinearEffectModel(space, Schema);
        var history = new OptimizationHistory();
        double[] cutoffs = [0.45, 0.5, 0.55, 0.5];
        for (int i = 0; i < cutoffs.Length; i++)
        {
            OptimizationObservation observation = Observation(i, State(("cutoff", cutoffs[i]), ("resonance", 0.2)), 0.5, (ToneFeatureIds.HighBandEnergy, 40 * (cutoffs[i] - 0.4)));
            model.Observe(observation);
            history.Add(observation);
        }

        OptimizationObservation current = history.Latest!;
        IReadOnlyList<ExperimentSuggestion> experiments = new NextExperimentAdvisor().Advise(
            current.Features, current.Parameters, model.EstimateJacobian(current.Parameters), space, Schema, history);

        Assert.Equal("resonance", experiments[0].ParameterId);
        Assert.Equal(OptimizationStepType.Exploration, experiments[0].StepType);
        Assert.True(experiments[0].ExpectedInformationGain > 0.9);
        Assert.DoesNotContain(experiments, e => e.ParameterId == "cutoff" && e.StepType == OptimizationStepType.Exploration);
    }

    [Fact]
    public void HistoryAndJacobian_RoundTripThroughJson()
    {
        var history = new OptimizationHistory();
        history.Add(Observation(0, State(("cutoff", 0.4)), 0.8, (ToneFeatureIds.HighBandEnergy, 3.0)));
        history.Add(Observation(1, State(("cutoff", 0.5)), 0.6, (ToneFeatureIds.HighBandEnergy, 1.0)) with { StepType = OptimizationStepType.Exploration, RenderDuration = TimeSpan.FromMilliseconds(12) });

        OptimizationHistory restored = OptimizationHistory.FromJson(history.ToJson());

        Assert.Equal(2, restored.Count);
        Assert.Equal(1, restored.Best!.Iteration);
        Assert.Equal(OptimizationStepType.Exploration, restored.Observations[1].StepType);
        Assert.Equal(TimeSpan.FromMilliseconds(12), restored.Observations[1].RenderDuration);
        Assert.Equal(0.5, restored.Observations[1].Parameters.Values["cutoff"]);
        Assert.Equal(1.0, restored.Observations[1].Features.Get(ToneFeatureIds.HighBandEnergy));

        var analyzer = new OptimizationHistoryAnalyzer();
        var space = new SynthParameterSpace([Cutoff]);
        ParameterChange change = Assert.Single(analyzer.GetParameterChanges(history.Observations[0], history.Observations[1], space));
        FeatureChange featureChange = Assert.Single(analyzer.GetFeatureChanges(history.Observations[0], history.Observations[1]));
        Assert.Equal(0.1, change.NormalizedDelta, 9);
        Assert.Equal(-2.0, featureChange.Delta, 9);

        var model = new LocalLinearEffectModel(space, Schema);
        model.ObserveAll(history);
        ParameterFeatureJacobian jacobian = model.EstimateJacobian(State(("cutoff", 0.5)));
        ParameterFeatureJacobian copy = HearSayJson.Deserialize<ParameterFeatureJacobian>(HearSayJson.Serialize(jacobian));
        Assert.Equal(jacobian.ParameterIds, copy.ParameterIds);
        Assert.Equal(jacobian.Values, copy.Values);
        Assert.Equal(jacobian.Confidence, copy.Confidence);
    }

    [Fact]
    public void Session_CanBeCheckpointedAndResumed()
    {
        var comparer = new ToneComparer();
        var options = new ToneComparisonOptions { LoudnessMode = LoudnessMode.TimbreOnly };
        Audio.AudioNote targetAudio = Render(0.7);
        var session = new ToneOptimizationSession(comparer.PrepareTarget(targetAudio, options), [Brightness, Attack]);
        session.Evaluate(State(("brightness", 0.3), ("attack", 0.1)), Render(0.3), OptimizationStepType.Exploration, "a", TimeSpan.FromMilliseconds(3));
        session.Evaluate(State(("brightness", 0.4), ("attack", 0.1)), Render(0.4), OptimizationStepType.Exploration, "b");

        string json = session.ExportState();
        ToneOptimizationSession restored = ToneOptimizationSession.ImportState(json, targetAudio);
        restored.Evaluate(State(("brightness", 0.5), ("attack", 0.1)), Render(0.5));

        Assert.Equal(3, restored.History.Count);
        Assert.Equal(2, restored.History.Latest!.Iteration);
        Assert.Equal(LoudnessMode.TimbreOnly, restored.Target.Options.LoudnessMode);
        Assert.Equal(session.History.Observations[1].OverallLoss, restored.History.Observations[1].OverallLoss);
        Assert.Equal("a", restored.History.Observations[0].CandidateId);
        Assert.Equal(3, restored.EffectModel.ObservationCount);
        Assert.Contains("\"libraryVersion\"", json);
        Assert.Contains("\"typicalScale\"", json);
        Assert.Throws<InvalidOperationException>(() => ToneOptimizationSession.ImportState(json, comparer.PrepareTarget(Render(0.2), options)));
    }

    [Fact]
    public void TaggedStep_IsRecordedWithItsType()
    {
        var session = new ToneOptimizationSession(new ToneComparer().PrepareTarget(Render(0.6)), [Brightness, Attack]);
        SynthParameterState before = State(("brightness", 0.4), ("attack", 0.1));
        var step = new OptimizationStep(OptimizationStepType.Exploration, before, before.With("brightness", 0.45), "brightness");

        session.Evaluate(step, Render(0.45));

        OptimizationObservation observation = session.History.Latest!;
        Assert.Equal(OptimizationStepType.Exploration, observation.StepType);
        Assert.Equal(0.45, observation.Parameters.Values["brightness"]);
        Assert.Equal("Exploration:brightness", observation.CandidateId);
        Assert.True(observation.AnalysisDuration > TimeSpan.Zero);
    }

    [Fact]
    public void ClosedLoop_GuidanceReducesLoss()
    {
        var comparer = new ToneComparer();
        var session = new ToneOptimizationSession(comparer.PrepareTarget(Render(0.75, 0.05)), [Brightness, Attack]);
        SynthParameterState state = State(("brightness", 0.3), ("attack", 0.4));
        double initial = session.Evaluate(state, Render(0.3, 0.4)).Scores.Overall;

        // Explore: one isolated probe per parameter.
        foreach (SynthParameter parameter in session.ParameterSpace.Parameters)
        {
            SynthParameterState probe = state.With(parameter.Id, state.Values[parameter.Id] + 0.05);
            session.Evaluate(probe, Render(probe.Values["brightness"], probe.Values["attack"]), OptimizationStepType.Exploration);
        }

        // Exploit: follow the suggestions; halve the step whenever a step is not an improvement.
        double trust = 0.15;
        for (int i = 0; i < 10; i++)
        {
            OptimizationObservation best = session.History.Best!;
            SynthParameterState next = best.Parameters;
            GuidanceResult guidance = session.SuggestAdjustments(best, new GuidanceOptions { MaximumNormalizedStep = trust });
            foreach (ParameterAdjustmentSuggestion suggestion in guidance.Suggestions)
            {
                next = next.With(suggestion.ParameterId, suggestion.SuggestedValue);
            }

            session.Evaluate(next, Render(next.Values["brightness"], next.Values["attack"]));
            trust = ReferenceEquals(session.History.Best, session.History.Latest) ? 0.15 : trust * 0.5;
        }

        OptimizationProgress progress = session.GetProgress()!;
        Assert.True(progress.BestLoss < 0.35 * initial, $"initial {initial}, best {progress.BestLoss}");
        Assert.InRange(session.History.Best!.Parameters.Values["brightness"], 0.6, 0.9);
        Assert.InRange(session.History.Best!.Parameters.Values["attack"], 0.0, 0.15);
    }
}
