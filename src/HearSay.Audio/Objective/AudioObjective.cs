using HearSay.Analysis;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Dsp;
using HearSay.Models;
using HearSay.Partials;
using HearSay.Pitch;
using HearSay.Preprocessing;
using HearSay.Spectral;

namespace HearSay.Objective;

/// <summary>How much analysis an evaluation spends. Losses are only comparable within one level.</summary>
public enum EvaluationLevel
{
    /// <summary>Long-term spectrum, rough spectrum and rough envelope. For sorting out candidates that are obviously wrong.</summary>
    Coarse,

    /// <summary>Multi-resolution STFT, Mel, partial set, spectral and band trajectories, envelope.</summary>
    Normal,

    /// <summary>Everything in <see cref="Normal"/> plus pitch trajectory and decay stages.</summary>
    Detailed,
}

/// <summary>Weights of the terms of the audio loss. They are normalized over the terms a level computes.</summary>
public sealed record ObjectiveWeights
{
    /// <summary>Multi-resolution STFT distance.</summary>
    public double Stft { get; init; } = 0.25;

    /// <summary>Log-Mel distance.</summary>
    public double Mel { get; init; } = 0.15;

    /// <summary>Partial-set assignment distance.</summary>
    public double Partial { get; init; } = 0.20;

    /// <summary>Spectral and band-energy trajectories.</summary>
    public double Trajectory { get; init; } = 0.15;

    /// <summary>Amplitude envelope.</summary>
    public double Envelope { get; init; } = 0.15;

    /// <summary>Pitch trajectory; only computed at the detailed level or when the target pitch moves.</summary>
    public double Pitch { get; init; } = 0.05;

    /// <summary>Long-term average spectrum.</summary>
    public double Ltas { get; init; } = 0.05;
}

/// <summary>What the search minimizes.</summary>
public enum ObjectiveCriterion
{
    /// <summary>The audio loss: the weighted terms of <see cref="ObjectiveWeights"/>, at every evaluation level.</summary>
    AudioLoss,

    /// <summary>
    /// One minus the similarity of the tone comparer (<see cref="SimilarityScores.Similarity"/>), the number a
    /// listener sees in reports. The coarse level keeps the audio loss, which is cheap enough for screening hundreds
    /// of hypotheses; the normal and detailed levels compare with the tone comparer.
    /// </summary>
    Similarity,
}

/// <summary>Configuration of <see cref="AudioObjective"/>.</summary>
public sealed record ObjectiveOptions
{
    /// <summary>What is minimized: the audio loss, or one minus the similarity of the tone comparer.</summary>
    public ObjectiveCriterion Criterion { get; init; } = ObjectiveCriterion.AudioLoss;

    /// <summary>Term weights.</summary>
    public ObjectiveWeights Weights { get; init; } = new();

    /// <summary>Target analysis options, including the analysis sample rate.</summary>
    public TargetAnalysisOptions Analysis { get; init; } = new();

    /// <summary>
    /// Compare timbre only: candidates are loudness-matched to the target before anything is measured. When false the
    /// absolute level is part of the envelope term.
    /// </summary>
    public bool TimbreNormalized { get; init; } = true;

    /// <summary>Loss reported for candidates that cannot be analysed: silent, not finite or far too short.</summary>
    public double UnusableLoss { get; init; } = 10.0;
}

/// <summary>The terms of one evaluation, each scaled so that 0 is identical and about 1 is completely different. NaN marks a term the level did not compute.</summary>
/// <param name="Stft">Multi-resolution STFT distance.</param>
/// <param name="Mel">Log-Mel distance.</param>
/// <param name="Partial">Partial-set distance.</param>
/// <param name="Trajectory">Trajectory distance.</param>
/// <param name="Envelope">Envelope distance.</param>
/// <param name="Pitch">Pitch distance.</param>
/// <param name="Ltas">Long-term spectrum distance.</param>
public sealed record ObjectiveTerms(double Stft, double Mel, double Partial, double Trajectory, double Envelope, double Pitch, double Ltas);

