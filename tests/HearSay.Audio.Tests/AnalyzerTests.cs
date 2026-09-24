using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Dsp;
using HearSay.Envelope;
using HearSay.Harmonics;
using HearSay.Models;
using HearSay.Preprocessing;
using HearSay.Spectral;

namespace HearSay.Tests;

public class AnalyzerTests
{
    private const int SampleRate = TestSignals.SampleRate;

    [Fact]
    public void Fft_FindsSinusoidAtItsBin()
    {
        const int Size = 1024;
        var real = new float[Size];
        var imaginary = new float[Size];
        for (int i = 0; i < Size; i++)
        {
            real[i] = MathF.Cos(2 * MathF.PI * 37 * i / Size);
        }

        FftPlan.Get(Size).Forward(real, imaginary);

        for (int k = 0; k < Size / 2; k++)
        {
            double magnitude = Math.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k]));
            Assert.Equal(k == 37 ? Size / 2.0 : 0.0, magnitude, 2);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => FftPlan.Get(1000));
    }

    [Fact]
    public void Spectrogram_IsAmplitudeCalibrated_AndPowerSpectrumMatchesSignalPower()
    {
        float[] tone = TestSignals.Tone(1000, [1.0], seconds: 1.0, attackMs: 0.01, releaseMs: 0.01, gain: 0.5);
        Spectrogram spectrogram = new StftAnalyzer().Analyze(tone, SampleRate, 4096, 1024);
        var spectrum = AveragePowerSpectrum.FromSpectrogram(spectrogram, 4, spectrogram.FrameCount - 8);

        Assert.InRange(spectrogram.MaxMagnitude, 0.47, 0.51);
        Assert.InRange(spectrum.TotalPower, 0.120, 0.130); // A^2 / 2 = 0.125
        Assert.InRange(spectrum.BandPower(900, 1100) / spectrum.TotalPower, 0.999, 1.001);
    }

    [Fact]
    public void Resampler_PreservesToneAndLength()
    {
        float[] tone = TestSignals.Tone(1000, [1.0], seconds: 0.5, sampleRate: 48000);
        float[] resampled = Resampler.Resample(tone, 48000, 44100);
        Spectrogram spectrogram = new StftAnalyzer().Analyze(resampled, 44100, 4096, 1024);
        ReadOnlySpan<float> frame = spectrogram.Frame(spectrogram.FrameCount / 2);
        int peak = 0;
        for (int k = 1; k < frame.Length; k++)
        {
            if (frame[k] > frame[peak])
            {
                peak = k;
            }
        }

        Assert.Equal(22050, resampled.Length);
        Assert.InRange(peak * spectrogram.BinHz, 990, 1010);
        Assert.InRange(frame[peak], 0.27, 0.31);
    }

    [Fact]
    public void Preprocessor_RemovesDc_TrimsSilence_AndMeasuresLoudness()
    {
        float[] tone = TestSignals.Tone(440, [1.0], seconds: 0.5, attackMs: 2, releaseMs: 5, gain: 0.5);
        var padded = new float[tone.Length + 8820];
        for (int i = 0; i < padded.Length; i++)
        {
            padded[i] = 0.1f + (i >= 4410 && i < 4410 + tone.Length ? tone[i - 4410] : 0f);
        }

        var preprocessor = new AudioPreprocessor();
        PreprocessedAudio raw = preprocessor.Process(TestSignals.Note(padded), new PreprocessingOptions(), SampleRate, null);
        PreprocessedAudio normalized = preprocessor.Process(TestSignals.Note(padded), new PreprocessingOptions(), SampleRate, -20.0);

        Assert.True(raw.IsUsable);
        Assert.InRange(raw.DcOffset, 0.099, 0.101);
        Assert.InRange(raw.OnsetOffsetMs, 95, 102);
        Assert.InRange(raw.DurationMs, 480, 505);
        Assert.InRange(raw.ActiveRmsDb, -9.6, -8.8); // 0.5 / sqrt(2) = -9.03 dBFS
        Assert.Equal(0.0, raw.AppliedGainDb);
        Assert.InRange(normalized.AppliedGainDb, -11.2, -10.4);
    }

    [Fact]
    public void Preprocessor_FlagsInvalidAndClippedInput()
    {
        float[] tone = TestSignals.Tone(440, [1.0], gain: 1.5);
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = Math.Clamp(tone[i], -1f, 1f);
        }

        tone[100] = float.NaN;
        tone[200] = float.PositiveInfinity;
        PreprocessedAudio result = new AudioPreprocessor().Process(TestSignals.Note(tone), new PreprocessingOptions(), SampleRate, null);

        Assert.True(result.IsUsable);
        Assert.Equal(2, result.InvalidSampleCount);
        Assert.True(result.ClippedFraction > 0.1);
        Assert.Contains(AnalysisWarningCodes.Clipping, result.Issues);
        Assert.Contains(AnalysisWarningCodes.InvalidSamples, result.Issues);
        Assert.All(result.Samples, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void Preprocessor_FindsOnsetAboveNoiseFloor()
    {
        float[] tone = TestSignals.Tone(440, [1.0], seconds: 0.5, attackMs: 2, gain: 0.5);
        float[] noisy = TestSignals.AddNoise(TestSignals.Delay(tone, 200), 0.005);
        PreprocessedAudio result = new AudioPreprocessor().Process(TestSignals.Note(noisy), new PreprocessingOptions(), SampleRate, null);

        Assert.InRange(result.OnsetOffsetMs, 190, 205);
    }

    [Fact]
    public void EnvelopeAnalyzer_MeasuresAdsrStages()
    {
        float[] tone = TestSignals.Tone(440, TestSignals.Rolloff(8, 1.0), seconds: 1.2, attackMs: 40, decayMs: 150, sustain: 0.5, releaseMs: 200);
        EnvelopeProfile profile = new EnvelopeAnalyzer().Analyze(tone, SampleRate, 440, new EnvelopeOptions());

        Assert.True(profile.HasSustain);
        Assert.InRange(profile.AttackMs!.Value, 32, 48);
        Assert.InRange(profile.DecayMs!.Value, 110, 160);      // 90% of a 150 ms linear decay is 135 ms
        Assert.InRange(profile.SustainDb!.Value, -6.6, -5.4);  // 0.5 = -6.02 dB
        Assert.InRange(profile.ReleaseStartMs!.Value, 970, 1030);
        Assert.InRange(profile.ReleaseMs!.Value, 160, 200);    // -20 dB is reached after 90% of a linear release
        Assert.True(profile.AttackConfidence > 0.8);
        Assert.True(profile.SustainConfidence > 0.8);
        Assert.True(profile.ReleaseConfidence > 0.8);
    }

    [Fact]
    public void EnvelopeAnalyzer_ReportsMissingStagesInsteadOfInventingThem()
    {
        // Plucked note: exponential decay, no sustain plateau.
        var plucked = new float[SampleRate];
        for (int i = 0; i < plucked.Length; i++)
        {
            double t = (double)i / SampleRate;
            plucked[i] = (float)(0.5 * Math.Min(1.0, t / 0.002) * Math.Exp(-t / 0.08) * Math.Sin(2 * Math.PI * 440 * t));
        }

        EnvelopeProfile pluck = new EnvelopeAnalyzer().Analyze(plucked.AsSpan(0, SampleRate / 2), SampleRate, 440, new EnvelopeOptions());

        Assert.False(pluck.HasSustain);
        Assert.Null(pluck.SustainDb);
        Assert.Null(pluck.ReleaseMs);
        Assert.Equal(0.0, pluck.ReleaseConfidence);
        Assert.InRange(pluck.DecayMs!.Value, 150, 215); // 20 dB at 8.69 dB per 80 ms = 184 ms

        // Truncated recording: starts and ends at full level.
        float[] truncated = TestSignals.Tone(440, [1.0], seconds: 0.5, attackMs: 0.001, releaseMs: 0.001);
        EnvelopeProfile cut = new EnvelopeAnalyzer().Analyze(truncated, SampleRate, 440, new EnvelopeOptions());

        Assert.True(cut.AttackConfidence <= 0.5);
        Assert.True(cut.ReleaseConfidence <= 0.3);
    }

    [Fact]
    public void HarmonicAnalyzer_MeasuresRelativeLevels_IndependentOfPhaseAndBinPosition()
    {
        var analyzer = new HarmonicAnalyzer();
        var stft = new StftAnalyzer();
        foreach (double f0 in new[] { 220.0, 233.08, 441.3 })
        {
            foreach (double phase in new[] { 0.0, 1.1 })
            {
                float[] tone = TestSignals.Tone(f0, [1.0, 0.5, 0.25, 0.0, 0.1], attackMs: 5, phase: phase);
                int size = analyzer.ChooseFftSize(SampleRate, f0, new HarmonicOptions());
                var spectrum = AveragePowerSpectrum.FromSpectrogram(stft.Analyze(tone, SampleRate, size, size / 4));
                HarmonicProfile profile = analyzer.Analyze(spectrum, f0, new HarmonicOptions());

                double h1 = profile.Harmonics[0].RelativeDb;
                Assert.InRange(profile.Harmonics[1].RelativeDb - h1, -6.3, -5.7);
                Assert.InRange(profile.Harmonics[2].RelativeDb - h1, -12.4, -11.6);
                Assert.True(profile.Harmonics[3].RelativeDb - h1 < -40);
                Assert.InRange(profile.Harmonics[4].RelativeDb - h1, -20.5, -19.5);
                Assert.InRange(profile.FundamentalHz, f0 * 0.999, f0 * 1.001);
                Assert.True(profile.InharmonicityCents < 2);
                Assert.True(profile.HnrDb > 25);
            }
        }
    }

    [Fact]
    public void HarmonicAnalyzer_DistinguishesOddOnlySpectra_AndStretchedPartials()
    {
        var analyzer = new HarmonicAnalyzer();
        var stft = new StftAnalyzer();
        HarmonicProfile Profile(float[] tone)
        {
            var spectrum = AveragePowerSpectrum.FromSpectrogram(stft.Analyze(tone, SampleRate, 8192, 2048));
            return analyzer.Analyze(spectrum, 220, new HarmonicOptions());
        }

        double[] square = [1, 0, 1 / 3.0, 0, 1 / 5.0, 0, 1 / 7.0, 0, 1 / 9.0];
        HarmonicProfile squareProfile = Profile(TestSignals.Tone(220, square));
        HarmonicProfile sawProfile = Profile(TestSignals.Tone(220, TestSignals.Rolloff(9, 1.0)));

        Assert.True(squareProfile.EvenOddRatioDb < -25);
        Assert.InRange(sawProfile.EvenOddRatioDb, -8, -3);

        // Partials stretched by 12 cents per harmonic number above the fundamental.
        var stretched = new float[SampleRate];
        for (int i = 0; i < stretched.Length; i++)
        {
            double t = (double)i / SampleRate;
            for (int k = 1; k <= 5; k++)
            {
                double f = 220.0 * k * Math.Pow(2.0, 12.0 * (k - 1) / 1200.0);
                stretched[i] += (float)(0.1 / k * Math.Sin(2 * Math.PI * f * t));
            }
        }

        Assert.True(Profile(stretched).InharmonicityCents > 10);
        Assert.True(sawProfile.InharmonicityCents < 2);
    }

    [Fact]
    public void FundamentalEstimator_HandlesLowAndHighNotes()
    {
        foreach (double f0 in new[] { 55.0, 220.0, 1760.0 })
        {
            float[] tone = TestSignals.Tone(f0, TestSignals.Rolloff(10, 1.0));
            FundamentalEstimate estimate = FundamentalEstimator.Estimate(tone, SampleRate);

            Assert.InRange(estimate.FrequencyHz, f0 * 0.99, f0 * 1.01);
            Assert.True(estimate.Confidence > 0.8);
        }

        Assert.Equal(0.0, FundamentalEstimator.Estimate(new float[10], SampleRate).FrequencyHz);
    }

    [Fact]
    public void MelFilterbank_CoversRange_AndHasNoEmptyBands()
    {
        var options = new MelOptions();
        MelFilterbank filterbank = MelFilterbank.Get(SampleRate, options.FftSize, options);
        float[] noise = TestSignals.AddNoise(new float[SampleRate / 2], 0.1);
        Spectrogram spectrogram = new StftAnalyzer().Analyze(noise, SampleRate, options.FftSize, 512);
        float[] mel = filterbank.Apply(spectrogram);

        Assert.Same(filterbank, MelFilterbank.Get(SampleRate, options.FftSize, options));
        Assert.Equal(options.BandCount + 2, filterbank.EdgesHz.Count);
        Assert.Equal(options.MinimumHz, filterbank.EdgesHz[0], 6);
        Assert.Equal(options.MaximumHz, filterbank.EdgesHz[^1], 6);
        int middle = spectrogram.FrameCount / 2 * options.BandCount;
        for (int b = 0; b < options.BandCount; b++)
        {
            Assert.True(mel[middle + b] > 0, $"band {b} is empty");
        }
    }

    [Fact]
    public void LowNote_UsesLargerFft_AndStillResolvesHarmonics()
    {
        var comparer = new ToneComparer();
        float[] target = TestSignals.Tone(41.2, [1.0, 0.5, 0.25], seconds: 1.5);
        float[] candidate = TestSignals.Tone(41.2, [1.0, 1.0, 0.25], seconds: 1.5);
        PreparedTarget prepared = comparer.PrepareTarget(TestSignals.Note(target, 41.2));
        ToneComparisonResult result = comparer.Compare(prepared, TestSignals.Note(candidate, 41.2));

        Assert.True(prepared.Analysis.AverageSpectrum.FftSize >= 8192);
        Assert.InRange(result.Harmonics[1].DifferenceDb, 3.5, 5.5);
    }

    [Fact]
    public void WavFile_RoundTrips_AndDownmixesStereo()
    {
        float[] tone = TestSignals.Tone(440, [1.0], seconds: 0.1);
        using var mono = new MemoryStream();
        WavFile.Write(mono, tone, SampleRate);
        mono.Position = 0;
        AudioNote restored = WavFile.Read(mono, expectedFundamentalHz: 440);

        Assert.Equal(SampleRate, restored.SampleRate);
        Assert.Equal(440, restored.ExpectedFundamentalHz);
        Assert.Equal(tone.Length, restored.Samples.Length);
        for (int i = 0; i < tone.Length; i += 97)
        {
            Assert.Equal(tone[i], restored.Samples.Span[i], 3);
        }

        // Stereo 32-bit float: left = 0.5, right = -0.25.
        using var stereo = new MemoryStream();
        using (var writer = new BinaryWriter(stereo, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            const int Frames = 100;
            writer.Write("RIFF"u8);
            writer.Write(36 + (Frames * 8));
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)3);
            writer.Write((short)2);
            writer.Write(48000);
            writer.Write(48000 * 8);
            writer.Write((short)8);
            writer.Write((short)32);
            writer.Write("data"u8);
            writer.Write(Frames * 8);
            for (int i = 0; i < Frames; i++)
            {
                writer.Write(0.5f);
                writer.Write(-0.25f);
            }
        }

        stereo.Position = 0;
        AudioNote downmix = WavFile.Read(stereo);
        stereo.Position = 0;
        AudioNote right = WavFile.Read(stereo, WavChannelMode.SingleChannel, 1);

        Assert.Equal(48000, downmix.SampleRate);
        Assert.Equal(100, downmix.Samples.Length);
        Assert.Equal(0.125f, downmix.Samples.Span[50], 6);
        Assert.Equal(-0.25f, right.Samples.Span[50], 6);
        Assert.Throws<InvalidDataException>(() => WavFile.Read(new MemoryStream(new byte[64])));
    }

    [Fact]
    public void WavFile_AlignsChannelsThatAreDelayedCopiesBeforeMixingThemDown()
    {
        // Right is left 11 samples later, as in a widened stereo file: averaging them as they are would comb-filter the
        // sound, with notches every sampleRate / 11 hertz.
        float[] left = TestSignals.AddNoise(TestSignals.Tone(440, TestSignals.Rolloff(12, 1.0), seconds: 0.3), 0.05);
        const int Delay = 11;
        using var stereo = new MemoryStream();
        using (var writer = new BinaryWriter(stereo, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + (left.Length * 8));
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)3);
            writer.Write((short)2);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 8);
            writer.Write((short)8);
            writer.Write((short)32);
            writer.Write("data"u8);
            writer.Write(left.Length * 8);
            for (int i = 0; i < left.Length; i++)
            {
                writer.Write(left[i]);
                writer.Write(i >= Delay ? left[i - Delay] : 0f);
            }
        }

        stereo.Position = 0;
        AudioNote mixed = WavFile.Read(stereo);

        for (int i = 0; i < left.Length - Delay; i++)
        {
            Assert.Equal(left[i], mixed.Samples.Span[i], 5);
        }
    }
}
