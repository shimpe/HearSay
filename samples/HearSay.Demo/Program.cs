using System.Globalization;
using HearSay;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Models;

// Usage:
//   HearSay.Demo <target.wav> <candidate.wav> [--f0 <Hz>] [--json <file>] [--timbre-only | --absolute] [--channel <n>]
//   HearSay.Demo --generate <directory>      writes an example target.wav and candidate.wav
//   HearSay.Demo                             generates the example pair in a temp directory and compares it
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

try
{
    return Run(args);
}
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static int Run(string[] args)
{
    var positional = new List<string>();
    double? f0 = null;
    string? jsonPath = null;
    string? generateDirectory = null;
    int? channel = null;
    LoudnessMode mode = LoudnessMode.SeparateLoudness;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--f0" when i + 1 < args.Length:
                f0 = double.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--json" when i + 1 < args.Length:
                jsonPath = args[++i];
                break;
            case "--generate" when i + 1 < args.Length:
                generateDirectory = args[++i];
                break;
            case "--channel" when i + 1 < args.Length:
                channel = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--timbre-only":
                mode = LoudnessMode.TimbreOnly;
                break;
            case "--absolute":
                mode = LoudnessMode.Absolute;
                break;
            case "--help" or "-h":
                Console.WriteLine("HearSay.Demo <target.wav> <candidate.wav> [--f0 <Hz>] [--json <file>] [--timbre-only | --absolute] [--channel <n>]");
                Console.WriteLine("HearSay.Demo --generate <directory>");
                return 0;
            default:
                positional.Add(args[i]);
                break;
        }
    }

    if (generateDirectory is not null)
    {
        (string t, string c) = ExampleTones.Write(generateDirectory);
        Console.WriteLine($"Wrote {t} and {c} (A3, 220 Hz).");
        return 0;
    }

    if (positional.Count == 0)
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-demo");
        (string t, string c) = ExampleTones.Write(directory);
        Console.WriteLine($"No files given; comparing the generated example pair in {directory}");
        Console.WriteLine();
        positional.AddRange([t, c]);
        f0 ??= ExampleTones.FundamentalHz;
    }

    if (positional.Count != 2)
    {
        Console.Error.WriteLine("error: expected a target and a candidate WAV file. Use --help for usage.");
        return 2;
    }

    // Stereo files are downmixed to mono unless a channel is selected.
    WavChannelMode channelMode = channel is null ? WavChannelMode.Downmix : WavChannelMode.SingleChannel;
    AudioNote target = WavFile.Read(positional[0], channelMode, channel ?? 0, f0);
    AudioNote candidate = WavFile.Read(positional[1], channelMode, channel ?? 0, f0);

    var comparer = new ToneComparer();
    ToneComparisonResult result = comparer.Compare(target, candidate, new ToneComparisonOptions { LoudnessMode = mode });
    PrintReport(result);

    string json = HearSayJson.Serialize(result);
    if (jsonPath is not null)
    {
        File.WriteAllText(jsonPath, json);
        Console.WriteLine($"Full analysis written to {jsonPath}");
    }
    else
    {
        Console.WriteLine("Full analysis (JSON):");
        Console.WriteLine(json);
    }

    return 0;
}

static void PrintReport(ToneComparisonResult result)
{
    if (!result.Quality.IsUsable)
    {
        Console.WriteLine("The candidate could not be analysed:");
        foreach (AnalysisWarning warning in result.Quality.Warnings)
        {
            Console.WriteLine($"  [{warning.Code}] {warning.Message}");
        }

        return;
    }

    SimilarityScores s = result.Scores;
    Console.WriteLine($"Overall distance: {s.Overall:F3}   (0 = perfect; similarity {s.Similarity:F2})");
    Console.WriteLine();
    Console.WriteLine($"  Spectral:   {s.Spectral:F3}");
    Console.WriteLine($"  Perceptual: {s.Perceptual:F3}");
    Console.WriteLine($"  Harmonic:   {s.Harmonic:F3}");
    Console.WriteLine($"  Envelope:   {s.Envelope:F3}");
    Console.WriteLine($"  Loudness:   {s.Loudness:F3}");
    Console.WriteLine();
    Console.WriteLine($"Fundamental: {result.Quality.FundamentalHz:F1} Hz (confidence {result.Quality.FundamentalConfidence:F2}); measurement confidence {result.Quality.OverallConfidence:F2}");
    Console.WriteLine();

    Console.WriteLine("Envelope (target -> candidate):");
    PrintStage("Attack", result.Envelope.Attack);
    PrintStage("Decay", result.Envelope.Decay);
    PrintStage("Sustain", result.Envelope.Sustain);
    PrintStage("Release", result.Envelope.Release);
    Console.WriteLine();

    Console.WriteLine("Top differences (candidate relative to target):");
    Console.WriteLine();
    int rank = 1;
    foreach (ToneDifference d in result.Differences.Take(10))
    {
        Console.WriteLine($"{rank++,2}. {Describe(d)}");
        string where = d.FrequencyStartHz is double a && d.FrequencyEndHz is double b
            ? (a == b ? $"{a:F0} Hz: " : $"{FormatHz(a)}-{FormatHz(b)}: ")
            : string.Empty;
        string when = d.TimeRegion is null ? string.Empty : $" during {d.TimeRegion.ToLowerInvariant()}";
        string amount = d.Direction == DifferenceDirection.Different ? "structurally different" : $"{d.SignedDelta:+0.0;-0.0} {d.Unit}";
        Console.WriteLine($"    {where}{amount}{when}   (importance {d.Importance:F2}, confidence {d.Confidence:F2})");
        Console.WriteLine();
    }

    if (result.Differences.Count == 0)
    {
        Console.WriteLine("    none above the reporting thresholds");
        Console.WriteLine();
    }

    if (result.ParameterHints.Count > 0)
    {
        Console.WriteLine("Generic hints (low confidence; measured effects on the actual synth should take priority):");
        foreach (ParameterHint hint in result.ParameterHints.Take(5))
        {
            Console.WriteLine($"  {hint.Feature}: {hint.SuggestedDirection} via {string.Join(", ", hint.PotentialParameterCategories)} (confidence {hint.Confidence:F2})");
        }

        Console.WriteLine();
    }

    foreach (AnalysisWarning warning in result.Quality.Warnings)
    {
        Console.WriteLine($"warning [{warning.Code}] {warning.Message}");
    }
}

