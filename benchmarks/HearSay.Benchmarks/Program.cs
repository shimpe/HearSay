using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Features;
using HearSay.Models;
using HearSay.Optimization.Effects;
using HearSay.Optimization.History;
using HearSay.Optimization.Parameters;
using HearSay.Optimization.Prediction;
using HearSay.Optimization.Sensitivity;
using HearSay.Preprocessing;
using HearSay.Spectral;

// Run with: dotnet run -c Release --project benchmarks/HearSay.Benchmarks -- --filter *
BenchmarkSwitcher.FromAssembly(typeof(ComparisonBenchmarks).Assembly).Run(args);

internal static class Signals
{
    public const int SampleRate = 44100;

    /// <summary>Sawtooth-like note with an ADSR envelope and a little noise; representative of a synth render.</summary>
    public static AudioNote Note(double seconds, double rolloff, double attackMs, int seed)
    {
        int n = (int)(seconds * SampleRate);
        var samples = new float[n];
        var random = new Random(seed);
        const double F0 = 220.0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate, ms = t * 1000.0;
            double envelope = Math.Min(1.0, ms / attackMs) * (0.6 + (0.4 * Math.Exp(-ms / 150.0)));
            double tail = (seconds * 1000.0) - ms;
            if (tail < 200.0)
            {
                envelope *= tail / 200.0;
            }

            double value = 0;
            for (int k = 1; k <= 30; k++)
            {
                value += Math.Pow(k, -rolloff) * Math.Sin(2 * Math.PI * F0 * k * t);
            }

            samples[i] = (float)(0.2 * envelope * (value + (0.01 * (random.NextDouble() - 0.5))));
        }

        return new AudioNote(samples, SampleRate, F0);
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class ComparisonBenchmarks
{
    private readonly ToneComparer comparer = new();
    private readonly AudioPreprocessor preprocessor = new();
    private readonly StftAnalyzer stft = new();
    private readonly ToneComparisonOptions options = new();
    private AudioNote target = null!;
    private AudioNote candidate = null!;
    private PreparedTarget prepared = null!;
    private ToneComparisonResult result = null!;

    [Params(1, 3, 5)]
    public int Seconds { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        target = Signals.Note(Seconds, 1.2, 10, 1);
        candidate = Signals.Note(Seconds, 1.0, 30, 2);
        prepared = comparer.PrepareTarget(target, options);
        result = comparer.Compare(prepared, candidate);
    }

    [Benchmark]
    public PreprocessedAudio Preprocessing() => preprocessor.Process(candidate, options.Preprocessing, Signals.SampleRate, options.ReferenceRmsDb);

    [Benchmark]
    public int StftAnalysis()
    {
        int frames = 0;
        foreach (int size in options.Stft.FftSizes)
        {
            frames += stft.Analyze(candidate.Samples.Span, Signals.SampleRate, size, size / 4).FrameCount;
        }

        return frames;
    }

    [Benchmark]
    public ToneComparisonResult FullComparison() => comparer.Compare(target, candidate, options);

    [Benchmark]
    public ToneComparisonResult PreparedTargetComparison() => comparer.Compare(prepared, candidate);

    [Benchmark]
    public NormalizedFeatureVector DenseFeatureVector() =>
        FeatureSchemaBuilder.ApplyWeights(
            FeatureSchemaBuilder.Normalize(result.FeatureVector, prepared.Schema, options.FeatureVector.NormalizedClip),
            prepared.Schema.DefaultWeights());
}

[MemoryDiagnoser]
[ShortRunJob]
public class OptimizationBenchmarks
{
    private const int ParameterCount = 8;
    private readonly FeatureVectorSchema schema = FeatureSchemaBuilder.Build(new ToneComparisonOptions());
    private SynthParameterSpace space = null!;
    private OptimizationObservation[] observations = null!;
    private LocalLinearEffectModel model = null!;
    private ParameterFeatureJacobian jacobian = null!;
    private SynthParameterState current = null!;
    private SynthParameterState proposed = null!;

    [Params(50, 500)]
    public int Observations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var parameters = new List<SynthParameter>();
        for (int p = 0; p < ParameterCount; p++)
        {
            parameters.Add(new SynthParameter($"p{p}", $"Parameter {p}", 0.5, 0, 1));
        }

        space = new SynthParameterSpace(parameters);
        var random = new Random(42);
        string[] featureIds = [.. schema.Features.Select(f => f.Id)];
        observations = new OptimizationObservation[Observations];
        for (int i = 0; i < observations.Length; i++)
        {
            var values = new Dictionary<string, double>();
            var point = new double[ParameterCount];
            for (int p = 0; p < ParameterCount; p++)
            {
                point[p] = 0.3 + (0.4 * random.NextDouble());
                values[$"p{p}"] = point[p];
            }

            var features = new double[featureIds.Length];
            for (int f = 0; f < features.Length; f++)
            {
                features[f] = (10.0 * point[f % ParameterCount] * point[f % ParameterCount]) + (0.05 * random.NextDouble());
            }

            var scores = new SimilarityScores(1, 1, 1, 1, 1, 1, 0);
            var typed = new ToneDifferenceVector(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
            observations[i] = new OptimizationObservation(
                i, new SynthParameterState(values), scores, typed, new FeatureVector(featureIds, features), DateTimeOffset.UnixEpoch, 1.0);
        }

        model = new LocalLinearEffectModel(space, schema);
        foreach (OptimizationObservation observation in observations)
        {
            model.Observe(observation);
        }

        current = observations[^1].Parameters;
        proposed = current.With("p0", current.Values["p0"] + 0.05).With("p3", current.Values["p3"] - 0.03);
        jacobian = model.EstimateJacobian(current);
    }

    [Benchmark]
    public int EffectModelUpdate()
    {
        var fresh = new LocalLinearEffectModel(space, schema);
        foreach (OptimizationObservation observation in observations)
        {
            fresh.Observe(observation);
        }

        return fresh.ObservationCount;
    }

    [Benchmark]
    public ParameterFeatureJacobian JacobianEstimation() => model.EstimateJacobian(current);

    [Benchmark]
    public ParameterChangePrediction PredictionFromJacobian() => jacobian.Predict(current, proposed, space);
}
