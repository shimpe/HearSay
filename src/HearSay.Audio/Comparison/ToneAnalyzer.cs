using HearSay.Dsp;
using HearSay.Envelope;
using HearSay.Features;
using HearSay.Harmonics;
using HearSay.Models;
using HearSay.Preprocessing;
using HearSay.Spectral;

namespace HearSay.Comparison;

/// <summary>Runs every single-signal analysis on a preprocessed note. Stateless apart from its injected analyzers.</summary>
internal sealed class ToneAnalyzer
{
    private const double MinimumSegmentMs = 5.0;

    private readonly IStftAnalyzer stft;
    private readonly IHarmonicAnalyzer harmonics;
    private readonly IEnvelopeAnalyzer envelope;
    private readonly ISpectralFeatureAnalyzer descriptors;

    public ToneAnalyzer(IStftAnalyzer stft, IHarmonicAnalyzer harmonics, IEnvelopeAnalyzer envelope, ISpectralFeatureAnalyzer descriptors)
    {
        this.stft = stft;
        this.harmonics = harmonics;
        this.envelope = envelope;
        this.descriptors = descriptors;
    }

    public ToneAnalysis Analyze(PreprocessedAudio audio, double fundamentalHz, ToneComparisonOptions options, EnvelopeProfile? alignTo)
    {
        int sampleRate = audio.SampleRate;
        float[] samples = audio.Samples;
        EnvelopeProfile profile = envelope.Analyze(samples, sampleRate, fundamentalHz, options.Envelope);

        // Alignment refinement only shifts the signal used for time-frequency analysis.
        int shift = 0;
        if (alignTo is not null && options.Preprocessing.RefineAlignment)
        {
            int hop = Math.Max(1, (int)Math.Round(options.Envelope.HopMs * sampleRate / 1000.0));
            shift = EstimateLagFrames(alignTo, profile, options.Preprocessing.MaxAlignmentShiftMs) * hop;
        }

        float[] shifted = Shift(samples, shift);
        double shiftMs = 1000.0 * shift / sampleRate;

        // Spectrograms: one per distinct FFT size.
        int harmonicSize = harmonics.ChooseFftSize(sampleRate, fundamentalHz, options.Harmonic);
        var spectrograms = new Dictionary<int, Spectrogram>();
        foreach (int size in options.Stft.FftSizes)
        {
            AddSpectrogram(spectrograms, shifted, sampleRate, size, options.Stft.HopFraction);
        }

        AddSpectrogram(spectrograms, shifted, sampleRate, options.Stft.DiagnosticFftSize, options.Stft.HopFraction);
        AddSpectrogram(spectrograms, shifted, sampleRate, options.Mel.FftSize, options.Mel.HopFraction);
        AddSpectrogram(spectrograms, shifted, sampleRate, harmonicSize, 0.25);

        // Mel.
        Spectrogram melSource = spectrograms[options.Mel.FftSize];
        MelFilterbank filterbank = MelFilterbank.Get(sampleRate, options.Mel.FftSize, options.Mel);
        float[] melPower = filterbank.Apply(melSource);
        int melBands = filterbank.BandCount;
        var melBandPower = new double[melBands];
        for (int f = 0; f < melSource.FrameCount; f++)
        {
            for (int b = 0; b < melBands; b++)
            {
                melBandPower[b] += melPower[(f * melBands) + b];
            }
        }

        for (int b = 0; b < melBands; b++)
        {
            melBandPower[b] /= melSource.FrameCount;
        }

        // Whole-note spectrum, harmonics and descriptors.
        var average = AveragePowerSpectrum.FromSpectrogram(spectrograms[harmonicSize]);
        AveragePowerSpectrum steady = SteadySpectrum(spectrograms[harmonicSize], profile, shiftMs) ?? average;
        HarmonicProfile harmonicProfile = harmonics.Analyze(steady, fundamentalHz, options.Harmonic);
        Spectrogram diagnostic = spectrograms[options.Stft.DiagnosticFftSize];
        SpectralDescriptors spectral = descriptors.Analyze(samples, sampleRate, average, diagnostic, fundamentalHz);

        bool absolute = options.LoudnessMode == LoudnessMode.Absolute;
        BandOptions bands = options.Bands;
        double nyquist = sampleRate / 2.0;

        return new ToneAnalysis
        {
            Audio = audio,
            FundamentalHz = fundamentalHz,
            Envelope = profile,
            Spectrograms = spectrograms,
            MelPower = melPower,
            MelFrameCount = melSource.FrameCount,
            MelBandPower = melBandPower,
            AverageSpectrum = average,
            Harmonics = harmonicProfile,
            Descriptors = spectral,
            BandLevelsDb = BandLevels(average, bands, absolute),
            LowBandDb = BandLevel(average, 20.0, bands.LowMidSplitHz, absolute, bands.FloorDb),
            MidBandDb = BandLevel(average, bands.LowMidSplitHz, bands.MidHighSplitHz, absolute, bands.FloorDb),
            HighBandDb = BandLevel(average, bands.MidHighSplitHz, nyquist, absolute, bands.FloorDb),
            Segments = AnalyzeSegments(profile, diagnostic, shiftMs, bands, absolute),
            AlignmentShiftSamples = shift,
        };
    }