static void PrintStage(string name, EnvelopeStageComparison stage)
{
    string Format(double? value) => value is double v ? $"{v:F1} {stage.Unit}" : "not measurable";
    string delta = stage.Difference is double d ? $"{d:+0.0;-0.0} {stage.Unit}" : "n/a";
    Console.WriteLine($"  {name,-8} {Format(stage.Target),16} -> {Format(stage.Candidate),-16} delta {delta,-10} confidence {stage.Confidence:F2}");
}

static string Describe(ToneDifference d)
{
    string feature = d.Feature switch
    {
        "loudness" => "Loudness",
        "pitch" => "Pitch",
        "envelope.attack" => "Attack time",
        "envelope.decay" => "Decay time",
        "envelope.sustain" => "Sustain level",
        "envelope.release" => "Release time",
        "envelope.shape" => "Envelope shape",
        "spectral.centroid" => "Spectral centroid (brightness)",
        "spectral.rolloff" => "Spectral rolloff",
        "spectral.bandwidth" => "Spectral bandwidth",
        "spectral.flatness" => "Spectral flatness (noisiness)",
        "spectral.slope" => "Spectral slope",
        "spectral.flux" => "Spectral flux (movement)",
        "spectral.resonance" => "Resonance",
        "noise.hnr" => "Harmonic-to-noise ratio",
        "harmonic.inharmonicity" => "Inharmonicity",
        "harmonic.slope" => "Harmonic rolloff slope",
        "harmonic.evenodd" => "Even versus odd harmonics",
        "harmonic.low" => "Low harmonics (2-5)",
        "harmonic.high" => "High harmonics (6+)",
        "mel.band" => "Perceptual band energy",
        _ when d.Feature.StartsWith("harmonic.", StringComparison.Ordinal) => $"Harmonic {d.Feature["harmonic.".Length..]}",
        _ when d.Feature.StartsWith("spectral.detail.", StringComparison.Ordinal) => $"Energy in the {d.Feature["spectral.detail.".Length..]} band",
        _ when d.Feature.StartsWith("segment.", StringComparison.Ordinal) => "Brightness in one segment",
        _ => d.Feature,
    };
    return $"{feature}: {d.Direction}";
}

static string FormatHz(double hz) => hz >= 1000 ? $"{hz / 1000:0.#} kHz" : $"{hz:F0} Hz";

/// <summary>Generates an example pair: the candidate is brighter, has a slower attack, a shorter release and a weaker third harmonic.</summary>
internal static class ExampleTones
{
    public const double FundamentalHz = 220.0;
    private const int SampleRate = 44100;

    public static (string Target, string Candidate) Write(string directory)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "target.wav");
        string candidate = Path.Combine(directory, "candidate.wav");
        WavFile.Write(target, Render(1.4, 1.0, 12, 250, 0.35), SampleRate);
        WavFile.Write(candidate, Render(1.0, 0.6, 35, 120, 0.45), SampleRate);
        return (target, candidate);
    }

    private static float[] Render(double rolloff, double thirdHarmonic, double attackMs, double releaseMs, double gain)
    {
        const double HoldMs = 700;
        int length = (int)((HoldMs + releaseMs + 50) * SampleRate / 1000.0);
        var samples = new float[length];
        const int Harmonics = 30;
        var amplitudes = new double[Harmonics];
        double norm = 0;
        for (int k = 0; k < Harmonics; k++)
        {
            amplitudes[k] = Math.Pow(k + 1, -rolloff) * (k == 2 ? thirdHarmonic : 1.0);
            norm += amplitudes[k];
        }

        for (int i = 0; i < length; i++)
        {
            double t = (double)i / SampleRate, ms = t * 1000.0;
            double envelope = Math.Min(1.0, ms / attackMs) * (0.7 + (0.3 * Math.Exp(-ms / 120.0)));
            if (ms > HoldMs)
            {
                envelope *= Math.Exp(-(ms - HoldMs) / (releaseMs / 4.6));
            }

            double value = 0;
            for (int k = 0; k < Harmonics; k++)
            {
                value += amplitudes[k] * Math.Sin(2 * Math.PI * FundamentalHz * (k + 1) * t);
            }

            samples[i] = (float)(gain * envelope * value / norm);
        }

        return samples;
    }
}