/// <summary>Result of one evaluation.</summary>
/// <param name="Loss">Weighted audio loss; 0 is a perfect match.</param>
/// <param name="Terms">Individual terms.</param>
/// <param name="Level">Level the evaluation ran at.</param>
/// <param name="IsUsable">False when the candidate could not be analysed and <see cref="Loss"/> is the penalty value.</param>
public sealed record ObjectiveResult(double Loss, ObjectiveTerms Terms, EvaluationLevel Level, bool IsUsable);

/// <summary>A target prepared for many evaluations. Immutable and safe to share between threads.</summary>
public sealed class PreparedObjectiveTarget
{
    private readonly Lazy<NoteFeatures>[] _features;

    private readonly Lazy<PreparedTarget> _comparerTarget;

    internal PreparedObjectiveTarget(TargetAnalysis analysis, ObjectiveOptions options, Func<EvaluationLevel, NoteFeatures> extract, Func<PreparedTarget> prepareForComparer)
    {
        Analysis = analysis;
        Options = options;
        _features =
        [
            new Lazy<NoteFeatures>(() => extract(EvaluationLevel.Coarse)),
            new Lazy<NoteFeatures>(() => extract(EvaluationLevel.Normal)),
            new Lazy<NoteFeatures>(() => extract(EvaluationLevel.Detailed)),
        ];
        _comparerTarget = new Lazy<PreparedTarget>(prepareForComparer);
    }

    /// <summary>Full analysis of the target.</summary>
    public TargetAnalysis Analysis { get; }

    /// <summary>Options the target was prepared with.</summary>
    public ObjectiveOptions Options { get; }

    internal NoteFeatures Features(EvaluationLevel level) => _features[(int)level].Value;

    /// <summary>The target as the tone comparer sees it, for the similarity criterion; prepared on first use.</summary>
    internal PreparedTarget ComparerTarget => _comparerTarget.Value;
}

/// <summary>What is measured on a note for one evaluation level.</summary>
internal sealed class NoteFeatures
{
    public required Spectrogram[] Spectrograms { get; init; }

    public required float[][] LogMagnitudes { get; init; }

    public required float[] MagnitudeFloors { get; init; }

    public required float[] EnvelopeDb { get; init; }

    public required LongTermAverageSpectrum Ltas { get; init; }

    public float[]? MelDb { get; init; }

    public int MelBands { get; init; }

    public IReadOnlyList<SpectralPartial>? Partials { get; init; }

    public SpectralTrajectories? Trajectories { get; init; }

    public PitchTrajectory? Pitch { get; init; }
}

/// <summary>
/// The scalar the search minimizes: how close does a candidate sound to the target? It is deliberately separate from
/// the diagnostic feature vector, which explains how a candidate differs. Thread-safe.
/// </summary>
public sealed class AudioObjective
{
    private const double DynamicRangeDb = 80.0;
    private static readonly MelOptions Mel = new();

    private readonly AudioPreprocessor _preprocessor = new();
    private readonly StftAnalyzer _stft = new();
    private readonly TargetAnalyzer _analyzer = new();
    private readonly ToneComparer _comparer = new();

    /// <summary>Analyses a target once for any number of evaluations.</summary>
    /// <param name="target">Target audio.</param>
    /// <param name="options">Objective options.</param>
    public PreparedObjectiveTarget Prepare(AudioTarget target, ObjectiveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new ObjectiveOptions();
        TargetAnalysis analysis = _analyzer.Analyze(target, options.Analysis);
        // Pitch is compared when it moves, and at the detailed level whenever the note has a pitch at all. An inharmonic
        // note has none: its period wanders with the balance of its partials, and comparing that would only add noise.
        bool pitched = analysis.Inharmonicity < 0.15;
        bool trackPitch = analysis.HasPitchMovement;
        return new PreparedObjectiveTarget(
            analysis,
            options,
            level => Extract(analysis.Audio, analysis.FundamentalHz, level, options, trackPitch || (pitched && level == EvaluationLevel.Detailed), magnitudeFloors: null),
            () => _comparer.PrepareTarget(
                new AudioNote(analysis.Audio.Samples, analysis.Audio.SampleRate, analysis.FundamentalHz),
                new ToneComparisonOptions { LoudnessMode = LoudnessMode.SeparateLoudness, ExpectedFundamentalHz = analysis.FundamentalHz }));
    }