    /// <summary>
    /// Lag, in envelope frames, that best aligns the attack portion of the candidate envelope with the target envelope.
    /// Positive: the candidate is late.
    /// </summary>
    internal static int EstimateLagFrames(EnvelopeProfile target, EnvelopeProfile candidate, double maxShiftMs)
    {
        double hopMs = target.HopMs;
        if (hopMs <= 0 || target.LevelsDb.Length < 4 || candidate.LevelsDb.Length < 4)
        {
            return 0;
        }

        int maxLag = (int)Math.Floor(maxShiftMs / hopMs);
        if (maxLag < 1)
        {
            return 0;
        }

        int region = (int)(Math.Max(60.0, 3.0 * (target.AttackMs ?? 20.0)) / hopMs);
        region = Math.Min(region, target.LevelsDb.Length);
        var a = new double[region];
        for (int i = 0; i < region; i++)
        {
            a[i] = MathUtil.DbToAmplitude(target.LevelsDb[i]);
        }

        int candidateLength = Math.Min(candidate.LevelsDb.Length, region + maxLag);
        var b = new double[candidateLength];
        for (int i = 0; i < candidateLength; i++)
        {
            b[i] = MathUtil.DbToAmplitude(candidate.LevelsDb[i]);
        }

        double best = double.NegativeInfinity, atZero = 0;
        int bestLag = 0;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double ab = 0, aa = 0, bb = 0;
            for (int i = 0; i < region; i++)
            {
                int j = i + lag;
                if (j < 0 || j >= candidateLength)
                {
                    continue;
                }

                ab += a[i] * b[j];
                aa += a[i] * a[i];
                bb += b[j] * b[j];
            }

            double correlation = aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : 0.0;
            if (lag == 0)
            {
                atZero = correlation;
            }

            if (correlation > best)
            {
                best = correlation;
                bestLag = lag;
            }
        }

