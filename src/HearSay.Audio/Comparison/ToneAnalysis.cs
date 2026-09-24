using HearSay.Envelope;
using HearSay.Features;
using HearSay.Harmonics;
using HearSay.Preprocessing;
using HearSay.Spectral;

namespace HearSay.Comparison;

/// <summary>Spectral summary of one time segment of a note.</summary>
/// <param name="Name">Segment name, see <see cref="Models.TimeSegments"/>.</param>
/// <param name="StartMs">Start of the segment, in ms from the start of the trimmed note.</param>
/// <param name="EndMs">End of the segment, in ms from the start of the trimmed note.</param>
/// <param name="BandLevelsDb">Energy per configured band within the segment, in dB.</param>
/// <param name="CentroidHz">Spectral centroid within the segment, in Hz.</param>
public sealed record SegmentAnalysis(string Name, double StartMs, double EndMs, IReadOnlyList<double> BandLevelsDb, double CentroidHz);

/// <summary>
/// Everything that is measured on a single signal. Instances are immutable after construction and can be shared
/// between threads; buffers exposed here must not be modified.
/// </summary>
public sealed class ToneAnalysis
{
    /// <summary>Preprocessed audio the analysis is based on.</summary>
    public required PreprocessedAudio Audio { get; init; }

    /// <summary>Fundamental used for the analysis, in Hz.</summary>
    public required double FundamentalHz { get; init; }

    /// <summary>Amplitude envelope and stage estimates.</summary>
    public required EnvelopeProfile Envelope { get; init; }

    /// <summary>Magnitude spectrograms by FFT size.</summary>
    public required IReadOnlyDictionary<int, Spectrogram> Spectrograms { get; init; }

    /// <summary>
    /// The spectrograms that are compared, by FFT size, with their noise smoothed (see
    /// <see cref="NoiseAnalyzer.SmoothNoise"/>): two notes that differ only in the random fine structure of their noise
    /// compare as the same sound.
    /// </summary>
    public required IReadOnlyDictionary<int, Spectrogram> ComparedSpectrograms { get; init; }

    /// <summary>Mel band powers, frame-major, from the smoothed spectrogram.</summary>
    public required float[] MelPower { get; init; }

    /// <summary>Number of Mel frames.</summary>
    public required int MelFrameCount { get; init; }

    /// <summary>Time-averaged power per Mel band.</summary>
    public required double[] MelBandPower { get; init; }

    /// <summary>Time-averaged power spectrum at the harmonic-analysis resolution.</summary>
    public required AveragePowerSpectrum AverageSpectrum { get; init; }

    /// <summary>Harmonic structure.</summary>
    public required HarmonicProfile Harmonics { get; init; }

    /// <summary>Scalar descriptors.</summary>
    public required SpectralDescriptors Descriptors { get; init; }

    /// <summary>Whole-note energy per configured band, in dB.</summary>
    public required IReadOnlyList<double> BandLevelsDb { get; init; }

    /// <summary>Energy of the aggregate low band, in dB.</summary>
    public required double LowBandDb { get; init; }

    /// <summary>Energy of the aggregate mid band, in dB.</summary>
    public required double MidBandDb { get; init; }

    /// <summary>Energy of the aggregate high band, in dB.</summary>
    public required double HighBandDb { get; init; }

    /// <summary>Per-segment spectral summaries, in chronological order. Segments that do not exist in this note are absent.</summary>
    public required IReadOnlyList<SegmentAnalysis> Segments { get; init; }

    /// <summary>
    /// Samples the signal was shifted by for time-frequency analysis after alignment refinement. Positive: the signal
    /// was late and its first samples were dropped. Always 0 for a target.
    /// </summary>
    public int AlignmentShiftSamples { get; init; }
}
