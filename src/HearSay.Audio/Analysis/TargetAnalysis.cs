using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Envelope;
using HearSay.Harmonics;
using HearSay.Partials;
using HearSay.Pitch;
using HearSay.Preprocessing;
using HearSay.Spectral;

namespace HearSay.Analysis;

/// <summary>A sound to approximate: ideally one dry, monophonic synthesizer note.</summary>
/// <param name="Samples">Mono samples.</param>
/// <param name="SampleRate">Sample rate in hertz.</param>
/// <param name="ExpectedFundamentalHz">Known fundamental; estimated when null.</param>
/// <param name="MidiNote">Known MIDI note; used for the fundamental when <paramref name="ExpectedFundamentalHz"/> is null.</param>
public sealed record AudioTarget(
    ReadOnlyMemory<float> Samples,
    int SampleRate,
    double? ExpectedFundamentalHz = null,
    int? MidiNote = null)
{
    /// <summary>Fundamental given by the caller, from the frequency or the MIDI note; null when neither is known.</summary>
    public double? KnownFundamentalHz =>
        ExpectedFundamentalHz ?? (MidiNote is int note ? 440.0 * Math.Pow(2.0, (note - 69) / 12.0) : null);

    /// <summary>Reads a target from a WAV file; several channels are mixed down.</summary>
    /// <param name="path">Path of the WAV file.</param>
    /// <param name="expectedFundamentalHz">Known fundamental.</param>
    /// <param name="midiNote">Known MIDI note.</param>
    public static AudioTarget FromWaveFile(string path, double? expectedFundamentalHz = null, int? midiNote = null)
    {
        AudioNote note = WavFile.Read(path);
        return new AudioTarget(note.Samples, note.SampleRate, expectedFundamentalHz, midiNote);
    }

    /// <summary>The target as a note for the comparison API.</summary>
    public AudioNote ToAudioNote() => new(Samples, SampleRate, KnownFundamentalHz);
}

/// <summary>Configuration of <see cref="TargetAnalyzer"/>.</summary>
public sealed record TargetAnalysisOptions
{
    /// <summary>Sample rate the analysis runs at; the target is resampled when it differs.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Level the active part of the target is brought to before the analysis, in dBFS RMS.</summary>
    public double ReferenceRmsDb { get; init; } = -20.0;

    /// <summary>Preprocessing options. The tail is kept down to -70 dB so that long decays stay part of the target.</summary>
    public PreprocessingOptions Preprocessing { get; init; } = new() { TailThresholdDb = -70.0 };

    /// <summary>Envelope options.</summary>
    public EnvelopeOptions Envelope { get; init; } = new();

    /// <summary>Partial detection options.</summary>
    public PartialOptions Partials { get; init; } = new();

    /// <summary>Partial tracking options.</summary>
    public PartialTrackingOptions Tracking { get; init; } = new();

    /// <summary>Bands of the band-energy trajectories.</summary>
    public BandOptions Bands { get; init; } = new();
}

/// <summary>
/// Everything that is measured on a target before a search starts. Nothing in here assumes a synthesis method: the
/// same analysis serves sine phase modulation today and other operator types later. Immutable.
/// </summary>
public sealed record TargetAnalysis
{
    /// <summary>Preprocessed audio: DC removed, trimmed to the onset, resampled and loudness-matched.</summary>
    public required PreprocessedAudio Audio { get; init; }

    /// <summary>Fundamental in hertz.</summary>
    public required double FundamentalHz { get; init; }

    /// <summary>0..1; 1 when the caller supplied the fundamental.</summary>
    public required double FundamentalConfidence { get; init; }

    /// <summary>Length of the trimmed note in seconds.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>Estimated moment the key was released, in seconds from the onset; the full duration when no release tail was found.</summary>
    public required double NoteOffSeconds { get; init; }

    /// <summary>Length of the release tail in seconds.</summary>
    public required double ReleaseTailSeconds { get; init; }

    /// <summary>Amplitude envelope with stage estimates.</summary>
    public required EnvelopeProfile Envelope { get; init; }

    /// <summary>Decay of the amplitude between its peak and the release: one or two stages.</summary>
    public required DecayModel AmplitudeDecay { get; init; }

    /// <summary>Partial cloud of the whole note, harmonic or not, ordered by frequency.</summary>
    public required IReadOnlyList<SpectralPartial> Partials { get; init; }

