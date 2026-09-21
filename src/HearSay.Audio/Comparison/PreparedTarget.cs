using System.Globalization;
using HearSay.Audio;
using HearSay.Models;
using HearSay.Spectral;

namespace HearSay.Comparison;

/// <summary>
/// A fully analysed target with everything that only depends on the target computed once: spectrograms at every
/// resolution, their log magnitudes, the log-Mel representation, harmonic profile, envelope, descriptors and band
/// energies. Immutable and thread-safe, so many candidates can be compared against it in parallel.
/// The DSP data is not serialized; a checkpointed session re-creates it from the target audio.
/// </summary>
public sealed class PreparedTarget
{
    internal PreparedTarget(
        ToneComparisonOptions options,
        ToneAnalysis analysis,
        double fundamentalConfidence,
        FeatureVectorSchema schema,
        string fingerprint)
    {
        Options = options;
        Analysis = analysis;
        FundamentalConfidence = fundamentalConfidence;
        Schema = schema;
        Fingerprint = fingerprint;

        // Cache log magnitudes of every compared resolution; candidates only compute their own.
        double range = options.Stft.DynamicRangeDb;
        var logs = new Dictionary<int, float[]>();
        var floors = new Dictionary<int, float>();
        foreach (int size in options.Stft.FftSizes)
        {
            if (logs.ContainsKey(size))
            {
                continue;
            }

            Spectrogram spectrogram = analysis.Spectrograms[size];
            float floor = (float)(spectrogram.MaxMagnitude * Math.Pow(10.0, -range / 20.0));
            floor = Math.Max(floor, 1e-12f);
            float[] magnitudes = spectrogram.Magnitudes;
            var log = new float[magnitudes.Length];
            for (int i = 0; i < log.Length; i++)
            {
                log[i] = 20f * MathF.Log10(Math.Max(magnitudes[i], floor));
            }

            logs[size] = log;
            floors[size] = floor;
        }

        LogMagnitudesDb = logs;
        MagnitudeFloors = floors;

        float melMax = 0f;
        foreach (float p in analysis.MelPower)
        {
            melMax = Math.Max(melMax, p);
        }

        MelPowerFloor = Math.Max(1e-24f, (float)(melMax * Math.Pow(10.0, -options.Mel.DynamicRangeDb / 10.0)));
        var melLog = new float[analysis.MelPower.Length];
        for (int i = 0; i < melLog.Length; i++)
        {
            melLog[i] = 10f * MathF.Log10(Math.Max(analysis.MelPower[i], MelPowerFloor));
        }

        MelLogDb = melLog;
    }

    /// <summary>Configuration the target was prepared with; candidates are analysed with the same configuration.</summary>
    public ToneComparisonOptions Options { get; }

    /// <summary>Complete single-signal analysis of the target.</summary>
    public ToneAnalysis Analysis { get; }

    /// <summary>Fundamental used for target and candidates, in Hz.</summary>
    public double FundamentalHz => Analysis.FundamentalHz;

    /// <summary>Confidence 0..1 in the fundamental; 1 when it was supplied by the caller.</summary>
    public double FundamentalConfidence { get; }

    /// <summary>Sample rate the analysis runs at, in Hz.</summary>
    public int SampleRate => Analysis.Audio.SampleRate;

    /// <summary>Schema of the dense feature vectors produced against this target.</summary>
    public FeatureVectorSchema Schema { get; }

    /// <summary>Hash of the target audio, used to verify that a restored session is paired with the same target.</summary>
    public string Fingerprint { get; }

    internal IReadOnlyDictionary<int, float[]> LogMagnitudesDb { get; }

    internal IReadOnlyDictionary<int, float> MagnitudeFloors { get; }

    internal float[] MelLogDb { get; }

    internal float MelPowerFloor { get; }

    internal static string ComputeFingerprint(AudioNote note)
    {
        // FNV-1a over the raw sample bits and the sample rate.
        ulong hash = 14695981039346656037UL;
        void Mix(uint value)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash = (hash ^ ((value >> shift) & 0xFF)) * 1099511628211UL;
            }
        }

        Mix((uint)note.SampleRate);
        foreach (float sample in note.Samples.Span)
        {
            Mix(BitConverter.SingleToUInt32Bits(sample));
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
