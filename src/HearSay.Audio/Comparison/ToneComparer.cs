using HearSay.Audio;
using HearSay.Dsp;
using HearSay.Envelope;
using HearSay.Features;
using HearSay.Harmonics;
using HearSay.Models;
using HearSay.Preprocessing;
using HearSay.Spectral;

namespace HearSay.Comparison;

/// <summary>Compares a candidate tone with a target tone of the same note.</summary>
public interface IToneComparer
{
    /// <summary>Analyses a target once so that many candidates can be compared against it cheaply.</summary>
    /// <param name="target">Target note.</param>
    /// <param name="options">Comparison configuration; null uses the defaults.</param>
    /// <exception cref="ArgumentException">The target cannot be analysed (silent, too short or invalid).</exception>
    PreparedTarget PrepareTarget(AudioNote target, ToneComparisonOptions? options = null);

    /// <summary>Compares a candidate with a prepared target, using the configuration the target was prepared with.</summary>
    /// <param name="target">Prepared target.</param>
    /// <param name="candidate">Candidate note.</param>
    ToneComparisonResult Compare(PreparedTarget target, AudioNote candidate);

    /// <summary>Compares a candidate with a target.</summary>
    /// <param name="target">Target note.</param>
    /// <param name="candidate">Candidate note.</param>
    /// <param name="options">Comparison configuration; null uses the defaults.</param>
    ToneComparisonResult Compare(AudioNote target, AudioNote candidate, ToneComparisonOptions? options = null);
}

/// <summary>
/// Default comparer. It holds no mutable state: all caches (FFT plans, windows, Mel filterbanks) are immutable after
/// initialization, so one instance can be used from many threads, and the same input always gives the same output.
/// Every comparison is phase invariant because only magnitude spectra, harmonic powers and envelopes are compared.
/// </summary>
public sealed class ToneComparer : IToneComparer
{
    private readonly IAudioPreprocessor preprocessor;
    private readonly IToneDifferenceAnalyzer differenceAnalyzer;
    private readonly ToneAnalyzer analyzer;

    /// <summary>Creates a comparer with the default analyzers.</summary>
    public ToneComparer()
        : this(null, null, null, null, null, null)
    {
    }

    /// <summary>Creates a comparer with custom analyzers; null selects the default implementation.</summary>
    /// <param name="preprocessor">Preprocessing pipeline.</param>
    /// <param name="stftAnalyzer">STFT implementation.</param>
    /// <param name="harmonicAnalyzer">Harmonic analyzer.</param>
    /// <param name="envelopeAnalyzer">Envelope analyzer.</param>
    /// <param name="spectralFeatureAnalyzer">Descriptor analyzer.</param>
    /// <param name="differenceAnalyzer">Difference analyzer.</param>
    public ToneComparer(
        IAudioPreprocessor? preprocessor,
        IStftAnalyzer? stftAnalyzer,
        IHarmonicAnalyzer? harmonicAnalyzer,
        IEnvelopeAnalyzer? envelopeAnalyzer,
        ISpectralFeatureAnalyzer? spectralFeatureAnalyzer,
        IToneDifferenceAnalyzer? differenceAnalyzer)
    {
        this.preprocessor = preprocessor ?? new AudioPreprocessor();
        this.differenceAnalyzer = differenceAnalyzer ?? new ToneDifferenceAnalyzer();
        analyzer = new ToneAnalyzer(
            stftAnalyzer ?? new StftAnalyzer(),
            harmonicAnalyzer ?? new HarmonicAnalyzer(),
            envelopeAnalyzer ?? new EnvelopeAnalyzer(),
            spectralFeatureAnalyzer ?? new SpectralFeatureAnalyzer());
    }