    /// <summary>Evaluates a candidate.</summary>
    /// <param name="target">Prepared target.</param>
    /// <param name="candidate">Candidate audio, usually a render.</param>
    /// <param name="level">How much analysis to spend.</param>
    public ObjectiveResult Evaluate(PreparedObjectiveTarget target, AudioNote candidate, EvaluationLevel level = EvaluationLevel.Normal)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidate);
        ObjectiveOptions options = target.Options;
        TargetAnalysis analysis = target.Analysis;
        var unusable = new ObjectiveResult(options.UnusableLoss, new ObjectiveTerms(1, 1, 1, 1, 1, 1, 1), level, false);
        if (!analysis.IsUsable)
        {
            return unusable;
        }

        double? reference = options.TimbreNormalized ? options.Analysis.ReferenceRmsDb : null;
        PreprocessedAudio audio = _preprocessor.Process(candidate, options.Analysis.Preprocessing, options.Analysis.SampleRate, reference);
        if (!audio.IsUsable)
        {
            return unusable;
        }

        if (options.Criterion == ObjectiveCriterion.Similarity && level != EvaluationLevel.Coarse)
        {
            // The number a listener reads in reports, as a loss: the tone comparer judges the raw candidate the same
            // way the final comparison of a result does. The coarse level stays the audio loss (see the enum).
            ToneComparisonResult comparison = _comparer.Compare(target.ComparerTarget, new AudioNote(candidate.Samples, candidate.SampleRate, analysis.FundamentalHz));
            SimilarityScores s = comparison.Scores;
            double similarityLoss = 1.0 - s.Similarity;
            if (!double.IsFinite(similarityLoss))
            {
                return unusable;
            }

            return new ObjectiveResult(similarityLoss, new ObjectiveTerms(1.0 - s.Spectral, 1.0 - s.Perceptual, 1.0 - s.Harmonic, double.NaN, 1.0 - s.Envelope, double.NaN, double.NaN), level, true);
        }

        if (!options.TimbreNormalized)
        {
            // Absolute mode: the candidate gets the gain of the target so that level differences survive.
            audio = ApplyGain(audio, analysis.Audio.AppliedGainDb);
        }

        NoteFeatures t = target.Features(level);
        NoteFeatures c = Extract(audio, analysis.FundamentalHz, level, options, t.Pitch is not null, t.MagnitudeFloors);

        double stft = StftDistance(t, c);
        double envelope = CurveDistance(t.EnvelopeDb, c.EnvelopeDb);
        double ltas = t.Ltas.DistanceTo(c.Ltas);
        double mel = double.NaN, partial = double.NaN, trajectory = double.NaN, pitch = double.NaN;
        if (level != EvaluationLevel.Coarse)
        {
            mel = MelDistance(t, c);
            partial = PartialSetDistance.Compare(t.Partials!, c.Partials!).Distance;
            trajectory = SpectralTrajectoryAnalyzer.Compare(t.Trajectories!, c.Trajectories!).Combined;
            if (t.Pitch is not null && c.Pitch is not null)
            {
                pitch = PitchTracker.Distance(t.Pitch, c.Pitch);
            }
        }

        ObjectiveWeights w = options.Weights;
        double sum = 0, weights = 0;
        Add(stft, w.Stft);
        Add(mel, w.Mel);
        Add(partial, w.Partial);
        Add(trajectory, w.Trajectory);
        Add(envelope, w.Envelope);
        Add(pitch, w.Pitch);
        Add(ltas, level == EvaluationLevel.Coarse ? w.Ltas + w.Partial + w.Mel : w.Ltas);
        double loss = weights > 0 ? sum / weights : 0.0;
        if (!double.IsFinite(loss))
        {
            return unusable;
        }

        return new ObjectiveResult(loss, new ObjectiveTerms(stft, mel, partial, trajectory, envelope, pitch, ltas), level, true);

        void Add(double term, double weight)
        {
            if (!double.IsNaN(term) && weight > 0)
            {
                sum += weight * term;
                weights += weight;
            }
        }
    }

    private NoteFeatures Extract(PreprocessedAudio audio, double fundamentalHz, EvaluationLevel level, ObjectiveOptions options, bool trackPitch, float[]? magnitudeFloors)
    {
        float[] samples = audio.Samples;
        int sampleRate = audio.SampleRate;
        if (level == EvaluationLevel.Coarse)
        {
            Spectrogram rough = _stft.Analyze(samples, sampleRate, 1024, 512);
            return Build([rough], rough, AveragePowerSpectrum.FromSpectrogram(rough), magnitudeFloors);
        }

        Spectrogram fine = _stft.Analyze(samples, sampleRate, 512, 128);
        Spectrogram wide = _stft.Analyze(samples, sampleRate, 2048, 512);
        int partialFft = options.Analysis.Partials.FftSize;
        Spectrogram longest = _stft.Analyze(samples, sampleRate, partialFft, partialFft / 4);
        AveragePowerSpectrum average = AveragePowerSpectrum.FromSpectrogram(longest);

        MelFilterbank bank = MelFilterbank.Get(sampleRate, 2048, Mel);
        float[] melPower = bank.Apply(wide);
        float melMax = 0;
        foreach (float p in melPower)
        {
            melMax = Math.Max(melMax, p);
        }

        float melFloor = Math.Max(1e-20f, melMax * 1e-8f);
        var melDb = new float[melPower.Length];
        for (int i = 0; i < melDb.Length; i++)
        {
            melDb[i] = 10f * MathF.Log10(Math.Max(melPower[i], melFloor));
        }

        NoteFeatures basic = Build([fine, wide], fine, average, magnitudeFloors);
        return new NoteFeatures
        {
            Spectrograms = basic.Spectrograms,
            LogMagnitudes = basic.LogMagnitudes,
            MagnitudeFloors = basic.MagnitudeFloors,
            EnvelopeDb = basic.EnvelopeDb,
            Ltas = basic.Ltas,
            MelDb = melDb,
            MelBands = bank.BandCount,
            Partials = PartialDetector.Detect(average.Power, average.BinHz, fundamentalHz, options.Analysis.Partials),
            Trajectories = SpectralTrajectoryAnalyzer.Analyze(wide, options.Analysis.Bands),
            Pitch = trackPitch ? PitchTracker.Track(samples, sampleRate, fundamentalHz) : null,
        };
    }

    private static NoteFeatures Build(Spectrogram[] spectrograms, Spectrogram envelopeSource, AveragePowerSpectrum average, float[]? magnitudeFloors)
    {
        var logs = new float[spectrograms.Length][];
        var floors = new float[spectrograms.Length];
        for (int s = 0; s < spectrograms.Length; s++)
        {
            float[] magnitudes = spectrograms[s].Magnitudes;
            float floor;
            if (magnitudeFloors is not null)
            {
                // A candidate is measured against the floor of the target, so both sides clip at the same level.
                floor = magnitudeFloors[s];
            }
            else
            {
                float max = 0;
                foreach (float m in magnitudes)
                {
                    max = Math.Max(max, m);
                }

                floor = Math.Max(1e-10f, max * (float)MathUtil.DbToAmplitude(-DynamicRangeDb));
            }

            floors[s] = floor;
            var log = new float[magnitudes.Length];
            for (int i = 0; i < log.Length; i++)
            {
                log[i] = 20f * MathF.Log10(Math.Max(magnitudes[i], floor));
            }

            logs[s] = log;
        }

        int frames = envelopeSource.FrameCount;
        int bins = envelopeSource.BinCount;
        var envelope = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<float> row = envelopeSource.Frame(f);
            double power = 0;
            for (int k = 0; k < bins; k++)
            {
                power += (double)row[k] * row[k];
            }

            envelope[f] = (float)Math.Max(-100.0, MathUtil.PowerToDb(power));
        }

        return new NoteFeatures
        {
            Spectrograms = spectrograms,
            LogMagnitudes = logs,
            MagnitudeFloors = floors,
            EnvelopeDb = envelope,
            Ltas = LongTermAverageSpectrum.From(average),
        };
    }

    /// <summary>Spectral convergence and log-magnitude distance, averaged over the resolutions.</summary>
    private static double StftDistance(NoteFeatures target, NoteFeatures candidate)
    {
        double total = 0;
        for (int s = 0; s < target.Spectrograms.Length; s++)
        {
            Spectrogram t = target.Spectrograms[s];
            Spectrogram c = candidate.Spectrograms[s];
            float[] logT = target.LogMagnitudes[s];
            float[] logC = candidate.LogMagnitudes[s];
            float floorDb = 20f * MathF.Log10(target.MagnitudeFloors[s]);
            int bins = t.BinCount;
            int frames = Math.Max(t.FrameCount, c.FrameCount);
            double numerator = 0, denominator = 0, log = 0;
            for (int f = 0; f < frames; f++)
            {
                bool hasT = f < t.FrameCount, hasC = f < c.FrameCount;
                ReadOnlySpan<float> rowT = hasT ? t.Frame(f) : default;
                ReadOnlySpan<float> rowC = hasC ? c.Frame(f) : default;
                ReadOnlySpan<float> rowLogT = hasT ? logT.AsSpan(f * bins, bins) : default;
                ReadOnlySpan<float> rowLogC = hasC ? logC.AsSpan(f * bins, bins) : default;
                for (int k = 0; k < bins; k++)
                {
                    float tm = hasT ? rowT[k] : 0f;
                    float cm = hasC ? rowC[k] : 0f;
                    float d = tm - cm;
                    numerator += d * d;
                    denominator += tm * tm;
                    float lt = hasT ? rowLogT[k] : floorDb;
                    float lc = hasC ? rowLogC[k] : floorDb;
                    log += Math.Abs(lt - lc);
                }
            }

            double convergence = denominator > 0 ? Math.Min(2.0, Math.Sqrt(numerator / denominator)) : 0.0;
            double logDistance = log / ((double)frames * bins);
            total += (0.5 * convergence) + (0.5 * logDistance / 20.0);
        }

        return total / target.Spectrograms.Length;
    }

    private static double MelDistance(NoteFeatures target, NoteFeatures candidate)
    {
        float[] t = target.MelDb!;
        float[] c = candidate.MelDb!;
        int bands = target.MelBands;

        // Both sides are clipped at the floor of the target so that silence compares equal to silence.
        float floor = float.PositiveInfinity;
        foreach (float v in t)
        {
            floor = Math.Min(floor, v);
        }

        int length = Math.Max(t.Length, c.Length);
        double sum = 0;
        for (int i = 0; i < length; i++)
        {
            float a = i < t.Length ? t[i] : floor;
            float b = i < c.Length ? Math.Max(c[i], floor) : floor;
            sum += Math.Abs(a - b);
        }

        return length > 0 && bands > 0 ? sum / length / 20.0 : 0.0;
    }

    /// <summary>Mean absolute difference of two level curves in dB, 20 dB counting as 1; a missing frame is silence.</summary>
    private static double CurveDistance(float[] target, float[] candidate)
    {
        float peak = float.NegativeInfinity;
        foreach (float v in target)
        {
            peak = Math.Max(peak, v);
        }

        float floor = peak - 70f;
        int length = Math.Max(target.Length, candidate.Length);
        double sum = 0;
        int count = 0;
        for (int i = 0; i < length; i++)
        {
            float a = i < target.Length ? Math.Max(target[i], floor) : floor;
            float b = i < candidate.Length ? Math.Max(candidate[i], floor) : floor;
            if (a <= floor && b <= floor)
            {
                continue;
            }

            sum += Math.Min(40f, Math.Abs(a - b));
            count++;
        }

        return count > 0 ? sum / count / 20.0 : 0.0;
    }

    private static PreprocessedAudio ApplyGain(PreprocessedAudio audio, double gainDb)
    {
        double delta = gainDb - audio.AppliedGainDb;
        if (Math.Abs(delta) < 1e-9)
        {
            return audio;
        }

        float gain = (float)MathUtil.DbToAmplitude(delta);
        var samples = new float[audio.Samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = audio.Samples[i] * gain;
        }

        return new PreprocessedAudio(samples, audio.SampleRate, audio.Issues)
        {
            IsUsable = audio.IsUsable,
            OnsetOffsetMs = audio.OnsetOffsetMs,
            ActiveRmsDb = audio.ActiveRmsDb + delta,
            PeakDb = audio.PeakDb + delta,
            AppliedGainDb = gainDb,
            DcOffset = audio.DcOffset,
            InvalidSampleCount = audio.InvalidSampleCount,
            ClippedFraction = audio.ClippedFraction,
            WasResampled = audio.WasResampled,
        };
    }
}
