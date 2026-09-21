using HearSay.Models;
using HearSay.Modeling.Parameters;

namespace HearSay.Modeling.Prediction;

/// <summary>Predicted effect of changing a single parameter.</summary>
/// <param name="ParameterId">Parameter that would change.</param>
/// <param name="ProposedDelta">Proposed change in native units.</param>
/// <param name="PredictedFeatureDelta">Predicted change of every feature delta, raw units.</param>
/// <param name="Confidence">Confidence 0..1 of the prediction.</param>
/// <param name="SupportingObservations">Effective number of observations the prediction is based on.</param>
public sealed record ParameterEffectPrediction(
    string ParameterId,
    double ProposedDelta,
    FeatureVector PredictedFeatureDelta,
    double Confidence,
    double SupportingObservations);

/// <summary>Predicted effect of moving to a proposed state, possibly changing several parameters.</summary>
/// <param name="ProposedState">State that was evaluated.</param>
/// <param name="PredictedFeatureDelta">Predicted change of every feature delta, raw units.</param>
/// <param name="Confidence">Confidence 0..1 of the prediction.</param>
public sealed record ParameterChangePrediction(
    SynthParameterState ProposedState,
    FeatureVector PredictedFeatureDelta,
    double Confidence);