    /// <inheritdoc />
    public PreparedTarget PrepareTarget(AudioNote target, ToneComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new ToneComparisonOptions();
        if (target.SampleRate <= 0)
        {
            throw new ArgumentException("The target has an invalid sample rate.", nameof(target));
        }

        int sampleRate = options.AnalysisSampleRate ?? target.SampleRate;
        PreprocessedAudio audio = preprocessor.Process(target, options.Preprocessing, sampleRate, ReferenceLevel(options));
        if (!audio.IsUsable)
        {
            throw new ArgumentException($"The target cannot be analysed: {string.Join(", ", audio.Issues)}.", nameof(target));
        }

        double confidence = 1.0;
        double? fundamental = options.ExpectedFundamentalHz ?? target.ExpectedFundamentalHz;
        if (fundamental is null or <= 0)
        {
            FundamentalEstimate estimate = FundamentalEstimator.Estimate(
                audio.Samples, sampleRate, options.Harmonic.MinimumFundamentalHz, options.Harmonic.MaximumFundamentalHz);
            fundamental = estimate.FrequencyHz;
            confidence = estimate.Confidence;
        }

        ToneAnalysis analysis = analyzer.Analyze(audio, fundamental.Value, options, null);
        return new PreparedTarget(options, analysis, confidence, FeatureSchemaBuilder.Build(options), PreparedTarget.ComputeFingerprint(target));
    }

    /// <inheritdoc />
    public ToneComparisonResult Compare(AudioNote target, AudioNote candidate, ToneComparisonOptions? options = null) =>
        Compare(PrepareTarget(target, options), candidate);

    /// <summary>Compares two sample arrays that share a sample rate.</summary>
    /// <param name="targetSamples">Target samples.</param>
    /// <param name="candidateSamples">Candidate samples.</param>
    /// <param name="sampleRate">Sample rate of both arrays in Hz.</param>
    /// <param name="expectedFundamentalHz">Fundamental of the note in Hz.</param>
    /// <param name="options">Comparison configuration; null uses the defaults.</param>
    public ToneComparisonResult Compare(float[] targetSamples, float[] candidateSamples, int sampleRate, double expectedFundamentalHz, ToneComparisonOptions? options = null) =>
        Compare(
            AudioNote.FromSamples(targetSamples, sampleRate, expectedFundamentalHz),
            AudioNote.FromSamples(candidateSamples, sampleRate, expectedFundamentalHz),
            options);

