using HearSay.Analysis;
using HearSay.Envelope;
using HearSay.Spectral;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>A structural starting point for the search: a complete patch whose structure is a guess and whose continuous values are rough.</summary>
/// <param name="Patch">Patch with the hypothesized structure.</param>
/// <param name="PriorScore">Plausibility according to the provider, higher is better; only comparable within one provider.</param>
/// <param name="Origin">Short description of the idea behind the hypothesis, for reports.</param>
public sealed record PatchHypothesis(OperatorMatrixPatch Patch, double PriorScore, string Origin);

/// <summary>
/// Turns a target analysis into structural hypotheses for one family of synthesis. Everything that is specific to a
/// synthesis method, such as sideband arithmetic for sine phase modulation, lives behind this interface; the search
/// coordinator knows nothing about it. New operator types bring their own provider.
/// </summary>
public interface ISynthesisPriorProvider
{
    /// <summary>Name for reports.</summary>
    string Name { get; }

    /// <summary>Generates hypotheses that the capabilities allow, best first. May return nothing when the profile lacks what this provider builds on.</summary>
    /// <param name="analysis">Analysis of the target.</param>
    /// <param name="capabilities">Limits of the synthesis profile; no hypothesis may exceed them.</param>
    IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities);
}

/// <summary>
/// Envelope and pitch starting values read from a target analysis. Independent of the synthesis method: every
/// operator type needs an amplitude envelope and every patch a pitch model.
/// </summary>
public static class TemporalPriors
{
    /// <summary>Envelope of an operator that is heard directly: it follows the amplitude of the target.</summary>
    /// <param name="analysis">Analysis of the target.</param>
    public static OperatorEnvelope OutputEnvelope(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        double held = Math.Max(0.05, analysis.NoteOffSeconds - (analysis.Envelope.PeakMs / 1000.0));
        return FromDecay(AttackSeconds(analysis), analysis.AmplitudeDecay, held, ReleaseSeconds(analysis));
    }

    /// <summary>
    /// Envelope of an operator that shapes the brightness: it follows the decay of the highest frequency band that
    /// carries real energy, which for most struck and plucked sounds is much faster than the amplitude.
    /// </summary>
    /// <param name="analysis">Analysis of the target.</param>
    public static OperatorEnvelope BrightnessEnvelope(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        double held = Math.Max(0.05, analysis.NoteOffSeconds - (analysis.Envelope.PeakMs / 1000.0));
        BandTrajectory? brightest = null;
        double loudest = double.NegativeInfinity;
        foreach (BandTrajectory band in analysis.Trajectories.Bands)
        {
            loudest = Math.Max(loudest, band.PeakDb);
        }

        foreach (BandTrajectory band in analysis.Trajectories.Bands)
        {
            if (band.PeakDb > loudest - 35.0 && band.Decay.Confidence > 0.2)
            {
                brightest = band;
            }
        }

        DecayModel decay = brightest?.Decay ?? analysis.AmplitudeDecay;

        // The brightness of a note cannot outlast its amplitude.
        if (decay.FastRateDbPerSecond > analysis.AmplitudeDecay.FastRateDbPerSecond)
        {
            decay = analysis.AmplitudeDecay;
        }

        return FromDecay(AttackSeconds(analysis) * 0.5, decay, held, ReleaseSeconds(analysis));
    }

    /// <summary>Pitch model from the measured pitch movement; <see cref="PitchModel.None"/> when the pitch is steady.</summary>
    /// <param name="analysis">Analysis of the target.</param>
    public static PitchModel Pitch(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!analysis.Pitch.HasMovement)
        {
            return PitchModel.None;
        }

        OperatorEnvelope? envelope = null;
        if (Math.Abs(analysis.Pitch.AttackOffsetCents) > 8.0)
        {
            envelope = new OperatorEnvelope(
                [
                    new EnvelopePoint(0, analysis.Pitch.AttackOffsetCents * 1.5),
                    new EnvelopePoint(Math.Max(0.01, analysis.Pitch.PitchDecaySeconds), 0),
                ],
                SustainPoint: null,
                ReleasePoint: 2);
        }

        LfoSettings? vibrato = analysis.Pitch.VibratoDepthCents > 4.0 && analysis.Pitch.VibratoConfidence > 0.4
            ? new LfoSettings(analysis.Pitch.VibratoRateHz, analysis.Pitch.VibratoDepthCents)
            : null;
        return new PitchModel(envelope, vibrato);
    }

    private static double AttackSeconds(TargetAnalysis analysis) =>
        Math.Clamp((analysis.Envelope.AttackMs ?? 5.0) / 1000.0, 0.001, 2.0);

    private static double ReleaseSeconds(TargetAnalysis analysis)
    {
        // The envelope reports the time to fall 20 dB; an exponential release segment runs all the way to -100 dB.
        double twentyDb = analysis.Envelope.ReleaseMs is double ms ? ms / 1000.0 : Math.Max(0.02, analysis.ReleaseTailSeconds * 0.3);
        return Math.Clamp(twentyDb * 4.0, 0.01, 5.0);
    }

    private static OperatorEnvelope FromDecay(double attack, DecayModel decay, double heldSeconds, double release)
    {
        double fastRate = Math.Min(-0.01, decay.FastRateDbPerSecond);
        double slowRate = Math.Min(-0.01, decay.SlowRateDbPerSecond);
        double breakDb, finalDb, fastTime, slowTime;
        if (decay.IsTwoStage)
        {
            fastTime = Math.Clamp(decay.BreakSeconds, 0.005, heldSeconds);
            breakDb = Math.Clamp(fastRate * fastTime, -80.0, 0.0);
            slowTime = Math.Max(0.02, heldSeconds - fastTime);
            finalDb = Math.Clamp(breakDb + (slowRate * slowTime), -90.0, breakDb);
        }
        else
        {
            // One stage: split it in the middle so that both points stay free for the optimizer.
            fastTime = Math.Max(0.01, heldSeconds * 0.3);
            breakDb = Math.Clamp(fastRate * fastTime, -80.0, 0.0);
            slowTime = Math.Max(0.02, heldSeconds - fastTime);
            finalDb = Math.Clamp(fastRate * heldSeconds, -90.0, breakDb);
        }

        return OperatorEnvelope.TwoStageDecay(
            attack,
            fastTime,
            Math.Pow(10.0, breakDb / 20.0),
            slowTime,
            Math.Pow(10.0, finalDb / 20.0),
            release);
    }
}