        return atZero >= best - 1e-4 ? 0 : bestLag;
    }

    /// <summary>
    /// Spectrum of the frames that lie completely inside the sustain stage. Measuring harmonics there keeps the
    /// spectral spreading caused by attack, decay and release out of the harmonic levels and the noise estimate.
    /// Returns null when the note has no sustain stage that is long enough.
    /// </summary>
    private static AveragePowerSpectrum? SteadySpectrum(Spectrogram spectrogram, EnvelopeProfile profile, double shiftMs)
    {
        if (!profile.HasSustain || profile.SustainStartMs is not double start || profile.ReleaseStartMs is not double end)
        {
            return null;
        }

        double halfWindowMs = 500.0 * spectrogram.FftSize / spectrogram.SampleRate;
        double frameMs = 1000.0 * spectrogram.HopSize / spectrogram.SampleRate;
        int first = Math.Max(0, (int)Math.Ceiling((start + halfWindowMs - shiftMs) / frameMs));
        int last = Math.Min(spectrogram.FrameCount - 1, (int)Math.Floor((end - halfWindowMs - shiftMs) / frameMs));
        return last >= first ? AveragePowerSpectrum.FromSpectrogram(spectrogram, first, last - first + 1) : null;
    }

    private static float[] Shift(float[] samples, int shift)
    {
        if (shift == 0)
        {
            return samples;
        }

        if (shift > 0)
        {
            return shift < samples.Length ? samples.AsSpan(shift).ToArray() : samples;
        }

        var padded = new float[samples.Length - shift];
        samples.CopyTo(padded, -shift);
        return padded;
    }

    private void AddSpectrogram(Dictionary<int, Spectrogram> spectrograms, float[] samples, int sampleRate, int size, double hopFraction)
    {
        if (!spectrograms.ContainsKey(size))
        {
            int hop = Math.Max(1, (int)Math.Round(size * hopFraction));
            spectrograms[size] = stft.Analyze(samples, sampleRate, size, hop);
        }
    }

    private static double[] BandLevels(AveragePowerSpectrum spectrum, BandOptions bands, bool absolute)
    {
        var levels = new double[bands.Bands.Count];
        for (int i = 0; i < levels.Length; i++)
        {
            FrequencyBand band = bands.Bands[i];
            levels[i] = BandLevel(spectrum, band.StartHz, band.EndHz, absolute, bands.FloorDb);
        }

        return levels;
    }

    private static double BandLevel(AveragePowerSpectrum spectrum, double startHz, double endHz, bool absolute, double floorDb)
    {
        double power = spectrum.BandPower(startHz, Math.Min(endHz, (spectrum.SampleRate / 2.0) + 1.0));
        if (absolute)
        {
            return Math.Max(floorDb - 20.0, MathUtil.PowerToDb(power));
        }

        return power > 0 && spectrum.TotalPower > 0
            ? Math.Max(floorDb, 10.0 * Math.Log10(power / spectrum.TotalPower))
            : floorDb;
    }

    private List<SegmentAnalysis> AnalyzeSegments(EnvelopeProfile profile, Spectrogram diagnostic, double shiftMs, BandOptions bands, bool absolute)
    {
        var segments = new List<SegmentAnalysis>(4);
        double duration = profile.DurationMs;
        if (duration <= 0 || profile.AttackMs is null)
        {
            return segments;
        }

        double attackEnd = Math.Min(duration, Math.Max(profile.PeakMs, profile.OnsetMs + 10.0));
        AddSegment(TimeSegments.Attack, 0.0, attackEnd);
        if (profile.HasSustain && profile.SustainStartMs is double sustainStart && profile.ReleaseStartMs is double releaseStart)
        {
            double decayEnd = Math.Max(attackEnd, sustainStart);
            AddSegment(TimeSegments.Decay, attackEnd, decayEnd);
            AddSegment(TimeSegments.Sustain, decayEnd, Math.Max(decayEnd, releaseStart));
            AddSegment(TimeSegments.Release, Math.Max(decayEnd, releaseStart), duration);
        }
        else
        {
            AddSegment(TimeSegments.Decay, attackEnd, duration);
        }

        return segments;

        void AddSegment(string name, double startMs, double endMs)
        {
            if (endMs - startMs < MinimumSegmentMs)
            {
                return;
            }

            double frameMs = 1000.0 * diagnostic.HopSize / diagnostic.SampleRate;
            int first = Math.Clamp((int)Math.Round((startMs - shiftMs) / frameMs), 0, diagnostic.FrameCount - 1);
            int last = Math.Clamp((int)Math.Round((endMs - shiftMs) / frameMs), first, diagnostic.FrameCount - 1);
            var spectrum = AveragePowerSpectrum.FromSpectrogram(diagnostic, first, last - first + 1);
            segments.Add(new SegmentAnalysis(name, startMs, endMs, BandLevels(spectrum, bands, absolute), descriptors.Centroid(spectrum)));
        }
    }
}
