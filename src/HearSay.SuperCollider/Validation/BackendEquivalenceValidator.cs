using HearSay.Analysis;
using HearSay.Comparison;
using HearSay.Models;
using HearSay.Partials;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.SuperCollider.Validation;

/// <summary>
/// How closely a backend reproduces the reference renderer for one patch. This measures reference renderer against
/// backend, never target against candidate: it validates the export mapping, not the quality of a match.
/// </summary>
/// <param name="OverallDistance">Composite HearSay distance with absolute loudness; 0 is identical.</param>
/// <param name="SpectralDistance">Multi-resolution STFT distance.</param>
/// <param name="EnvelopeDistance">Amplitude envelope distance.</param>
/// <param name="PartialDistance">Partial-set assignment distance.</param>
/// <param name="MaxSampleDifference">Largest absolute difference between two samples; only meaningful for patches without chaotic feedback.</param>
/// <param name="PeakLevel">Peak of the reference render, to put <paramref name="MaxSampleDifference"/> into proportion.</param>
public sealed record BackendExportValidation(
    double OverallDistance,
    double SpectralDistance,
    double EnvelopeDistance,
    double PartialDistance,
    double MaxSampleDifference,
    double PeakLevel)
{
    /// <summary>True when the two renders are the same sound for every practical purpose.</summary>
    public bool IsEquivalent => OverallDistance < 0.02 && PartialDistance < 0.05;
}

/// <summary>Compares the reference renderer with a real SuperCollider, patch by patch, using HearSay itself.</summary>
public sealed class BackendEquivalenceValidator
{
    private readonly IOperatorMatrixRenderer _reference;
    private readonly SuperColliderFm7Renderer _backend;
    private readonly ToneComparer _comparer = new();
    private readonly TargetAnalyzer _analyzer = new();

    /// <summary>Creates a validator.</summary>
    /// <param name="reference">Offline reference renderer.</param>
    /// <param name="backend">SuperCollider renderer.</param>
    public BackendEquivalenceValidator(IOperatorMatrixRenderer reference, SuperColliderFm7Renderer backend)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(backend);
        _reference = reference;
        _backend = backend;
    }

    /// <summary>Renders every job with both renderers and compares the results. All jobs share one SuperCollider session.</summary>
    /// <param name="jobs">Patches and notes.</param>
    public IReadOnlyList<BackendExportValidation> Validate(IReadOnlyList<(OperatorMatrixPatch Patch, RenderRequest Request)> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        IReadOnlyList<RenderedAudio> backend = _backend.RenderMany(jobs);
        var results = new List<BackendExportValidation>(jobs.Count);
        for (int j = 0; j < jobs.Count; j++)
        {
            RenderedAudio reference = _reference.Render(jobs[j].Patch, jobs[j].Request);
            results.Add(Compare(reference, backend[j], jobs[j].Request.FrequencyHz));
        }

        return results;
    }

    /// <summary>Compares two renders of the same patch and note.</summary>
    /// <param name="reference">Render of the reference renderer.</param>
    /// <param name="backend">Render of the backend.</param>
    /// <param name="fundamentalHz">Played note.</param>
    public BackendExportValidation Compare(RenderedAudio reference, RenderedAudio backend, double fundamentalHz)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(backend);
        double maxDifference = 0;
        int length = Math.Min(reference.Samples.Length, backend.Samples.Length);
        for (int i = 0; i < length; i++)
        {
            maxDifference = Math.Max(maxDifference, Math.Abs(reference.Samples[i] - backend.Samples[i]));
        }

        var options = new ToneComparisonOptions { LoudnessMode = LoudnessMode.Absolute, ExpectedFundamentalHz = fundamentalHz };
        ToneComparisonResult comparison = _comparer.Compare(reference.ToAudioNote(fundamentalHz), backend.ToAudioNote(fundamentalHz), options);
        TargetAnalysis a = _analyzer.Analyze(new AudioTarget(reference.Samples, reference.SampleRate, fundamentalHz));
        TargetAnalysis b = _analyzer.Analyze(new AudioTarget(backend.Samples, backend.SampleRate, fundamentalHz));
        double partial = PartialSetDistance.Compare(a.Partials, b.Partials).Distance;
        return new BackendExportValidation(
            comparison.Scores.Overall,
            comparison.Scores.Spectral,
            comparison.Scores.Envelope,
            partial,
            maxDifference,
            reference.Peak);
    }
}