    /// <summary>0 (harmonic) to 1 (inharmonic), from the partial cloud.</summary>
    public required double Inharmonicity { get; init; }

    /// <summary>Partials followed through time.</summary>
    public required IReadOnlyList<PartialTrack> PartialTracks { get; init; }

    /// <summary>Spectral descriptors and band energies per frame.</summary>
    public required SpectralTrajectories Trajectories { get; init; }

    /// <summary>Long-term average spectrum.</summary>
    public required LongTermAverageSpectrum Ltas { get; init; }

    /// <summary>Fundamental over time.</summary>
    public required PitchTrajectory Pitch { get; init; }

    /// <summary>Slow amplitude beating in the steady part of the note.</summary>
    public required BeatAnalysis Beating { get; init; }

    /// <summary>Problems found while preparing the audio, see <see cref="Models.AnalysisWarningCodes"/>.</summary>
    public required IReadOnlyList<string> Issues { get; init; }

    /// <summary>False when the audio is unusable: silent, too short or mostly invalid.</summary>
    public bool IsUsable => Audio.IsUsable;
}

/// <summary>Runs the complete target analysis. Stateless and thread-safe.</summary>
public sealed class TargetAnalyzer
{
    private readonly AudioPreprocessor _preprocessor = new();
    private readonly StftAnalyzer _stft = new();
    private readonly EnvelopeAnalyzer _envelope = new();

    /// <summary>Analyses a target.</summary>
    /// <param name="target">Target audio.</param>
    /// <param name="options">Analysis options.</param>
    public TargetAnalysis Analyze(AudioTarget target, TargetAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new TargetAnalysisOptions();
        PreprocessedAudio audio = _preprocessor.Process(target.ToAudioNote(), options.Preprocessing, options.SampleRate, options.ReferenceRmsDb);
        return Analyze(audio, target.KnownFundamentalHz, options);
    }

    /// <summary>Analyses audio that has already been preprocessed.</summary>
    /// <param name="audio">Preprocessed audio.</param>
    /// <param name="knownFundamentalHz">Fundamental when known.</param>
    /// <param name="options">Analysis options.</param>
    public TargetAnalysis Analyze(PreprocessedAudio audio, double? knownFundamentalHz, TargetAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(audio);
        options ??= new TargetAnalysisOptions();
        float[] samples = audio.Samples;
        int sampleRate = audio.SampleRate;
        double duration = (double)samples.Length / sampleRate;

        int partialFft = options.Partials.FftSize;
        Spectrogram longSpectrogram = _stft.Analyze(samples, sampleRate, partialFft, partialFft / 4);
        AveragePowerSpectrum average = AveragePowerSpectrum.FromSpectrogram(longSpectrogram);

        double fundamental;
        double fundamentalConfidence;
        if (knownFundamentalHz is double known && known > 0)
        {
            fundamental = known;
            fundamentalConfidence = 1.0;
        }
        else
        {
            FundamentalEstimate estimate = audio.IsUsable ? FundamentalEstimator.Estimate(samples, sampleRate) : default;
            fundamental = estimate.FrequencyHz;
            fundamentalConfidence = estimate.Confidence;
            if (!(fundamental > 0) || fundamentalConfidence < 0.5)
            {
                // Inharmonic notes have no clear period; the lowest strong partial is the best available anchor.
                IReadOnlyList<SpectralPartial> provisional = PartialDetector.Detect(average.Power, average.BinHz, 100.0, options.Partials);
                double strongest = double.NegativeInfinity;
                foreach (SpectralPartial partial in provisional)
                {
                    strongest = Math.Max(strongest, partial.LevelDb);
                }

                foreach (SpectralPartial partial in provisional)
                {
                    if (partial.LevelDb >= strongest - 20.0)
                    {
                        fundamental = partial.FrequencyHz;
                        fundamentalConfidence = 0.3;
                        break;
                    }
                }
            }
        }

        if (!(fundamental > 0))
        {
            fundamental = 440.0;
            fundamentalConfidence = 0.0;
        }

        EnvelopeProfile envelope = _envelope.Analyze(samples, sampleRate, fundamental, options.Envelope);
        float[] levels = NoteLevels(envelope, out double step);
        double noteOff = envelope.ReleaseStartMs is double releaseStart ? Math.Min(duration, releaseStart / 1000.0) : duration;
        noteOff = DetectReleaseBySlope(levels, step, envelope.PeakMs / 1000.0) is double bySlope
            ? Math.Min(duration, bySlope)
            : RefineNoteOff(levels, step, envelope.PeakMs / 1000.0, noteOff, duration);
        noteOff = Math.Min(duration, SharpenNoteOff(envelope, noteOff));
        DecayModel decay = FitAmplitudeDecay(levels, step, envelope.PeakMs / 1000.0, noteOff);

        IReadOnlyList<SpectralPartial> partials = PartialDetector.Detect(average.Power, average.BinHz, fundamental, options.Partials);
        Spectrogram trackingSpectrogram = _stft.Analyze(samples, sampleRate, options.Tracking.FftSize, options.Tracking.HopSize);
        IReadOnlyList<PartialTrack> tracks = PartialTracker.Track(trackingSpectrogram, fundamental, options.Tracking);
        Spectrogram trajectorySpectrogram = _stft.Analyze(samples, sampleRate, 2048, 512);
        SpectralTrajectories trajectories = SpectralTrajectoryAnalyzer.Analyze(trajectorySpectrogram, options.Bands);
        PitchTrajectory pitch = PitchTracker.Track(samples, sampleRate, fundamental);

        double steadyFrom = Math.Min(noteOff * 0.5, (envelope.PeakMs / 1000.0) + 0.05);
        BeatAnalysis beating = BeatDetector.Detect(levels, step, steadyFrom, noteOff);

        return new TargetAnalysis
        {
            Audio = audio,
            FundamentalHz = fundamental,
            FundamentalConfidence = fundamentalConfidence,
            DurationSeconds = duration,
            NoteOffSeconds = noteOff,
            ReleaseTailSeconds = Math.Max(0.0, duration - noteOff),
            Envelope = envelope,
            AmplitudeDecay = decay,
            Partials = partials,
            Inharmonicity = PartialDetector.Inharmonicity(partials),
            PartialTracks = tracks,
            Trajectories = trajectories,
            Ltas = LongTermAverageSpectrum.From(average),
            Pitch = pitch,
            Beating = beating,
            Issues = audio.Issues,
        };
    }