    /// <inheritdoc />
    public ToneComparisonResult Compare(PreparedTarget target, AudioNote candidate)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidate);
        ToneComparisonOptions options = target.Options;
        PreprocessedAudio audio = preprocessor.Process(candidate, options.Preprocessing, target.SampleRate, ReferenceLevel(options));
        if (!audio.IsUsable)
        {
            return Unusable(target, audio);
        }

        ToneAnalysis t = target.Analysis;
        ToneAnalysis c = analyzer.Analyze(audio, target.FundamentalHz, options, t.Envelope);

        SpectralComparison spectral = CompareStft(target, c);
        (MelComparison mel, double[] melBandDifferences) = CompareMel(target, c);
        List<HarmonicComparison> harmonics = CompareHarmonics(t.Harmonics, c.Harmonics, options.Harmonic.LevelFloorDb);
        EnvelopeComparison envelope = CompareEnvelopes(t.Envelope, c.Envelope, options.Envelope.FloorDb);
        List<FrequencyBandDifference> bands = CompareBands(options.Bands, t.BandLevelsDb, c.BandLevelsDb, target.SampleRate);
        List<TimeSegmentComparison> segments = CompareSegments(options.Bands, t, c, target.SampleRate);

        double loudnessDelta = c.Audio.ActiveRmsDb - t.Audio.ActiveRmsDb;
        double pitchDelta = MathUtil.Cents(c.Harmonics.FundamentalHz, t.Harmonics.FundamentalHz);
        List<DescriptorComparison> descriptors = CompareDescriptors(t, c);

        var warnings = new List<AnalysisWarning>();
        AddWarnings(warnings, t.Audio, "target");
        AddWarnings(warnings, c.Audio, "candidate");
        if (target.FundamentalConfidence < 0.6)
        {
            warnings.Add(new AnalysisWarning(AnalysisWarningCodes.UncertainFundamental, "target", "The fundamental was estimated with low confidence; harmonic measurements may be unreliable."));
        }

        if (Math.Abs(pitchDelta) > 50.0)
        {
            warnings.Add(new AnalysisWarning(AnalysisWarningCodes.PitchMismatch, "candidate", "The candidate pitch deviates more than 50 cents from the target."));
        }

        if (envelope.TargetHasSustain != envelope.CandidateHasSustain)
        {
            warnings.Add(new AnalysisWarning(AnalysisWarningCodes.EnvelopeShapeMismatch, "candidate", "Only one of the notes has a sustain stage; sustain and release were not compared."));
        }

        double measurementConfidence = target.FundamentalConfidence
            * (HasIssue(t.Audio, c.Audio, AnalysisWarningCodes.Clipping) ? 0.8 : 1.0)
            * (HasIssue(t.Audio, c.Audio, AnalysisWarningCodes.InvalidSamples) ? 0.9 : 1.0);

        var measurements = new ToneMeasurements(
            loudnessDelta, pitchDelta, envelope, harmonics, descriptors, bands, segments, mel, melBandDifferences,
            MelFilterbank.Get(target.SampleRate, options.Mel.FftSize, options.Mel).EdgesHz, measurementConfidence);
        IReadOnlyList<ToneDifference> differences = differenceAnalyzer.Analyze(measurements, options);
        IReadOnlyList<ParameterHint> hints = options.Differences.IncludeParameterHints ? differenceAnalyzer.CreateHints(differences) : [];

        // Vectors.
        var unreliable = new List<string>();
        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [ToneFeatureIds.Loudness] = loudnessDelta,
            [ToneFeatureIds.Pitch] = pitchDelta,
            [ToneFeatureIds.AttackTime] = StageDelta(envelope.Attack, ToneFeatureIds.AttackTime, unreliable),
            [ToneFeatureIds.DecayTime] = StageDelta(envelope.Decay, ToneFeatureIds.DecayTime, unreliable),
            [ToneFeatureIds.SustainLevel] = StageDelta(envelope.Sustain, ToneFeatureIds.SustainLevel, unreliable),
            [ToneFeatureIds.ReleaseTime] = StageDelta(envelope.Release, ToneFeatureIds.ReleaseTime, unreliable),
        };
        foreach (DescriptorComparison d in descriptors)
        {
            values[d.FeatureId] = d.Difference;
        }

        foreach (FrequencyBandDifference band in bands)
        {
            values[ToneFeatureIds.DetailBand(band.Name)] = band.DifferenceDb;
        }

        foreach (string name in TimeSegments.All)
        {
            string id = ToneFeatureIds.SegmentCentroid(name);
            TimeSegmentComparison? segment = segments.Find(s => s.Segment == name);
            if (segment is null)
            {
                unreliable.Add(id);
            }

            values[id] = segment?.CentroidDifferenceHz ?? 0.0;
        }

        int harmonicCount = options.FeatureVector.HarmonicCount;
        var harmonicDeltas = new double[harmonicCount];
        for (int k = 0; k < harmonicCount; k++)
        {
            harmonicDeltas[k] = k < harmonics.Count ? harmonics[k].DifferenceDb : 0.0;
            values[ToneFeatureIds.Harmonic(k + 1)] = harmonicDeltas[k];
        }

        FeatureVectorSchema schema = target.Schema;
        var ids = new string[schema.Features.Count];
        var dense = new double[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = schema.Features[i].Id;
            dense[i] = values.TryGetValue(ids[i], out double v) && double.IsFinite(v) ? v : 0.0;
        }

        var featureVector = new FeatureVector(ids, dense);
        NormalizedFeatureVector normalized = FeatureSchemaBuilder.Normalize(featureVector, schema, options.FeatureVector.NormalizedClip);
        var typed = new ToneDifferenceVector(
            loudnessDelta,
            pitchDelta,
            values[ToneFeatureIds.AttackTime],
            values[ToneFeatureIds.DecayTime],
            values[ToneFeatureIds.SustainLevel],
            values[ToneFeatureIds.ReleaseTime],
            values[ToneFeatureIds.SpectralCentroid],
            values[ToneFeatureIds.SpectralRolloff],
            values[ToneFeatureIds.SpectralBandwidth],
            values[ToneFeatureIds.SpectralFlatness],
            values[ToneFeatureIds.SpectralSlope],
            values[ToneFeatureIds.HarmonicToNoiseRatio],
            values[ToneFeatureIds.Inharmonicity],
            values[ToneFeatureIds.HarmonicSlope],
            values[ToneFeatureIds.EvenOddRatio],
            values[ToneFeatureIds.LowBandEnergy],
            values[ToneFeatureIds.MidBandEnergy],
            values[ToneFeatureIds.HighBandEnergy],
            harmonicDeltas);

        SimilarityScores scores = Score(options, spectral, mel, harmonics, t.Harmonics, c.Harmonics, envelope, loudnessDelta);
        double alignmentMs = 1000.0 * c.AlignmentShiftSamples / target.SampleRate;
        var quality = new AnalysisQuality(
            true, measurementConfidence, target.FundamentalHz, target.FundamentalConfidence, alignmentMs,
            t.Audio.OnsetOffsetMs, c.Audio.OnsetOffsetMs, unreliable, warnings);

        return new ToneComparisonResult(
            scores, envelope, harmonics, spectral, mel, descriptors, bands, segments, differences, hints, typed, featureVector, normalized, quality);
    }

    private static double? ReferenceLevel(ToneComparisonOptions options) =>
        options.LoudnessMode == LoudnessMode.Absolute ? null : options.ReferenceRmsDb;

    private static bool HasIssue(PreprocessedAudio a, PreprocessedAudio b, string code) => a.Issues.Contains(code) || b.Issues.Contains(code);

    private static void AddWarnings(List<AnalysisWarning> warnings, PreprocessedAudio audio, string source)
    {
        foreach (string issue in audio.Issues)
        {
            warnings.Add(new AnalysisWarning(issue, source, $"The {source} raised '{issue}' during preprocessing."));
        }

        if (audio.WasResampled)
        {
            warnings.Add(new AnalysisWarning(AnalysisWarningCodes.Resampled, source, $"The {source} was resampled to {audio.SampleRate} Hz."));
        }
    }

    private static double StageDelta(EnvelopeStageComparison stage, string featureId, List<string> unreliable)
    {
        if (stage.Difference is double delta)
        {
            return delta;
        }

        unreliable.Add(featureId);
        return 0.0;
    }

    private static SpectralComparison CompareStft(PreparedTarget target, ToneAnalysis candidate)
    {
        StftOptions options = target.Options.Stft;
        var sizes = new List<int>();
        foreach (int size in options.FftSizes)
        {
            if (!sizes.Contains(size))
            {
                sizes.Add(size);
            }
        }

        int profileSize = sizes.Contains(options.DiagnosticFftSize) ? options.DiagnosticFftSize : sizes[sizes.Count / 2];
        var resolutions = new List<StftResolutionResult>(sizes.Count);
        var profile = new List<TimeProfilePoint>();
        double convergenceSum = 0, logSum = 0;

        foreach (int size in sizes)
        {
            Spectrogram t = target.Analysis.Spectrograms[size];
            Spectrogram c = candidate.Spectrograms[size];
            float[] logT = target.LogMagnitudesDb[size];
            float floor = target.MagnitudeFloors[size];
            float floorDb = 20f * MathF.Log10(floor);
            int bins = t.BinCount;
            int frames = Math.Max(t.FrameCount, c.FrameCount);
            double numerator = 0, denominator = 0, logTotal = 0;
            double[]? perFrame = size == profileSize ? new double[frames] : null;

            for (int f = 0; f < frames; f++)
            {
                bool hasT = f < t.FrameCount, hasC = f < c.FrameCount;
                ReadOnlySpan<float> rowT = hasT ? t.Frame(f) : default;
                ReadOnlySpan<float> rowC = hasC ? c.Frame(f) : default;
                ReadOnlySpan<float> rowLogT = hasT ? logT.AsSpan(f * bins, bins) : default;
                double frameLog = 0;
                for (int k = 0; k < bins; k++)
                {
                    float tm = hasT ? rowT[k] : 0f;
                    float cm = hasC ? rowC[k] : 0f;
                    float d = tm - cm;
                    numerator += d * d;
                    denominator += tm * tm;
                    if (tm <= floor && cm <= floor)
                    {
                        continue;
                    }

                    float lt = hasT ? rowLogT[k] : floorDb;
                    float lc = cm > floor ? 20f * MathF.Log10(cm) : floorDb;
                    frameLog += Math.Abs(lt - lc);
                }

                logTotal += frameLog;
                if (perFrame is not null)
                {
                    perFrame[f] = frameLog / bins;
                }
            }

            double convergence = denominator > 0 ? Math.Sqrt(numerator / denominator) : 0.0;
            double logDistance = logTotal / ((double)frames * bins);
            resolutions.Add(new StftResolutionResult(size, t.HopSize, convergence, logDistance));
            convergenceSum += convergence;
            logSum += logDistance;

            if (perFrame is not null)
            {
                int points = Math.Min(Math.Max(1, options.TimeProfilePoints), frames);
                for (int p = 0; p < points; p++)
                {
                    int a = (int)((long)p * frames / points), b = (int)((long)(p + 1) * frames / points);
                    double sum = 0;
                    for (int f = a; f < b; f++)
                    {
                        sum += perFrame[f];
                    }

                    profile.Add(new TimeProfilePoint(t.FrameTimeMs(a) + (0.5 * (b - a) * 1000.0 * t.HopSize / t.SampleRate), sum / Math.Max(1, b - a)));
                }
            }
        }

        return new SpectralComparison(resolutions, convergenceSum / sizes.Count, logSum / sizes.Count, profile);
    }

    private static (MelComparison Comparison, double[] BandDifferences) CompareMel(PreparedTarget target, ToneAnalysis candidate)
    {
        MelOptions options = target.Options.Mel;
        ToneAnalysis t = target.Analysis;
        MelFilterbank filterbank = MelFilterbank.Get(target.SampleRate, options.FftSize, options);
        int bands = filterbank.BandCount;
        float floor = target.MelPowerFloor;
        float floorDb = 10f * MathF.Log10(floor);
        float[] logT = target.MelLogDb;
        float[] powerC = candidate.MelPower;
        int frames = Math.Max(t.MelFrameCount, candidate.MelFrameCount);
        double total = 0;
        for (int f = 0; f < frames; f++)
        {
            bool hasT = f < t.MelFrameCount, hasC = f < candidate.MelFrameCount;
            for (int b = 0; b < bands; b++)
            {
                float lt = hasT ? logT[(f * bands) + b] : floorDb;
                float pc = hasC ? powerC[(f * bands) + b] : 0f;
                float lc = pc > floor ? 10f * MathF.Log10(pc) : floorDb;
                total += Math.Abs(lt - lc);
            }
        }

        double distance = total / ((double)frames * bands);

        // Time-averaged band levels, compared within 60 dB of the strongest target band.
        double max = 0;
        foreach (double p in t.MelBandPower)
        {
            max = Math.Max(max, p);
        }

        double averageFloor = Math.Max(1e-30, max * 1e-6);
        var differences = new double[bands];
        for (int b = 0; b < bands; b++)
        {
            differences[b] = 10.0 * Math.Log10(Math.Max(candidate.MelBandPower[b], averageFloor) / Math.Max(t.MelBandPower[b], averageFloor));
        }

        IReadOnlyList<double> edges = filterbank.EdgesHz;
        var excess = new List<MelBandDifference>();
        var deficient = new List<MelBandDifference>();
        int start = -1, sign = 0;
        for (int b = 0; b <= bands; b++)
        {
            int current = b < bands && Math.Abs(differences[b]) >= options.ReportThresholdDb ? Math.Sign(differences[b]) : 0;
            if (current != sign)
            {
                if (sign != 0)
                {
                    double sum = 0, peak = 0;
                    for (int j = start; j < b; j++)
                    {
                        sum += differences[j];
                        if (Math.Abs(differences[j]) > Math.Abs(peak))
                        {
                            peak = differences[j];
                        }
                    }

                    var range = new MelBandDifference(
                        0.5 * (edges[start] + edges[start + 1]),
                        0.5 * (edges[b] + edges[b + 1]),
                        sum / (b - start),
                        peak);
                    (sign > 0 ? excess : deficient).Add(range);
                }

                start = b;
                sign = current;
            }
        }

        return (new MelComparison(distance, excess, deficient), differences);
    }

    private static List<HarmonicComparison> CompareHarmonics(HarmonicProfile target, HarmonicProfile candidate, double floorDb)
    {
        var result = new List<HarmonicComparison>(target.Harmonics.Count);
        for (int i = 0; i < target.Harmonics.Count; i++)
        {
            HarmonicPeak t = target.Harmonics[i];
            HarmonicPeak? c = i < candidate.Harmonics.Count ? candidate.Harmonics[i] : null;
            double candidateLevel = c?.RelativeDb ?? floorDb;
            result.Add(new HarmonicComparison(
                t.Number, t.NominalHz, t.RelativeDb, candidateLevel, candidateLevel - t.RelativeDb, t.AbsoluteDb, c?.AbsoluteDb ?? MathUtil.FloorDb));
        }

        return result;
    }

    private static EnvelopeComparison CompareEnvelopes(EnvelopeProfile target, EnvelopeProfile candidate, double floorDb)
    {
        bool sameShape = target.HasSustain == candidate.HasSustain;
        float[] a = target.LevelsDb, b = candidate.LevelsDb;
        int length = Math.Max(a.Length, b.Length);
        double sum = 0;
        for (int i = 0; i < length; i++)
        {
            double la = i < a.Length ? a[i] : floorDb;
            double lb = i < b.Length ? b[i] : floorDb;
            sum += Math.Abs(la - lb);
        }

        return new EnvelopeComparison(
            Stage(target.AttackMs, target.AttackConfidence, candidate.AttackMs, candidate.AttackConfidence, "ms", true),
            Stage(target.DecayMs, target.DecayConfidence, candidate.DecayMs, candidate.DecayConfidence, "ms", sameShape),
            Stage(target.SustainDb, target.SustainConfidence, candidate.SustainDb, candidate.SustainConfidence, "dB", sameShape),
            Stage(target.ReleaseMs, target.ReleaseConfidence, candidate.ReleaseMs, candidate.ReleaseConfidence, "ms", sameShape),
            length > 0 ? sum / length : 0.0,
            target.HasSustain,
            candidate.HasSustain,
            target.DurationMs,
            candidate.DurationMs);

        static EnvelopeStageComparison Stage(double? t, double tConfidence, double? c, double cConfidence, string unit, bool comparable)
        {
            bool valid = comparable && t is not null && c is not null;
            return new EnvelopeStageComparison(t, c, valid ? c - t : null, unit, valid ? Math.Min(tConfidence, cConfidence) : 0.0);
        }
    }

    private static List<FrequencyBandDifference> CompareBands(BandOptions options, IReadOnlyList<double> target, IReadOnlyList<double> candidate, int sampleRate)
    {
        var result = new List<FrequencyBandDifference>(options.Bands.Count);
        double nyquist = sampleRate / 2.0;
        for (int i = 0; i < options.Bands.Count; i++)
        {
            FrequencyBand band = options.Bands[i];
            result.Add(new FrequencyBandDifference(band.Name, band.StartHz, Math.Min(band.EndHz, nyquist), target[i], candidate[i], candidate[i] - target[i]));
        }

        return result;
    }

    private static List<TimeSegmentComparison> CompareSegments(BandOptions options, ToneAnalysis target, ToneAnalysis candidate, int sampleRate)
    {
        var result = new List<TimeSegmentComparison>(4);
        foreach (SegmentAnalysis t in target.Segments)
        {
            SegmentAnalysis? c = null;
            foreach (SegmentAnalysis s in candidate.Segments)
            {
                if (s.Name == t.Name)
                {
                    c = s;
                    break;
                }
            }

            if (c is null)
            {
                continue;
            }

            result.Add(new TimeSegmentComparison(
                t.Name, t.StartMs, t.EndMs, c.StartMs, c.EndMs,
                CompareBands(options, t.BandLevelsDb, c.BandLevelsDb, sampleRate),
                t.CentroidHz, c.CentroidHz, c.CentroidHz - t.CentroidHz));
        }

        return result;
    }

    private static List<DescriptorComparison> CompareDescriptors(ToneAnalysis t, ToneAnalysis c)
    {
        SpectralDescriptors a = t.Descriptors, b = c.Descriptors;
        HarmonicProfile ha = t.Harmonics, hb = c.Harmonics;
        return
        [
            Make(ToneFeatureIds.SpectralCentroid, "Hz", a.CentroidHz, b.CentroidHz),
            Make(ToneFeatureIds.SpectralRolloff, "Hz", a.RolloffHz, b.RolloffHz),
            Make(ToneFeatureIds.SpectralBandwidth, "Hz", a.BandwidthHz, b.BandwidthHz),
            Make(ToneFeatureIds.SpectralFlatness, "dB", a.FlatnessDb, b.FlatnessDb),
            Make(ToneFeatureIds.SpectralSlope, "dB/oct", a.SlopeDbPerOctave, b.SlopeDbPerOctave),
            Make(ToneFeatureIds.SpectralFlux, string.Empty, a.Flux, b.Flux),
            Make(ToneFeatureIds.ZeroCrossingRate, "Hz", a.ZeroCrossingRateHz, b.ZeroCrossingRateHz),
            Make(ToneFeatureIds.CrestFactor, "dB", a.CrestFactorDb, b.CrestFactorDb),
            Make(ToneFeatureIds.HarmonicToNoiseRatio, "dB", ha.HnrDb, hb.HnrDb),
            Make(ToneFeatureIds.Inharmonicity, "cents", ha.InharmonicityCents, hb.InharmonicityCents),
            Make(ToneFeatureIds.HarmonicSlope, "dB/oct", ha.SlopeDbPerOctave, hb.SlopeDbPerOctave),
            Make(ToneFeatureIds.EvenOddRatio, "dB", ha.EvenOddRatioDb, hb.EvenOddRatioDb),
            Make(ToneFeatureIds.LowHarmonics, "dB", ha.LowHarmonicsDb, hb.LowHarmonicsDb),
            Make(ToneFeatureIds.HighHarmonics, "dB", ha.HighHarmonicsDb, hb.HighHarmonicsDb),
            Make(ToneFeatureIds.LowBandEnergy, "dB", t.LowBandDb, c.LowBandDb),
            Make(ToneFeatureIds.MidBandEnergy, "dB", t.MidBandDb, c.MidBandDb),
            Make(ToneFeatureIds.HighBandEnergy, "dB", t.HighBandDb, c.HighBandDb),
        ];

        static DescriptorComparison Make(string id, string unit, double target, double candidate) =>
            new(id, unit, target, candidate, candidate - target);
    }

    private static SimilarityScores Score(
        ToneComparisonOptions options,
        SpectralComparison spectral,
        MelComparison mel,
        List<HarmonicComparison> harmonics,
        HarmonicProfile targetHarmonics,
        HarmonicProfile candidateHarmonics,
        EnvelopeComparison envelope,
        double loudnessDelta)
    {
        double spectralDistance = 0;
        foreach (StftResolutionResult r in spectral.Resolutions)
        {
            spectralDistance += (0.5 * r.SpectralConvergence) + (0.5 * r.LogMagnitudeDistanceDb / 20.0);
        }

        spectralDistance /= Math.Max(1, spectral.Resolutions.Count);
        double perceptual = mel.DistanceDb / 20.0;

        // Harmonic: differences of prominent harmonics weigh more than differences near the floor.
        double weightSum = 0, weighted = 0;
        foreach (HarmonicComparison h in harmonics)
        {
            double w = Math.Pow(10.0, Math.Max(h.TargetLevelDb, h.CandidateLevelDb) / 40.0);
            weightSum += w;
            weighted += w * Math.Abs(h.DifferenceDb);
        }

        double levelTerm = weightSum > 0 ? weighted / weightSum / 20.0 : 0.0;
        double hnrTerm = Math.Min(40.0, Math.Abs(candidateHarmonics.HnrDb - targetHarmonics.HnrDb)) / 40.0;
        double harmonic = (0.8 * levelTerm) + (0.2 * hnrTerm);

        // Envelope: curve distance plus stage terms, each weighted by how reliably it was measured.
        double stageWeight = 0, stageSum = 0;
        AddStage(envelope.Attack, 0.35, 5.0, true);
        AddStage(envelope.Decay, 0.20, 20.0, true);
        AddStage(envelope.Sustain, 0.20, 0.0, false);
        AddStage(envelope.Release, 0.25, 20.0, true);
        double stageTerm = stageWeight > 0 ? stageSum / stageWeight : 0.0;
        double envelopeDistance = (0.5 * envelope.CurveDistanceDb / 20.0) + (0.5 * stageTerm);

        double loudness = Math.Abs(loudnessDelta) / 20.0;
        ScoreWeights w5 = options.Weights;
        double loudnessWeight = options.LoudnessMode == LoudnessMode.TimbreOnly ? 0.0 : w5.Loudness;
        double totalWeight = w5.Spectral + w5.Perceptual + w5.Harmonic + w5.Envelope + loudnessWeight;
        double overall = totalWeight > 0
            ? ((w5.Spectral * spectralDistance) + (w5.Perceptual * perceptual) + (w5.Harmonic * harmonic) + (w5.Envelope * envelopeDistance) + (loudnessWeight * loudness)) / totalWeight
            : 0.0;

        return new SimilarityScores(overall, spectralDistance, harmonic, envelopeDistance, perceptual, loudness, Math.Exp(-3.0 * overall));

        void AddStage(EnvelopeStageComparison stage, double weight, double offsetMs, bool isTime)
        {
            if (stage.Difference is not double delta || stage.Target is not double t || stage.Candidate is not double c)
            {
                return;
            }

            double term = isTime
                ? Math.Abs(Math.Log2((c + offsetMs) / (t + offsetMs))) / 3.0
                : Math.Abs(delta) / 20.0;
            stageWeight += weight * stage.Confidence;
            stageSum += weight * stage.Confidence * term;
        }
    }

    private static ToneComparisonResult Unusable(PreparedTarget target, PreprocessedAudio audio)
    {
        double penalty = target.Options.UnusableCandidateDistance;
        var warnings = new List<AnalysisWarning>();
        AddWarnings(warnings, audio, "candidate");
        FeatureVectorSchema schema = target.Schema;
        var ids = new string[schema.Features.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = schema.Features[i].Id;
        }

        var empty = new EnvelopeStageComparison(null, null, null, "ms", 0.0);
        var zeros = new double[ids.Length];
        return new ToneComparisonResult(
            new SimilarityScores(penalty, penalty, penalty, penalty, penalty, penalty, 0.0),
            new EnvelopeComparison(empty, empty, empty with { Unit = "dB" }, empty, 0.0, target.Analysis.Envelope.HasSustain, false, target.Analysis.Envelope.DurationMs, audio.DurationMs),
            [],
            new SpectralComparison([], 0.0, 0.0, []),
            new MelComparison(0.0, [], []),
            [],
            [],
            [],
            [],
            [],
            new ToneDifferenceVector(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new double[target.Options.FeatureVector.HarmonicCount]),
            new FeatureVector(ids, zeros),
            new NormalizedFeatureVector(ids, zeros),
            new AnalysisQuality(false, 0.0, target.FundamentalHz, target.FundamentalConfidence, 0.0, target.Analysis.Audio.OnsetOffsetMs, audio.OnsetOffsetMs, ids, warnings));
    }
}