    /// <summary>
    /// The release as the final stretch of the note that falls much faster than the note usually does. Returns null
    /// when the note has no such ending, for example because the recording stops before the key is released.
    /// </summary>
    private static double? DetectReleaseBySlope(float[] levels, double step, double peakSeconds)
    {
        int n = levels.Length;
        int span = Math.Max(2, (int)Math.Round(0.02 / step));
        int from = Math.Min(n - 1, (int)((peakSeconds + 0.05) / step));
        if (n < (6 * span) + from)
        {
            return null;
        }

        // Smoothed level and its slope in dB per second.
        var smooth = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - span), b = Math.Min(n - 1, i + span);
            double sum = 0;
            for (int j = a; j <= b; j++)
            {
                sum += levels[j];
            }

            smooth[i] = sum / (b - a + 1);
        }

        var slope = new double[n];
        for (int i = span; i < n - span; i++)
        {
            slope[i] = (smooth[i + span] - smooth[i - span]) / (2 * span * step);
        }

        var held = new List<double>();
        int heldEnd = from + (int)(0.8 * (n - from));
        for (int i = Math.Max(from, span); i < heldEnd; i++)
        {
            held.Add(slope[i]);
        }

        if (held.Count < 4)
        {
            return null;
        }

        double usual = Math.Min(0.0, Dsp.MathUtil.MedianInPlace(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(held)));
        double steep = Math.Min(usual * 3.0, usual - 40.0);

        // Walk back from the end of the audible note through everything that falls this steeply.
        int last = n - span - 1;
        while (last > from && levels[last] < -55f)
        {
            last--;
        }

        int start = last;
        while (start > from && slope[start] < steep)
        {
            start--;
        }

        double length = (last - start) * step;
        if (length < 0.015 || start <= from)
        {
            return null;
        }

        // The slope window smears the corner; the release begins about half a window after the first steep point.
        return (start + span) * step;
    }

    /// <summary>
    /// Sharpens a coarse note-off on the full-resolution envelope: a line through the level just before the release
    /// is extended, and the release starts where the envelope leaves that line for good.
    /// </summary>
    private static double SharpenNoteOff(EnvelopeProfile envelope, double coarseSeconds)
    {
        double hop = Math.Max(0.1, envelope.HopMs) / 1000.0;
        int lead = (int)Math.Round(envelope.LeadInMs / Math.Max(0.1, envelope.HopMs));
        float[] levels = envelope.LevelsDb;
        int Index(double seconds) => lead + (int)Math.Round(seconds / hop);
        int fitFrom = Math.Max(lead, Index(coarseSeconds - 0.15));
        int fitTo = Math.Min(levels.Length - 1, Index(coarseSeconds - 0.03));
        int probe = Math.Min(levels.Length - 1, Index(coarseSeconds + 0.03));
        if (fitTo - fitFrom < 8 || probe <= fitTo)
        {
            return coarseSeconds;
        }

        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int count = fitTo - fitFrom + 1;
        for (int i = fitFrom; i <= fitTo; i++)
        {
            sx += i;
            sy += levels[i];
            sxx += (double)i * i;
            sxy += (double)i * levels[i];
        }

        double variance = (count * sxx) - (sx * sx);
        double slope = variance > 0 ? ((count * sxy) - (sx * sy)) / variance : 0.0;
        double intercept = (sy - (slope * sx)) / count;
        if (levels[probe] > intercept + (slope * probe) - 1.5)
        {
            // Not clearly in the release yet; the coarse estimate stands.
            return coarseSeconds;
        }

        int at = probe;
        while (at > fitFrom && levels[at] < intercept + (slope * at) - 0.3)
        {
            at--;
        }

        return Math.Max(0.0, (at - lead) * hop);
    }

    private static DecayModel FitAmplitudeDecay(float[] levels, double step, double peakSeconds, double noteOffSeconds)
    {
        if (levels.Length == 0)
        {
            return DecayModel.None;
        }

        int from = Math.Clamp((int)Math.Round(peakSeconds / step), 0, levels.Length);
        int to = Math.Clamp((int)Math.Round(noteOffSeconds / step), from, levels.Length);
        return MultiStageDecay.Fit(levels.AsSpan(from, to - from), step, -58.0);
    }

    /// <summary>
    /// A deep dip of a beating note looks like the start of a release. A real release never recovers, so when the
    /// level comes back after the presumed note-off, the note-off is moved to the last moment the note is still at
    /// its usual level.
    /// </summary>
    private static double RefineNoteOff(float[] levels, double step, double peakSeconds, double noteOff, double duration)
    {
        int at = (int)(noteOff / step);
        int after = (int)((noteOff + 0.02) / step);
        if (levels.Length < 8 || at >= levels.Length - 2 || after >= levels.Length)
        {
            return noteOff;
        }

        bool recovers = false;
        for (int i = after; i < levels.Length; i++)
        {
            if (levels[i] >= levels[at] - 1f)
            {
                recovers = true;
                break;
            }
        }

        if (!recovers)
        {
            return noteOff;
        }

        int from = Math.Clamp((int)(peakSeconds / step), 0, levels.Length - 1);
        var held = new List<double>();
        for (int i = from; i < levels.Length; i++)
        {
            if (levels[i] > -30f)
            {
                held.Add(levels[i]);
            }
        }

        if (held.Count < 4)
        {
            return noteOff;
        }

        double usual = Dsp.MathUtil.MedianInPlace(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(held));
        for (int i = levels.Length - 1; i > from; i--)
        {
            if (levels[i] >= usual - 6.0)
            {
                return Math.Min(duration, (i + 1) * step);
            }
        }

        return noteOff;
    }

    /// <summary>
    /// The envelope of the note itself, without the lead-in of the envelope analysis, reduced to a step of about 5 ms
    /// by averaging. Levels are in dB relative to the peak. Plenty for decay and beat fits.
    /// </summary>
    private static float[] NoteLevels(EnvelopeProfile envelope, out double stepSeconds)
    {
        double hopMs = Math.Max(0.1, envelope.HopMs);
        int skip = Math.Clamp((int)Math.Round(envelope.LeadInMs / hopMs), 0, envelope.LevelsDb.Length);
        int factor = Math.Max(1, (int)Math.Round(5.0 / hopMs));
        stepSeconds = hopMs * factor / 1000.0;
        var result = new float[(envelope.LevelsDb.Length - skip) / factor];
        for (int i = 0; i < result.Length; i++)
        {
            float sum = 0;
            for (int j = 0; j < factor; j++)
            {
                sum += envelope.LevelsDb[skip + (i * factor) + j];
            }

            result[i] = sum / factor;
        }

        return result;
    }
}
